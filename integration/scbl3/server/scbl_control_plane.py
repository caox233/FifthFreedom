#!/usr/bin/env python3
"""SCBL Public sidecar control plane.

The sidecar exposes signed overlay-only JSON APIs. Slow system and SQLite
sampling is performed by background workers so HTTP request threads only read
immutable in-memory snapshots.
"""
from __future__ import annotations

import hashlib
import hmac
import ipaddress
import json
import os
import re
import signal
import socket
import sqlite3
import tempfile
import threading
import time
from contextlib import contextmanager
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlsplit

from control_plane_domain import (
    ClientState,
    GameplayModeAssertion,
    RuntimeState,
    clean_float,
    clean_int,
    clean_text,
    gameplay_modes_compatible,
    is_client_overlay_ip,
    normalize_gameplay_mode,
    utc_ms,
)
from control_plane_auth import (
    AuthenticatedIdentity,
    ControlAuthError,
    validate_control_auth_token as validate_auth_token,
)
from control_plane_gameplay import GameplaySnapshotPublisher
from control_plane_runtime import (
    AuthoritativeGameSession,
    account_exists as runtime_account_exists,
    capabilities as runtime_capabilities,
    compute_health as runtime_compute_health,
    database_health as runtime_database_health,
    decorate_clients as runtime_decorate_clients,
    game_session_payload as runtime_game_session_payload,
    load_authoritative_sessions,
    read_authenticated_identity_row,
    topology_summary,
)

SERVER_IP = os.environ.get("SCBL_SERVER_IP", "10.66.0.1").strip()
CONTROL_PORT = int(os.environ.get("SCBL_CONTROL_PORT", "19080"))
SECRET = os.environ.get("SCBL_SECRET", "").encode("utf-8")
_AUTH_TOKEN_KEY_HEX = os.environ.get("SCBL_AUTH_TOKEN_KEY", "").strip()
if _AUTH_TOKEN_KEY_HEX and not re.fullmatch(r"[0-9A-Fa-f]{64}", _AUTH_TOKEN_KEY_HEX):
    raise ValueError("SCBL_AUTH_TOKEN_KEY must contain exactly 64 hexadecimal characters")
AUTH_TOKEN_KEY = bytes.fromhex(_AUTH_TOKEN_KEY_HEX) if _AUTH_TOKEN_KEY_HEX else b""
CONTROL_AUTH_POLICY = os.environ.get("SCBL_CONTROL_AUTH_POLICY", "transition").strip()
if CONTROL_AUTH_POLICY not in {"transition", "required"}:
    raise ValueError("SCBL_CONTROL_AUTH_POLICY must be transition or required")
SCBL_ROOT = Path(os.environ.get("SCBL_ROOT", "/opt/scbl-public"))
DB_PATH = Path(os.environ.get("SCBL_DB_PATH", str(SCBL_ROOT / "server" / "5th-echelon.db")))
GAMEPLAY_MODE_STATE_PATH = Path(
    os.environ.get("SCBL_GAMEPLAY_MODE_STATE_PATH", str(SCBL_ROOT / "runtime" / "gameplay-modes.json"))
)
SERVER_TOOL_VERSION = os.environ.get("SCBL_SERVER_TOOL_VERSION", "0.0.0").strip()
MAINTENANCE = os.environ.get("SCBL_MAINTENANCE", "n").strip().lower() in {"1", "y", "yes", "true", "on"}
SERVER_MODE = os.environ.get("SCBL_SERVER_MODE", "stable").strip().lower()
if SERVER_MODE not in {"stable", "test"}:
    raise ValueError("SCBL_SERVER_MODE must be stable or test")
HEARTBEAT_TTL_SECONDS = max(10, int(os.environ.get("SCBL_HEARTBEAT_TTL", "20")))
MODE_ASSERTION_TTL_SECONDS = max(
    HEARTBEAT_TTL_SECONDS,
    int(os.environ.get("SCBL_GAMEPLAY_MODE_ASSERTION_TTL", "90")),
)
MAX_BODY_BYTES = 32 * 1024
MAX_CLOCK_SKEW_SECONDS = 90
OVERLAY = ipaddress.ip_network(
    os.environ.get("SCBL_VIRTUAL_NET", "10.66.0.0/24"), strict=False
)
if OVERLAY.version != 4:
    raise ValueError("SCBL_VIRTUAL_NET must be an IPv4 network")
