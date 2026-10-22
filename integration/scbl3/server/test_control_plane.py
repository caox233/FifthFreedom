#!/usr/bin/env python3
"""Focused tests for SCBL control-plane snapshots and automatic client versions."""

from __future__ import annotations

import importlib.util
import ipaddress
import json
import os
import socket
import sqlite3
import sys
import tempfile
import threading
import time
from contextlib import closing, contextmanager
from pathlib import Path


def load_module(path: Path):
    spec = importlib.util.spec_from_file_location("scbl_control_plane_v062", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("unable to load control-plane module")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def main() -> None:
    module_path = Path(__file__).with_name("scbl_control_plane.py")
    control_plane = load_module(module_path)
    custom_overlay = ipaddress.ip_network("10.77.4.0/24")
    assert control_plane.is_client_overlay_ip("10.77.4.2", custom_overlay)
    assert not control_plane.is_client_overlay_ip("10.66.0.2", custom_overlay)
    network_capabilities = control_plane.capabilities()
    assert network_capabilities["wssPort"] == int(os.environ.get("SCBL_WSS_PORT", "11010"))
    assert network_capabilities["quicPort"] == int(os.environ.get("SCBL_QUIC_PORT", "11010"))
    assert network_capabilities["wssEnabled"] is True
    assert network_capabilities["quicEnabled"] is True

    with tempfile.TemporaryDirectory(prefix="scbl-control-plane-test-") as tmp:
        db_path = Path(tmp) / "5th-echelon.db"
        with closing(sqlite3.connect(db_path)) as conn:
            conn.executescript(
                """
                CREATE TABLE users (id INTEGER PRIMARY KEY, username TEXT NOT NULL);
                CREATE TABLE game_sessions (
                    id INTEGER PRIMARY KEY,
                    type_id INTEGER NOT NULL,
                    creator_id INTEGER NOT NULL,
                    attributes TEXT NOT NULL,
                    destroyed_at TEXT
                );
                CREATE TABLE participants (game_id INTEGER NOT NULL, user_id INTEGER NOT NULL);
                CREATE TABLE station_urls (user_id INTEGER NOT NULL, url TEXT NOT NULL);
                INSERT INTO users(id, username) VALUES (1003, 'A'), (1006, 'B'), (1008, 'C');

                -- Real multiplayer session. B is the host at 10.66.0.4.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (39, 1, 1006, '', NULL);
                INSERT INTO participants(game_id, user_id) VALUES (39, 1003), (39, 1006);

                -- Newer personal session for A. It must not override session 39.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (40, 1, 1003, '', NULL);
                INSERT INTO participants(game_id, user_id) VALUES (40, 1003);

                -- A solo lobby/personal session for C must not be treated as a room.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (42, 1, 1008, '', NULL);
                INSERT INTO participants(game_id, user_id) VALUES (42, 1008);

                -- A destroyed session must never be considered.
                INSERT INTO game_sessions(id, type_id, creator_id, attributes, destroyed_at)
                    VALUES (41, 1, 1008, '', CURRENT_TIMESTAMP);
                INSERT INTO participants(game_id, user_id) VALUES (41, 1003), (41, 1008);

                INSERT INTO station_urls(user_id, url)
                    VALUES (1003, 'prudps:/address=10.66.0.2;port=13000'),
                           (1006, 'prudps:/address=10.66.0.4;port=13000'),
                           (1008, 'prudps:/address=10.66.0.8;port=13000');
                """
            )

        control_plane.SCBL_ROOT = Path(tmp)
        updates = control_plane.SCBL_ROOT / "client-updates"
        updates.mkdir(parents=True)
        (updates / "client_update_manifest.json").write_text('{"version":"1.0.0"}', encoding="utf-8")
        assert control_plane.required_client_version() == "1.0.0"
        component_channel = updates / "components" / "channels" / "stable"
        component_channel.mkdir(parents=True)
        (component_channel / "client_components_v2.json").write_text(
            '{"components":{"launcher":{"version":"1.0.1"}}}',
            encoding="utf-8",
        )
        assert control_plane.required_client_version() == "1.0.1"
        test_component_channel = updates / "components" / "channels" / "test"
        test_component_channel.mkdir(parents=True)
        (test_component_channel / "client_components_v2.json").write_text(
            '{"components":{"launcher":{"version":"1.0.2"}}}',
            encoding="utf-8",
        )
        control_plane.SERVER_MODE = "test"
        assert control_plane.required_client_version() == "1.0.2"
        control_plane.SERVER_MODE = "stable"
        assert control_plane.required_client_version() == "1.0.1"

        state_defaults = {
            "username": "A",
            "virtual_ip": "10.66.0.2",
            "instance_id": "instance-a",
            "client_version": "2.0.24",
            "easytier_version": "9.8.7",
            "gameplay_mode": "official",
            "gameplay_integrity_valid": True,
            "game_running": False,
            "game_role": "idle",
            "game_peer_ip": "",
            "server_latency_ms": None,
            "server_transport": "udp",
            "server_address_family": "ipv4",
            "game_latency_ms": None,
            "game_transport": "",
            "game_address_family": "",
            "next_hop": "",
            "hop_count": None,
            "game_latency_p50_ms": None,
            "game_latency_p95_ms": None,
            "game_jitter_ms": None,
            "game_loss_percent": None,
        }
        official = control_plane.ClientState(**state_defaults)
        mod = control_plane.ClientState(
            **{**state_defaults, "username": "B", "virtual_ip": "10.66.0.3", "gameplay_mode": "mod"}
        )
        mod_invalid = control_plane.ClientState(
            **{
                **state_defaults,
                "username": "C",
                "virtual_ip": "10.66.0.4",
                "gameplay_mode": "mod",
                "gameplay_integrity_valid": False,
            }
        )
        assert control_plane.gameplay_modes_compatible(official, official, "quick-match")[0] is True
        assert control_plane.gameplay_modes_compatible(official, mod, "private-room")[0] is False
        assert control_plane.gameplay_modes_compatible(official, mod, "lobby-party")[0] is False
        assert control_plane.gameplay_modes_compatible(official, mod, "coop")[0] is False
        assert control_plane.gameplay_modes_compatible(mod, mod_invalid, "quick-match")[0] is False
        assert control_plane.normalize_gameplay_mode("official") == "official"
        assert control_plane.normalize_gameplay_mode("mod") == "mod"
        for legacy_mode in ("casual", "serious", ""):
            try:
                control_plane.normalize_gameplay_mode(legacy_mode)
            except ValueError:
                pass
            else:
                raise AssertionError(f"legacy gameplay mode was accepted: {legacy_mode!r}")
        mode_snapshot = control_plane.gameplay_mode_snapshot_payload(
            [
                control_plane.GameplayModeAssertion.from_client(state)
                for state in (official, mod, mod_invalid)
            ]
        )
        assert mode_snapshot["schemaVersion"] == 1
        assert mode_snapshot["ttlSeconds"] == control_plane.MODE_ASSERTION_TTL_SECONDS
        assert mode_snapshot["players"]["a"]["gameplayMode"] == "official"
        assert mode_snapshot["players"]["b"]["gameplayMode"] == "mod"
        assert mode_snapshot["players"]["c"]["gameplayIntegrityValid"] is False

        # Online topology expires quickly, while the last authenticated mode
        # assertion survives a bounded network reset. Unknown or explicitly
        # invalid state can still revoke the assertion immediately.
        fake_now = [1_000_000]
        retained_state = control_plane.RuntimeState(
            heartbeat_ttl_seconds=20,
            mode_assertion_ttl_seconds=90,
            clock=lambda: fake_now[0],
        )
        trusted_official = control_plane.ClientState(
            **{
                **state_defaults,
                "identity_trusted": True,
                "user_id": 1003,
                "last_seen_ms": fake_now[0],
            }
        )
        retained_state.upsert(trusted_official)
        fake_now[0] += 21_000
        assert retained_state.active_clients() == []
        retained_assertion = retained_state.mode_assertion_by_virtual_ip(
            trusted_official.virtual_ip
        )
        assert retained_assertion is not None
        assert retained_assertion.gameplay_mode == "official"
        fake_now[0] += 70_000
        assert retained_state.active_mode_assertions() == []

        retained_state.upsert(
            control_plane.ClientState(
                **{
                    **state_defaults,
                    "identity_trusted": True,
                    "user_id": 1003,
                    "last_seen_ms": fake_now[0],
                }
            )
        )
        retained_state.invalidate_mode_assertion("10.66.0.2", "A")
        assert retained_state.active_mode_assertions() == []

        # Request threads only mark the snapshot dirty. One background writer
        # must coalesce a burst and remain the only thread touching the file.
        snapshot_path = Path(tmp) / "runtime" / "gameplay-modes.json"
        original_snapshot_path = control_plane.GAMEPLAY_MODE_STATE_PATH
        original_state = control_plane.STATE
        original_write = control_plane._write_gameplay_mode_snapshot
        control_plane.GAMEPLAY_MODE_STATE_PATH = snapshot_path
        control_plane.STATE = control_plane.RuntimeState()
        control_plane.STATE.upsert(
            control_plane.ClientState(
                **{
                    **state_defaults,
                    "identity_trusted": True,
                    "user_id": 1003,
                }
            )
        )
        first_write_started = threading.Event()
        release_first_write = threading.Event()
        write_lock = threading.Lock()
        writer_threads: list[str] = []

        def observed_write(payload):
            with write_lock:
                writer_threads.append(threading.current_thread().name)
                write_number = len(writer_threads)
            if write_number == 1:
                first_write_started.set()
                assert release_first_write.wait(timeout=5)
            original_write(payload)

        control_plane._write_gameplay_mode_snapshot = observed_write
        dirty_count = 64
        try:
            control_plane.start_gameplay_mode_snapshot_writer()
            assert first_write_started.wait(timeout=5)
            markers = [
                threading.Thread(target=control_plane.mark_gameplay_mode_snapshot_dirty)
                for _ in range(dirty_count)
            ]
            for marker in markers:
                marker.start()
            for marker in markers:
                marker.join(timeout=2)
            assert all(not marker.is_alive() for marker in markers)
            release_first_write.set()
            assert control_plane.publish_gameplay_mode_snapshot(timeout=5)
            assert control_plane.stop_gameplay_mode_snapshot_writer(timeout=5)
        finally:
            release_first_write.set()
            control_plane.stop_gameplay_mode_snapshot_writer(timeout=5)
            control_plane._write_gameplay_mode_snapshot = original_write
            control_plane.STATE = original_state
            control_plane.GAMEPLAY_MODE_STATE_PATH = original_snapshot_path

        assert writer_threads
        assert set(writer_threads) == {"scbl-gameplay-mode-snapshot"}
        assert len(writer_threads) < dirty_count
        published_snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))
        assert published_snapshot["schemaVersion"] == 1
        assert published_snapshot["players"]["a"]["gameplayMode"] == "official"
        assert list(snapshot_path.parent.glob(".gameplay-modes.json.*.tmp")) == []
        assert control_plane.client_version_accepted("1.0.1", "1.0.1") is True
        assert control_plane.client_version_accepted("1.0.0", "1.0.1") is False
        assert control_plane.client_version_accepted("1.0.2", "1.0.1") is False
        assert control_plane.required_json_bool({"value": False}, "value") is False
        assert control_plane.required_json_bool({"value": True}, "value") is True
        for invalid_bool in (None, 0, 1, "false", "true", ""):
            try:
                control_plane.required_json_bool({"value": invalid_bool}, "value")
            except ValueError:
                pass
            else:
                raise AssertionError(f"non-boolean JSON value was accepted: {invalid_bool!r}")

        control_plane.DB_PATH = db_path

        # Authentication identity reads may briefly overlap a Dedicated write.
        # Retry only SQLITE_BUSY/SQLITE_LOCKED; schema and other operational
        # failures must remain fail-closed without being hidden by retries.
        original_readonly_database = control_plane.readonly_database
        transient_attempts = 0

        @contextmanager
        def transient_readonly_database():
            nonlocal transient_attempts
            transient_attempts += 1
            if transient_attempts < 3:
                raise sqlite3.OperationalError("database is locked")

            class IdentityConnection:
                @staticmethod
                def execute(_query, _parameters):
                    class IdentityCursor:
                        @staticmethod
                        def fetchone():
                            return ("A", 7)

                    return IdentityCursor()

            yield IdentityConnection()

        control_plane.readonly_database = transient_readonly_database
        try:
            assert control_plane._read_authenticated_identity_row(1003) == ("A", 7)
        finally:
            control_plane.readonly_database = original_readonly_database
        assert transient_attempts == 3

        non_transient_attempts = 0

        @contextmanager
        def invalid_schema_database():
            nonlocal non_transient_attempts
            non_transient_attempts += 1
            raise sqlite3.OperationalError("no such column: auth_version")
            yield  # pragma: no cover - required to keep this a context manager

        control_plane.readonly_database = invalid_schema_database
        try:
            try:
                control_plane._read_authenticated_identity_row(1003)
            except sqlite3.OperationalError as error:
                assert "no such column" in str(error)
            else:
                raise AssertionError("non-transient SQLite errors must fail closed")
        finally:
            control_plane.readonly_database = original_readonly_database
        assert non_transient_attempts == 1

        with control_plane._SESSION_LOCK:
            control_plane._SESSION_CACHE = {}
            control_plane._SESSION_SNAPSHOT_AT_MS = 0
            control_plane._SESSION_SNAPSHOT_ERROR = ""

        # sqlite3.Connection.__enter__ only starts a transaction; __exit__ does
        # not close the OS file handle. Track the wrapper directly so the
        # long-running refresh loops cannot silently regress into leaking one
        # descriptor per poll.
        real_connect = control_plane.sqlite3.connect
        opened_connections = 0
        closed_connections = 0

        class TrackedConnection:
            def __init__(self, inner):
                self._inner = inner
                self._closed = False

            def __getattr__(self, name):
                return getattr(self._inner, name)

            def close(self):
                nonlocal closed_connections
                if not self._closed:
                    self._closed = True
                    closed_connections += 1
                self._inner.close()

        def tracked_connect(*args, **kwargs):
            nonlocal opened_connections
            opened_connections += 1
            return TrackedConnection(real_connect(*args, **kwargs))

        control_plane.sqlite3.connect = tracked_connect
        try:
            for _ in range(25):
                assert control_plane._load_authoritative_sessions()
                assert control_plane.database_health() == (True, 3)
                assert control_plane.account_exists("A") is True
                assert control_plane.account_exists("missing") is False
        finally:
            control_plane.sqlite3.connect = real_connect

        assert opened_connections == 100
        assert closed_connections == opened_connections

        assert control_plane.refresh_game_session_snapshot() is True
        a = control_plane.game_session_payload("10.66.0.2")
        b = control_plane.game_session_payload("10.66.0.4")
        c = control_plane.game_session_payload("10.66.0.8")
        unrelated = control_plane.game_session_payload("10.66.0.9")

        assert a["active"] is True
        assert a["sessionId"] == 39
        assert a["hostVirtualIp"] == "10.66.0.4"
        assert a["participantCount"] == 2
        assert a["requesterIsHost"] is False
        assert b["requesterIsHost"] is True
        assert c["active"] is False
        assert c["sessionId"] is None
        assert c["participantCount"] == 0
        assert c["requesterIsHost"] is False
        assert "10.66.0.8" not in control_plane.authoritative_sessions_by_ip()
        assert unrelated["active"] is False
        assert unrelated["hostVirtualIp"] == ""

        # Exercise the real request handler. The signed internal probe must complete,
        # and each HTTP/1.1 response must explicitly close instead of holding an idle
        # handler thread until a socket timeout.
        control_plane.SERVER_IP = "127.0.0.1"
        control_plane.SECRET = b"control-plane-test-secret"
        control_plane.ALLOW_LOOPBACK = True
        server = control_plane.ScblThreadingHTTPServer(("127.0.0.1", 0), control_plane.Handler)
        control_plane.CONTROL_PORT = server.server_address[1]
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        thread.start()
        try:
            deadline = time.time() + 2.0
            while time.time() < deadline and not control_plane._signed_health_probe(timeout=0.4):
                time.sleep(0.02)
            assert control_plane._signed_health_probe(timeout=0.8) is True

            timestamp = str(int(time.time()))
            path = "/v1/health"
            signature = control_plane.expected_signature(timestamp, "GET", path, b"")
            request = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{control_plane.CONTROL_PORT}\r\n"
                f"X-SCBL-Timestamp: {timestamp}\r\n"
                f"X-SCBL-Signature: {signature}\r\n\r\n"
            ).encode("ascii")
            with socket.create_connection(("127.0.0.1", control_plane.CONTROL_PORT), timeout=1.0) as client:
                client.sendall(request)
                client.settimeout(1.0)
                response = bytearray()
                while True:
                    chunk = client.recv(4096)
                    if not chunk:
                        break
                    response.extend(chunk)
            assert b"HTTP/1.1 200" in response
            assert b"Connection: close" in response

            bad_request = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{control_plane.CONTROL_PORT}\r\n"
                f"X-SCBL-Timestamp: {timestamp}\r\n"
                "X-SCBL-Signature: 00\r\nConnection: close\r\n\r\n"
            ).encode("ascii")
            with socket.create_connection(("127.0.0.1", control_plane.CONTROL_PORT), timeout=1.0) as client:
                client.sendall(bad_request)
                client.settimeout(1.0)
                bad_response = bytearray()
                while True:
                    chunk = client.recv(4096)
                    if not chunk:
                        break
                    bad_response.extend(chunk)
            assert b"HTTP/1.1 401" in bad_response
            assert b'"reason":"invalid_signature"' in bad_response
            assert b'"serverTimeUnixMs":' in bad_response

            stale_timestamp = str(int(time.time()) - control_plane.MAX_CLOCK_SKEW_SECONDS - 10)
            stale_signature = control_plane.expected_signature(stale_timestamp, "GET", path, b"")
            stale_request = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{control_plane.CONTROL_PORT}\r\n"
                f"X-SCBL-Timestamp: {stale_timestamp}\r\n"
                f"X-SCBL-Signature: {stale_signature}\r\nConnection: close\r\n\r\n"
            ).encode("ascii")
            with socket.create_connection(("127.0.0.1", control_plane.CONTROL_PORT), timeout=1.0) as client:
                client.sendall(stale_request)
                client.settimeout(1.0)
                stale_response = bytearray()
                while True:
                    chunk = client.recv(4096)
                    if not chunk:
                        break
                    stale_response.extend(chunk)
            assert b'"reason":"clock_skew"' in stale_response
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=1.0)

        print("control-plane multiplayer evidence and HTTP lifecycle tests passed")


if __name__ == "__main__":
    main()