ALLOW_LOOPBACK = os.environ.get("SCBL_ALLOW_LOOPBACK", "n").strip().lower() in {"1", "y", "yes", "true", "on"}
SESSION_REFRESH_SECONDS = 2.0
HEALTH_REFRESH_SECONDS = 5.0
HTTP_REQUEST_TIMEOUT_SECONDS = 5.0
SELF_WATCHDOG_INTERVAL_SECONDS = 15.0
SELF_WATCHDOG_FAILURE_THRESHOLD = 2
GAMEPLAY_MODE_SNAPSHOT_REFRESH_SECONDS = 2.0
GAMEPLAY_MODE_SNAPSHOT_FLUSH_TIMEOUT_SECONDS = 3.0
AUTH_TOKEN_VERSION = "v1"
MIN_AUTH_TOKEN_TTL_SECONDS = 5 * 60
MAX_AUTH_TOKEN_TTL_SECONDS = 7 * 24 * 60 * 60
MAX_AUTH_TOKEN_FUTURE_SKEW_SECONDS = 60
AUTH_IDENTITY_DATABASE_ATTEMPTS = 3
AUTH_IDENTITY_DATABASE_RETRY_SECONDS = 0.04
_CONTROL_PLANE_EXIT_CODE = 0



def required_client_version() -> str:
    # Each SCBL 3.0 server instance serves exactly one client channel. The
    # configured server identity selects its own manifest; a client cannot ask
    # the same instance to apply the other channel's policy.
    #
    # The full-package manifest remains the recovery/install baseline. Keeping
    # these two release lines separate prevents a Launcher-only component patch
    # from forcing every unchanged client file through the full updater.
    manifests = (
        SCBL_ROOT
        / "client-updates"
        / "components"
        / "channels"
        / SERVER_MODE
        / "client_components_v2.json",
        SCBL_ROOT / "client-updates" / "client_update_manifest.json",
    )
    for manifest in manifests:
        try:
            data = json.loads(manifest.read_text(encoding="utf-8-sig"))
            if manifest.name == "client_components_v2.json":
                value = clean_text(
                    ((data.get("components") or {}).get("launcher") or {}).get("version"),
                    32,
                ).lstrip("vV")
            else:
                value = clean_text(data.get("version"), 32).lstrip("vV")
        except Exception:
            continue
        if re.fullmatch(r"\d+\.\d+\.\d+", value) and value != "0.0.0":
            return value
    return ""


def version_tuple(value: str) -> tuple[int, int, int]:
    parts = clean_text(value, 32).lstrip("vV").split(".")
    if len(parts) != 3:
        return (0, 0, 0)
    try:
        return tuple(int(x) for x in parts)  # type: ignore[return-value]
    except ValueError:
        return (0, 0, 0)


def client_version_accepted(client_version: str, required_version: str) -> bool:
    required = version_tuple(required_version)
    current = version_tuple(client_version)
    if required == (0, 0, 0) or current == (0, 0, 0):
        return False
    return current == required


def required_json_bool(payload: dict[str, Any], name: str) -> bool:
    value = payload.get(name)
    if type(value) is not bool:
        raise ValueError(f"{name} must be a JSON boolean")
    return value


STATE = RuntimeState(
    HEARTBEAT_TTL_SECONDS,
    mode_assertion_ttl_seconds=MODE_ASSERTION_TTL_SECONDS,
)


def gameplay_mode_snapshot_payload(
    assertions: list[GameplayModeAssertion],
) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "generatedAtUnixMs": utc_ms(),
        "ttlSeconds": MODE_ASSERTION_TTL_SECONDS,
        "players": {
            state.username.casefold(): {
                "username": state.username,
                "userId": state.user_id,
                "identityTrusted": state.identity_trusted,
                "gameplayMode": state.gameplay_mode,
                "gameplayIntegrityValid": state.gameplay_integrity_valid,
                "lastSeenUnixMs": state.last_seen_ms,
            }
            for state in assertions
            if state.username
        },
    }


def _write_gameplay_mode_snapshot(payload: dict[str, Any]) -> None:
    GAMEPLAY_MODE_STATE_PATH.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{GAMEPLAY_MODE_STATE_PATH.name}.",
        suffix=".tmp",
        dir=GAMEPLAY_MODE_STATE_PATH.parent,
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, ensure_ascii=False, separators=(",", ":"))
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, 0o640)
        os.replace(temporary, GAMEPLAY_MODE_STATE_PATH)
    finally:
        temporary.unlink(missing_ok=True)


def _publish_current_gameplay_snapshot() -> None:
    _write_gameplay_mode_snapshot(
        gameplay_mode_snapshot_payload(STATE.active_mode_assertions())
    )


_GAMEPLAY_MODE_SNAPSHOT_PUBLISHER = GameplaySnapshotPublisher(
    _publish_current_gameplay_snapshot,
    refresh_seconds=GAMEPLAY_MODE_SNAPSHOT_REFRESH_SECONDS,
)


def mark_gameplay_mode_snapshot_dirty() -> int:
    return _GAMEPLAY_MODE_SNAPSHOT_PUBLISHER.mark_dirty()


def start_gameplay_mode_snapshot_writer() -> None:
    _GAMEPLAY_MODE_SNAPSHOT_PUBLISHER.start()


def publish_gameplay_mode_snapshot(
    timeout: float = GAMEPLAY_MODE_SNAPSHOT_FLUSH_TIMEOUT_SECONDS,
) -> bool:
    """Request one snapshot generation and wait for the sole writer to publish it."""
    start_gameplay_mode_snapshot_writer()
    return _GAMEPLAY_MODE_SNAPSHOT_PUBLISHER.publish(timeout)


def stop_gameplay_mode_snapshot_writer(
    timeout: float = GAMEPLAY_MODE_SNAPSHOT_FLUSH_TIMEOUT_SECONDS,
) -> bool:
    return _GAMEPLAY_MODE_SNAPSHOT_PUBLISHER.stop(timeout)


_SESSION_LOCK = threading.RLock()
_SESSION_CACHE: dict[str, AuthoritativeGameSession] = {}
_SESSION_SNAPSHOT_AT_MS = 0
_SESSION_SNAPSHOT_ERROR = ""
_HEALTH_LOCK = threading.RLock()
_HEALTH_CACHE: dict[str, Any] = {}
_HEALTH_SNAPSHOT_AT_MS = 0
_HEALTH_SNAPSHOT_ERROR = ""
@contextmanager
def readonly_database():
    """Open the dedicated-server database and always release its file handle."""
    conn = sqlite3.connect(f"file:{DB_PATH}?mode=ro", uri=True, timeout=0.35)
    try:
        conn.execute("PRAGMA query_only=ON")
        conn.execute("PRAGMA busy_timeout=350")
        yield conn
    finally:
        conn.close()


def _read_authenticated_identity_row(user_id: int) -> tuple[Any, ...] | None:
    return read_authenticated_identity_row(
        user_id,
        readonly_database,
        attempts=AUTH_IDENTITY_DATABASE_ATTEMPTS,
        retry_seconds=AUTH_IDENTITY_DATABASE_RETRY_SECONDS,
    )


def validate_control_auth_token(
    token: str,
    source_ip: str,
    *,
    now: int | None = None,
) -> AuthenticatedIdentity:
    return validate_auth_token(
        token,
        source_ip,
        key=AUTH_TOKEN_KEY,
        identity_loader=_read_authenticated_identity_row,
        token_version=AUTH_TOKEN_VERSION,
        min_ttl_seconds=MIN_AUTH_TOKEN_TTL_SECONDS,
        max_ttl_seconds=MAX_AUTH_TOKEN_TTL_SECONDS,
        max_future_skew_seconds=MAX_AUTH_TOKEN_FUTURE_SKEW_SECONDS,
        now=now,
    )


def _load_authoritative_sessions() -> dict[str, AuthoritativeGameSession]:
    return load_authoritative_sessions(DB_PATH.exists(), readonly_database)


def refresh_game_session_snapshot() -> bool:
    global _SESSION_CACHE, _SESSION_SNAPSHOT_AT_MS, _SESSION_SNAPSHOT_ERROR
    try:
        snapshot = _load_authoritative_sessions()
    except Exception as exc:
        with _SESSION_LOCK:
            _SESSION_SNAPSHOT_ERROR = clean_text(exc, 256)
        return False
    with _SESSION_LOCK:
        _SESSION_CACHE = snapshot
        _SESSION_SNAPSHOT_AT_MS = utc_ms()
        _SESSION_SNAPSHOT_ERROR = ""
    return True


def _session_refresh_loop() -> None:
    while True:
        refresh_game_session_snapshot()
        time.sleep(SESSION_REFRESH_SECONDS)


def authoritative_sessions_by_ip() -> dict[str, AuthoritativeGameSession]:
    with _SESSION_LOCK:
        return dict(_SESSION_CACHE)


def session_snapshot_metadata() -> tuple[int | None, str]:
    with _SESSION_LOCK:
        age = utc_ms() - _SESSION_SNAPSHOT_AT_MS if _SESSION_SNAPSHOT_AT_MS else None
        return age, _SESSION_SNAPSHOT_ERROR


def game_session_payload(source_ip: str) -> dict[str, Any]:
    snapshot_age, snapshot_error = session_snapshot_metadata()
    return runtime_game_session_payload(
        source_ip,
        authoritative_sessions_by_ip(),
        snapshot_age,
        snapshot_error,
    )


def decorated_clients(clients: list[ClientState]) -> list[dict[str, Any]]:
    return runtime_decorate_clients(clients, authoritative_sessions_by_ip())


def database_health() -> tuple[bool, int | None]:
    return runtime_database_health(DB_PATH.exists(), readonly_database)


def account_exists(username: str) -> bool | None:
    return runtime_account_exists(username, DB_PATH.exists(), readonly_database)


def _compute_health() -> dict[str, Any]:
    return runtime_compute_health(SERVER_IP, database_health())


def refresh_health_snapshot() -> bool:
    global _HEALTH_CACHE, _HEALTH_SNAPSHOT_AT_MS, _HEALTH_SNAPSHOT_ERROR
    try:
        health = _compute_health()
    except Exception as exc:
        with _HEALTH_LOCK:
            _HEALTH_SNAPSHOT_ERROR = clean_text(exc, 256)
        return False
    with _HEALTH_LOCK:
        _HEALTH_CACHE = health
        _HEALTH_SNAPSHOT_AT_MS = utc_ms()
        _HEALTH_SNAPSHOT_ERROR = ""
    return True


def _health_refresh_loop() -> None:
    while True:
        refresh_health_snapshot()
        time.sleep(HEALTH_REFRESH_SECONDS)


def get_health() -> dict[str, Any]:
    with _HEALTH_LOCK:
        health = dict(_HEALTH_CACHE)
        snapshot_at = _HEALTH_SNAPSHOT_AT_MS
        error = _HEALTH_SNAPSHOT_ERROR
    if not health:
        refresh_health_snapshot()
        with _HEALTH_LOCK:
            health = dict(_HEALTH_CACHE)
            snapshot_at = _HEALTH_SNAPSHOT_AT_MS
            error = _HEALTH_SNAPSHOT_ERROR
    age = utc_ms() - snapshot_at if snapshot_at else None
    health["snapshotAgeMs"] = age
    health["stale"] = age is None or age > int(HEALTH_REFRESH_SECONDS * 3000)
    health["snapshotError"] = error
    return health


def capabilities() -> dict[str, Any]:
    return runtime_capabilities(SERVER_IP, CONTROL_PORT)


def expected_signature(timestamp: str, method: str, path_with_query: str, body: bytes) -> str:
    message = timestamp.encode("ascii") + b"\n" + method.upper().encode("ascii") + b"\n" + path_with_query.encode("utf-8") + b"\n" + body
    return hmac.new(SECRET, message, hashlib.sha256).hexdigest()


class RequestMetrics:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.samples: dict[str, list[int]] = {}
        self.write_errors = 0

    def record(self, endpoint: str, duration_ms: int, write_error: bool = False) -> None:
        with self.lock:
            values = self.samples.setdefault(endpoint, [])
            values.append(max(0, duration_ms))
            if len(values) > 512:
                del values[: len(values) - 512]
            if write_error:
                self.write_errors += 1

    def drain_summary(self) -> tuple[dict[str, tuple[int, int, int, int]], int]:
        with self.lock:
            data = self.samples
            self.samples = {}
            errors = self.write_errors
            self.write_errors = 0
        summary: dict[str, tuple[int, int, int, int]] = {}
        for endpoint, values in data.items():
            ordered = sorted(values)
            if not ordered:
                continue
            p50 = ordered[(len(ordered) - 1) * 50 // 100]
            p95 = ordered[(len(ordered) - 1) * 95 // 100]
            summary[endpoint] = (len(ordered), p50, p95, ordered[-1])
        return summary, errors


METRICS = RequestMetrics()
_AUTH_LOG_LOCK = threading.Lock()
_AUTH_LOG_LAST_MS: dict[tuple[str, str, str], int] = {}
_AUTH_LOG_SUPPRESSED: dict[tuple[str, str, str], int] = {}


def _metrics_loop() -> None:
    while True:
        time.sleep(30)
        summary, write_errors = METRICS.drain_summary()
        if not summary and not write_errors:
            continue
        text = ", ".join(
            f"{endpoint}:n={values[0]},p50={values[1]}ms,p95={values[2]}ms,max={values[3]}ms"
            for endpoint, values in sorted(summary.items())
        )
        print(f"control-plane metrics: {text}; response-write-errors={write_errors}", flush=True)




def _signed_health_probe(timeout: float = 2.0) -> bool:
    timestamp = str(int(time.time()))
    path = "/v1/health"
    signature = expected_signature(timestamp, "GET", path, b"")
    request = (
        f"GET {path} HTTP/1.1\r\n"
        f"Host: {SERVER_IP}:{CONTROL_PORT}\r\n"
        f"X-SCBL-Timestamp: {timestamp}\r\n"
        f"X-SCBL-Signature: {signature}\r\n"
        "Connection: close\r\n\r\n"
    ).encode("ascii")
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.settimeout(timeout)
        # Guarantee that the overlay-only source check sees the fixed server IP.
        sock.bind((SERVER_IP, 0))
        sock.connect((SERVER_IP, CONTROL_PORT))
        sock.sendall(request)
        response = bytearray()
        while len(response) < 16 * 1024:
            chunk = sock.recv(4096)
            if not chunk:
                break
            response.extend(chunk)
        first_line = bytes(response).split(b"\r\n", 1)[0]
        return b" 200 " in first_line
    except OSError:
        return False
    finally:
        sock.close()


def _listener_watchdog_loop() -> None:
    global _CONTROL_PLANE_EXIT_CODE
    failures = 0
    while True:
        time.sleep(SELF_WATCHDOG_INTERVAL_SECONDS)
        if _signed_health_probe():
            if failures:
                print(f"control-plane self-watchdog recovered after {failures} failure(s)", flush=True)
            failures = 0
            continue
        failures += 1
        print(
            f"control-plane self-watchdog failed {failures}/{SELF_WATCHDOG_FAILURE_THRESHOLD}: "
            f"signed health request to {SERVER_IP}:{CONTROL_PORT} did not complete",
            flush=True,
        )
        if failures >= SELF_WATCHDOG_FAILURE_THRESHOLD:
            print("control-plane listener is unhealthy; exiting for systemd restart", flush=True)
            _CONTROL_PLANE_EXIT_CODE = 70
            os.kill(os.getpid(), signal.SIGTERM)
            return

class ScblThreadingHTTPServer(ThreadingHTTPServer):
    request_queue_size = 128
    daemon_threads = True
    block_on_close = False
    allow_reuse_address = True


class Handler(BaseHTTPRequestHandler):
    server_version = f"SCBLControlPlane/{SERVER_TOOL_VERSION}"
    protocol_version = "HTTP/1.1"

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(HTTP_REQUEST_TIMEOUT_SECONDS)

    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"{self.address_string()} - {fmt % args}", flush=True)

    def log_error(self, fmt: str, *args: Any) -> None:
        # Previous keep-alive handling logged an idle timeout for every /v1/peers
        # connection. Responses now close explicitly, so suppress only stale
        # timeout noise while preserving all other handler errors.
        if fmt.startswith("Request timed out"):
            return
        super().log_error(fmt, *args)

    def _begin_request(self) -> None:
        self._request_started = time.monotonic()
        self._request_recorded = False

    def send_json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        write_error = False
        try:
            self.send_response(status.value)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(data)
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, OSError):
            write_error = True
        finally:
            self.close_connection = True
            if not getattr(self, "_request_recorded", False):
                self._request_recorded = True
                started = getattr(self, "_request_started", time.monotonic())
                endpoint = urlsplit(self.path).path
                METRICS.record(endpoint, int((time.monotonic() - started) * 1000), write_error)

    def read_body(self) -> bytes | None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            return None
        if length < 0 or length > MAX_BODY_BYTES:
            return None
        return self.rfile.read(length) if length else b""

    def authorization_failure_reason(self, body: bytes) -> str:
        if not SECRET:
            return "server_secret_unavailable"
        timestamp = self.headers.get("X-SCBL-Timestamp", "")
        signature = self.headers.get("X-SCBL-Signature", "")
        if not timestamp:
            return "missing_timestamp"
        try:
            ts = int(timestamp)
        except ValueError:
            return "invalid_timestamp"
        if abs(int(time.time()) - ts) > MAX_CLOCK_SKEW_SECONDS:
            return "clock_skew"
        if not signature:
            return "missing_signature"
        expected = expected_signature(timestamp, self.command, self.path, body)
        return "" if hmac.compare_digest(expected, signature.lower()) else "invalid_signature"

    def log_auth_rejection(self, reason: str) -> None:
        source = self.client_address[0]
        endpoint = urlsplit(self.path).path
        key = (source, endpoint, reason)
        now = utc_ms()
        with _AUTH_LOG_LOCK:
            last = _AUTH_LOG_LAST_MS.get(key, 0)
            if now - last < 30_000:
                _AUTH_LOG_SUPPRESSED[key] = _AUTH_LOG_SUPPRESSED.get(key, 0) + 1
                return
            suppressed = _AUTH_LOG_SUPPRESSED.pop(key, 0)
            _AUTH_LOG_LAST_MS[key] = now
        suffix = f" suppressed={suppressed}" if suppressed else ""
        print(
            f"control-plane auth rejected source={source} method={self.command} "
            f"path={endpoint} reason={reason}{suffix}",
            flush=True,
        )

    def require_overlay_source(self, payload_ip: str | None = None) -> bool:
        source_ip = self.client_address[0]
        try:
            source = ipaddress.ip_address(source_ip)
        except ValueError:
            return False
        if source not in OVERLAY and not (ALLOW_LOOPBACK and source.is_loopback):
            return False
        if payload_ip and source_ip != payload_ip:
            return bool(ALLOW_LOOPBACK and source.is_loopback)
        return True

    def resolve_authenticated_identity(self) -> tuple[bool, AuthenticatedIdentity | None]:
        token = self.headers.get("X-SCBL-Auth-Token", "")
        if not token:
            if CONTROL_AUTH_POLICY == "transition":
                return True, None
            reason = "missing_auth_token"
        else:
            try:
                return True, validate_control_auth_token(token, self.client_address[0])
            except ControlAuthError as exc:
                reason = exc.reason
        self.log_auth_rejection(reason)
        self.send_json(
            HTTPStatus.UNAUTHORIZED,
            {"error": "unauthorized", "reason": reason, "serverTimeUnixMs": utc_ms()},
        )
        return False, None

    def do_GET(self) -> None:  # noqa: N802
        self._begin_request()
        body = b""
        auth_reason = self.authorization_failure_reason(body)
        if auth_reason:
            self.log_auth_rejection(auth_reason)
            self.send_json(
                HTTPStatus.UNAUTHORIZED,
                {"error": "unauthorized", "reason": auth_reason, "serverTimeUnixMs": utc_ms()},
            )
            return
        if not self.require_overlay_source():
            self.send_json(HTTPStatus.FORBIDDEN, {"error": "overlay source required"})
            return

        parsed = urlsplit(self.path)
        query = parse_qs(parsed.query)
        if parsed.path == "/v1/health":
            self.send_json(HTTPStatus.OK, {"health": get_health(), "serverTimeUnixMs": utc_ms()})
            return
        identity: AuthenticatedIdentity | None = None
        if parsed.path in {"/v1/peers", "/v1/game-session", "/v1/mode-compatibility"}:
            allowed, identity = self.resolve_authenticated_identity()
            if not allowed:
                return
        if parsed.path == "/v1/peers":
            peers = decorated_clients(STATE.active_clients())
            self.send_json(
                HTTPStatus.OK,
                {
                    "peers": peers,
                    "onlineCount": len(peers),
                    "ttlSeconds": HEARTBEAT_TTL_SECONDS,
                    "topology": topology_summary(peers, OVERLAY),
                    "identityTrusted": identity is not None,
                    "requesterUserId": identity.user_id if identity is not None else None,
                },
            )
            return
        if parsed.path == "/v1/game-session":
            response = game_session_payload(self.client_address[0])
            response["identityTrusted"] = identity is not None
            response["requesterUserId"] = identity.user_id if identity is not None else None
            self.send_json(HTTPStatus.OK, response)
            return
        if parsed.path == "/v1/mode-compatibility":
            source = STATE.mode_assertion_by_virtual_ip(self.client_address[0])
            target_ip = clean_text((query.get("targetVirtualIp") or [""])[0], 64)
            target = STATE.mode_assertion_by_virtual_ip(target_ip)
            context = clean_text((query.get("context") or ["multiplayer"])[0], 32)
            if source is None or target is None:
                self.send_json(
                    HTTPStatus.OK,
                    {
                        "compatible": False,
                        "reason": "player mode state unavailable",
                        "context": context,
                        "identityTrusted": identity is not None,
                        "requesterUserId": identity.user_id if identity is not None else None,
                    },
                )
                return
            compatible, reason = gameplay_modes_compatible(source, target, context)
            self.send_json(
                HTTPStatus.OK,
                {
                    "compatible": compatible,
                    "reason": reason,
                    "context": context,
                    "sourceMode": source.gameplay_mode,
                    "targetMode": target.gameplay_mode,
                    "identityTrusted": identity is not None,
                    "requesterUserId": identity.user_id if identity is not None else None,
                },
            )
            return
        if parsed.path == "/v1/bootstrap":
            username = clean_text((query.get("username") or [""])[0], 64)
            client_version = clean_text((query.get("clientVersion") or [""])[0], 32)
            client_channel = clean_text((query.get("clientChannel") or [""])[0], 16).lower()
            peers = decorated_clients(STATE.active_clients())
            required_version = required_client_version()
            channel_accepted = client_channel == SERVER_MODE
            accepted = channel_accepted and client_version_accepted(client_version, required_version)
            self.send_json(
                HTTPStatus.OK,
                {
                    "serverToolVersion": SERVER_TOOL_VERSION,
                    "serverMode": SERVER_MODE,
                    "clientChannelAccepted": channel_accepted,
                    "requiredClientVersion": required_version,
                    "clientVersionAccepted": accepted,
                    "updateRequired": not accepted,
                    "maintenance": MAINTENANCE,
                    "accountExists": account_exists(username),
                    "onlineCount": len(peers),
                    "topology": topology_summary(peers, OVERLAY),
                    "health": get_health(),
                    "capabilities": capabilities(),
                    "serverTimeUnixMs": utc_ms(),
                },
            )
            return
        self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        self._begin_request()
        body = self.read_body()
        if body is None:
            self.send_json(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, {"error": "invalid body size"})
            return
        auth_reason = self.authorization_failure_reason(body)
        if auth_reason:
            self.log_auth_rejection(auth_reason)
            self.send_json(
                HTTPStatus.UNAUTHORIZED,
                {"error": "unauthorized", "reason": auth_reason, "serverTimeUnixMs": utc_ms()},
            )
            return
        try:
            payload = json.loads(body.decode("utf-8")) if body else {}
        except Exception:
            self.send_json(HTTPStatus.BAD_REQUEST, {"error": "invalid json"})
            return

        parsed = urlsplit(self.path)
        if parsed.path == "/v1/heartbeat":
            allowed, identity = self.resolve_authenticated_identity()
            if not allowed:
                return
            virtual_ip = clean_text(payload.get("virtualIp"), 64)
            if (
                not is_client_overlay_ip(virtual_ip, OVERLAY)
                or virtual_ip == SERVER_IP
                or not self.require_overlay_source(virtual_ip)
            ):
                self.send_json(HTTPStatus.FORBIDDEN, {"error": "heartbeat virtual ip must match overlay source"})
                return
            client_version = clean_text(payload.get("clientVersion"), 32)
            required_version = required_client_version()
            if not client_version_accepted(client_version, required_version):
                self.send_json(
                    HTTPStatus.UPGRADE_REQUIRED,
                    {
                        "error": "client update required",
                        "requiredClientVersion": required_version,
                    },
                )
                return
            try:
                gameplay_mode = normalize_gameplay_mode(payload.get("gameplayMode"))
                gameplay_integrity_valid = required_json_bool(
                    payload, "gameplayIntegrityValid"
                )
                game_running = required_json_bool(payload, "gameRunning")
            except ValueError as exc:
                STATE.invalidate_mode_assertion(
                    virtual_ip,
                    identity.username if identity is not None else "",
                )
                mark_gameplay_mode_snapshot_dirty()
                self.send_json(HTTPStatus.BAD_REQUEST, {"error": str(exc)})
                return
            state = ClientState(
                username=identity.username if identity is not None else clean_text(payload.get("username"), 64) or "Player",
                virtual_ip=virtual_ip,
                instance_id=clean_text(payload.get("instanceId"), 64),
                client_version=client_version,
                easytier_version=clean_text(payload.get("easyTierVersion"), 32),
                gameplay_mode=gameplay_mode,
                gameplay_integrity_valid=gameplay_integrity_valid,
                game_running=game_running,
                game_role=clean_text(payload.get("gameRole"), 16),
                game_peer_ip=clean_text(payload.get("gamePeerIp"), 64),
                server_latency_ms=clean_int(payload.get("serverLatencyMs"), 0, 600_000),
                server_transport=clean_text(payload.get("serverTransport"), 32),
                server_address_family=clean_text(payload.get("serverAddressFamily"), 16),
                game_latency_ms=clean_int(payload.get("gameLatencyMs"), 0, 600_000),
                game_transport=clean_text(payload.get("gameTransport"), 32),
                game_address_family=clean_text(payload.get("gameAddressFamily"), 16),
                next_hop=clean_text(payload.get("nextHop"), 64),
                hop_count=clean_int(payload.get("hopCount"), 0, 255),
                game_latency_p50_ms=clean_int(payload.get("gameLatencyP50Ms"), 0, 600_000),
                game_latency_p95_ms=clean_int(payload.get("gameLatencyP95Ms"), 0, 600_000),
                game_jitter_ms=clean_int(payload.get("gameJitterMs"), 0, 600_000),
                game_loss_percent=clean_float(payload.get("gameLossPercent"), 0.0, 100.0),
                user_id=identity.user_id if identity is not None else None,
                identity_trusted=identity is not None,
            )
            STATE.upsert(state)
            mark_gameplay_mode_snapshot_dirty()
            self.send_json(
                HTTPStatus.OK,
                {
                    "ok": True,
                    "serverTimeUnixMs": utc_ms(),
                    "nextHeartbeatSeconds": 5,
                    "identityTrusted": identity is not None,
                    "userId": identity.user_id if identity is not None else None,
                },
            )
            return
        self.send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})


def _raise_keyboard_interrupt(_signal_number: int, _frame: Any) -> None:
    raise KeyboardInterrupt


def main() -> None:
    if not SECRET:
        raise SystemExit("SCBL_SECRET is empty; control plane will not start")
    if not AUTH_TOKEN_KEY:
        print(
            "SCBL_AUTH_TOKEN_KEY is unavailable; supplied identity tokens will fail closed",
            flush=True,
        )
    refresh_game_session_snapshot()
    refresh_health_snapshot()
    server = ScblThreadingHTTPServer((SERVER_IP, CONTROL_PORT), Handler)
    previous_sigterm_handler = signal.signal(signal.SIGTERM, _raise_keyboard_interrupt)
    try:
        start_gameplay_mode_snapshot_writer()
        if not publish_gameplay_mode_snapshot():
            print("initial gameplay mode snapshot publish timed out", flush=True)
        threading.Thread(target=_session_refresh_loop, name="scbl-session-snapshot", daemon=True).start()
        threading.Thread(target=_health_refresh_loop, name="scbl-health-snapshot", daemon=True).start()
        threading.Thread(target=_metrics_loop, name="scbl-control-metrics", daemon=True).start()
        threading.Thread(target=_listener_watchdog_loop, name="scbl-listener-watchdog", daemon=True).start()
        print(
            f"SCBL control plane v{SERVER_TOOL_VERSION} listening on http://{SERVER_IP}:{CONTROL_PORT}; "
            f"heartbeat TTL={HEARTBEAT_TTL_SECONDS}s; mode assertion TTL={MODE_ASSERTION_TTL_SECONDS}s; session snapshot={SESSION_REFRESH_SECONDS}s; "
            f"connection=close; self-watchdog={SELF_WATCHDOG_INTERVAL_SECONDS}s x {SELF_WATCHDOG_FAILURE_THRESHOLD}",
            flush=True,
        )
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        if not stop_gameplay_mode_snapshot_writer():
            print("final gameplay mode snapshot flush timed out", flush=True)
        signal.signal(signal.SIGTERM, previous_sigterm_handler)
    if _CONTROL_PLANE_EXIT_CODE:
        raise SystemExit(_CONTROL_PLANE_EXIT_CODE)


if __name__ == "__main__":
    main()
