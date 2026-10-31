"""Owned synthetic Linux TLS/SQLite processes; no game, external service or live data."""
import asyncio
from contextlib import closing
from dataclasses import replace
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sqlite3
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from test_authenticated_bridge import KEY, NOW, ROOT, RELEASE, NONCE, claims, package, signed

sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control.authenticated_bridge import AuthBridgeConfig, AuthenticatedSessionBridge
    from fifth_freedom_control.protected_transport import ProtectedSessionServer, RateLimiter
    from fifth_freedom_control.secure_config import build_tls_context, load_config, load_issuer_key, validate_database
    from fifth_freedom_control.session_reader import AdmissionError, ServerPolicy, SessionReader
finally:
    sys.path.pop(0)

SUPPORTED = sys.platform == "linux" and shutil.which("openssl") is not None


def private(path, data):
    path.write_bytes(data)
    path.chmod(0o600)
    return path


def seed(directory):
    path = directory / "synthetic.sqlite"
    stamp = datetime.fromtimestamp(NOW, timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    with closing(sqlite3.connect(path)) as connection, connection:
        for migration in sorted((ROOT / "src/native/server/dedicated-server/src/storage/migrations").glob("*.sql")):
            connection.executescript(migration.read_text())
        for user, name, nonce, address in [(1000, "bridge-fixture-host", NONCE, "127.0.0.1"),
                                          (1001, "bridge-fixture-peer", "a" * 32, "127.0.0.2")]:
            connection.execute("INSERT INTO users(id,username,auth_version) VALUES(?,?,1)", (user, name))
            connection.execute("INSERT INTO game_runs(user_id,instance,mode,source_ip,release_identity) VALUES(?,?,2,?,?)",
                               (user, nonce, address, RELEASE.storage_key))
        connection.execute("INSERT INTO game_sessions(id,type_id,creator_id,release_identity) VALUES(10,1,1000,?)", (RELEASE.storage_key,))
        connection.executemany("INSERT INTO participants(game_id,user_id) VALUES(10,?)", [(1000,), (1001,)])
        connection.execute("UPDATE game_runs SET seen_at=?", (stamp,))
    path.chmod(0o600)
    return path


def prepare(directory, *, certificate=None):
    key = private(directory / "issuer.key", KEY)
    if certificate is None:
        cert, tls_key = directory / "certificate.pem", directory / "tls.key"
        process = subprocess.run([shutil.which("openssl"), "req", "-x509", "-newkey", "rsa:2048", "-nodes",
            "-keyout", str(tls_key), "-out", str(cert), "-days", "1", "-subj", "/CN=synthetic-loopback",
            "-addext", "subjectAltName=IP:127.0.0.1"], capture_output=True, timeout=20)
        if process.returncode:
            raise RuntimeError("SYNTHETIC_CERTIFICATE_GENERATION_FAILED")
        cert.chmod(0o600)
        tls_key.chmod(0o600)
    else:
        cert, tls_key = certificate
    database = seed(directory)
    value = dict(schema=1, listen_ip="127.0.0.1", listen_port=19443, database=str(database),
                 issuer_key_file=str(key), tls_certificate_file=str(cert), tls_private_key_file=str(tls_key),
                 approved_release=dict(release_id=RELEASE.release_id, compat_id=RELEASE.compat_id, manifest_sha256=RELEASE.manifest_sha256),
                 client_network="127.0.0.0/8", observer_allowed=False, official_only=False)
    config_path = private(directory / "control.json", json.dumps(value).encode())
    return config_path, value


@unittest.skipUnless(SUPPORTED, "Linux private-file contract and openssl required")
class SecureConfigTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-private-config-")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.key = private(self.directory / "issuer.key", KEY)

    def test_keyfile_exact_bytes_and_rotation_are_loaded_only_on_reopen(self):
        original = load_issuer_key(self.key)
        rotated = private(self.directory / "next.key", b"r" * 32)
        os.replace(rotated, self.key)
        self.assertEqual(original.key, KEY)
        self.assertEqual(load_issuer_key(self.key).key, b"r" * 32)
        self.assertNotIn(KEY.hex(), repr(original))

    def test_keyfile_in_place_change_during_read_is_detected(self):
        original_read = os.read
        changed = False
        def changing_read(descriptor, count):
            nonlocal changed
            value = original_read(descriptor, count)
            if not changed:
                changed = True
                self.key.write_bytes(b"r" * 32)
            return value
        with patch("fifth_freedom_control.secure_config.os.read", side_effect=changing_read):
            with self.assertRaisesRegex(AdmissionError, "PRIVATE_FILE_CHANGED"):
                load_issuer_key(self.key)

    def test_keyfile_rejects_lengths_permissions_hardlinks_and_special_files(self):
        for data in (b"", b"x" * 31, b"x" * 33, KEY.hex().encode(), KEY + b"\n"):
            private(self.key, data)
            with self.assertRaises(AdmissionError):
                load_issuer_key(self.key)
        private(self.key, KEY).chmod(0o644)
        with self.assertRaises(AdmissionError):
            load_issuer_key(self.key)
        self.key.chmod(0o600)
        os.link(self.key, self.directory / "alias.key")
        with self.assertRaises(AdmissionError):
            load_issuer_key(self.key)
        fifo = self.directory / "fifo"
        os.mkfifo(fifo, 0o600)
        started = time.monotonic()
        with self.assertRaises(AdmissionError):
            load_issuer_key(fifo)
        self.assertLess(time.monotonic() - started, 1)

    def test_keyfile_rejects_relative_traversal_symlink_file_and_parent(self):
        link = self.directory / "link.key"
        link.symlink_to(self.key)
        nested = self.directory / "real"
        nested.mkdir(mode=0o700)
        private(nested / "nested.key", KEY)
        (self.directory / "linked").symlink_to(nested, target_is_directory=True)
        for path in ("issuer.key", str(self.directory) + "/../" + self.directory.name + "/issuer.key", link,
                     self.directory / "linked/nested.key"):
            with self.assertRaises(AdmissionError):
                load_issuer_key(path)
        self.directory.chmod(0o755)
        try:
            with self.assertRaises(AdmissionError):
                load_issuer_key(self.key)
        finally:
            self.directory.chmod(0o700)

    def test_config_parser_requires_all_explicit_fields_and_rejects_duplicates(self):
        path, value = prepare(self.directory)
        config = load_config(path)
        self.assertEqual(config.listen_ip, "127.0.0.1")
        self.assertEqual(validate_database(config).database, Path(value["database"]))
        self.assertEqual(build_tls_context(config).minimum_version, ssl.TLSVersion.TLSv1_2)
        for updates in ({"listen_ip": "old-service.invalid"}, {"listen_port": True}, {"listen_port": 0},
                        {"issuer_key_file": "relative"}, {"observer_allowed": 1}, {"extra": "denied"}):
            private(path, json.dumps({**value, **updates}).encode())
            with self.assertRaises(AdmissionError):
                load_config(path)
        private(path, json.dumps(value).encode()[:-1] + b',"schema":1}')
        with self.assertRaises(AdmissionError):
            load_config(path)

    def test_missing_database_tls_and_key_fail_before_bind_without_creation(self):
        path, value = prepare(self.directory)
        config = load_config(path)
        missing = self.directory / "not-created"
        for operation in (lambda: validate_database(replace(config, database=missing)),
                          lambda: build_tls_context(replace(config, tls_private_key_file=missing)),
                          lambda: load_issuer_key(missing)):
            with self.assertRaises(AdmissionError):
                operation()
        self.assertFalse(missing.exists())

    def test_rate_limiter_bounds_sources_and_preserves_per_source_burst(self):
        instant = [10.0]
        limiter = RateLimiter(clock=lambda: instant[0])
        self.assertTrue(all(limiter.admit("127.0.0.1") for _ in range(10)))
        self.assertFalse(limiter.admit("127.0.0.1"))
        self.assertTrue(limiter.admit("127.0.0.2"))
        instant[0] += 0.5
        self.assertTrue(limiter.admit("127.0.0.1"))
        self.assertFalse(limiter.admit("127.0.0.1"))
        with patch("fifth_freedom_control.protected_transport.MAX_RATE_SOURCES", 2):
            self.assertFalse(limiter.admit("127.0.0.3"))
            self.assertLessEqual(len(limiter.sources), 2)
            instant[0] += 60
            self.assertTrue(limiter.admit("127.0.0.3"))


@unittest.skipUnless(SUPPORTED, "Linux private-file contract and openssl required")
class ProtectedTransportTests(unittest.IsolatedAsyncioTestCase):
    @classmethod
    def setUpClass(cls):
        cls.certificate_directory = tempfile.TemporaryDirectory(prefix="ff-loopback-certificate-")
        cls.addClassCleanup(cls.certificate_directory.cleanup)
        path, value = prepare(Path(cls.certificate_directory.name))
        cls.certificate = (Path(value["tls_certificate_file"]), Path(value["tls_private_key_file"]))

    async def asyncSetUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-loopback-runtime-")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.config_path, self.config_value = prepare(self.directory, certificate=self.certificate)
        self.config = load_config(self.config_path)
        self.bridge = AuthenticatedSessionBridge(validate_database(self.config), load_issuer_key(self.config.issuer_key_file), clock=lambda: NOW)
        self.server = ProtectedSessionServer(self.bridge, build_tls_context(self.config), "127.0.0.1", 0)
        self.host, self.port = await self.server.start()
        self.addAsyncCleanup(self.server.close)
        self.client_context = ssl.create_default_context(cafile=str(self.certificate[0]))

    def request(self, *, token=None, headers=b"", body=None):
        token = signed(claims(source_ip="127.0.0.1")) if token is None else token
        body = json.dumps(dict(protocol_version=1, package=package(), observer_requested=False)).encode() if body is None else body
        return b"POST /v1/sessions HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nAuthorization: Bearer " + token.encode() + b"\r\nContent-Length: " + str(len(body)).encode() + b"\r\n" + headers + b"\r\n" + body

    async def exchange(self, request):
        reader, writer = await asyncio.open_connection(self.host, self.port, ssl=self.client_context, server_hostname=self.host)
        try:
            writer.write(request)
            await writer.drain()
            response = await asyncio.wait_for(reader.read(), 3)
            head, body = response.split(b"\r\n\r\n", 1)
            self.assertIn(b"Cache-Control: no-store", head)
            return int(head.split(b" ")[1]), json.loads(body)
        finally:
            writer.close()
            await writer.wait_closed()

    async def test_real_tls_query_uses_signed_current_run_without_database_writes(self):
        before = hashlib.sha256(self.config.database.read_bytes()).digest()
        status, body = await self.exchange(self.request())
        self.assertEqual(status, 200)
        self.assertEqual([row["session_id"] for row in body["sessions"]], [10])
        self.assertEqual(body["sessions"][0]["members"][0], dict(user_id=1000, source_ip="127.0.0.1"))
        self.assertEqual(hashlib.sha256(self.config.database.read_bytes()).digest(), before)

    async def test_forwarded_headers_cannot_override_kernel_peer_or_signed_identity(self):
        token = signed(claims(source_ip="10.66.0.10"))
        status, body = await self.exchange(self.request(token=token, headers=b"X-Forwarded-For: 10.66.0.10\r\nForwarded: for=10.66.0.10\r\n"))
        self.assertEqual((status, body), (403, {"error": "ADMISSION_DENIED"}))
        status, _ = await self.exchange(self.request(headers=b"X-Forwarded-For: attacker-controlled\r\n"))
        self.assertEqual(status, 200)

    async def test_malformed_smuggled_oversized_and_duplicate_json_requests_are_rejected(self):
        cases = [
            (self.request(headers=b"Content-Length: 1\r\n"), 400),
            (self.request(headers=b"Transfer-Encoding: chunked\r\n"), 400),
            (self.request(headers=b"Expect: 100-continue\r\n"), 400),
            (self.request(body=b'{"protocol_version":1,"protocol_version":1,"package":{},"observer_requested":false}'), 400),
            (self.request(body=b'\xff'), 400),
            (self.request(body=b"x" * 16385), 413),
            (self.request(headers=b"X-Oversize: " + b"x" * 8192 + b"\r\n"), 431),
            (self.request().replace(b"POST /v1/sessions", b"GET /v1/sessions"), 400),
        ]
        for request, expected in cases:
            with self.subTest(expected=expected):
                status, body = await self.exchange(request)
                self.assertEqual(status, expected)
                self.assertEqual(set(body), {"error"})

    async def test_auth_revocation_is_rechecked_between_real_tls_requests(self):
        self.assertEqual((await self.exchange(self.request()))[0], 200)
        with closing(sqlite3.connect(self.config.database)) as connection, connection:
            connection.execute("UPDATE users SET auth_version=2 WHERE id=1000")
        self.assertEqual((await self.exchange(self.request()))[0], 403)

    async def test_plain_http_is_never_served_and_slow_handshake_does_not_block_accept(self):
        reader, writer = await asyncio.open_connection(self.host, self.port)
        try:
            self.assertEqual((await self.exchange(self.request()))[0], 200)
            writer.write(self.request())
            await writer.drain()
            try:
                result = await asyncio.wait_for(reader.read(), 2)
            except ConnectionError:
                result = b""
            self.assertNotIn(b"HTTP/1.1 200", result)
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass

    async def test_connection_bound_includes_slow_tls_handshakes_and_close_owns_cleanup(self):
        with patch("fifth_freedom_control.protected_transport.MAX_CONNECTIONS", 2):
            first = await asyncio.open_connection(self.host, self.port)
            second = await asyncio.open_connection(self.host, self.port)
            for _ in range(20):
                if len(self.server.connections) == 2:
                    break
                await asyncio.sleep(0.01)
            third = await asyncio.open_connection(self.host, self.port)
            self.assertEqual(await asyncio.wait_for(third[0].read(), 1), b"")
            self.assertEqual(len(self.server.connections), 2)
            await self.server.close()
            self.assertFalse(self.server.connections)
            for _, writer in (first, second, third):
                writer.close()
                await writer.wait_closed()
            with self.assertRaises(OSError):
                await asyncio.open_connection(self.host, self.port)

    async def test_body_timeout_releases_connection_without_disclosing_partial_request(self):
        with patch("fifth_freedom_control.protected_transport.REQUEST_TIMEOUT", 0.2):
            reader, writer = await asyncio.open_connection(self.host, self.port, ssl=self.client_context, server_hostname=self.host)
            writer.write(self.request()[:-20])
            await writer.drain()
            self.assertEqual(await asyncio.wait_for(reader.read(), 2), b"")
            writer.close()
            await writer.wait_closed()
        self.assertEqual((await self.exchange(self.request()))[0], 200)

    async def test_timed_out_query_keeps_worker_permit_until_actual_completion(self):
        entered, release = threading.Event(), threading.Event()
        original = self.bridge.read
        calls = []
        def blocked(*args, **kwargs):
            calls.append(1)
            entered.set()
            if not release.wait(3):
                raise AdmissionError("SYNTHETIC_WORKER_TIMEOUT")
            return original(*args, **kwargs)
        with patch("fifth_freedom_control.protected_transport.REQUEST_TIMEOUT", 0.2), \
                patch("fifth_freedom_control.protected_transport.MAX_QUERIES", 1), \
                patch.object(self.bridge, "read", side_effect=blocked):
            reader, writer = await asyncio.open_connection(self.host, self.port, ssl=self.client_context, server_hostname=self.host)
            try:
                writer.write(self.request())
                await writer.drain()
                for _ in range(100):
                    if entered.is_set():
                        break
                    await asyncio.sleep(0.01)
                self.assertTrue(entered.is_set())
                self.assertEqual(await asyncio.wait_for(reader.read(), 2), b"")
                self.assertEqual(len(self.server.queries), 1)
                self.assertEqual(await self.exchange(self.request()), (503, {"error": "SERVICE_BUSY"}))
                self.assertEqual(len(calls), 1)
            finally:
                release.set()
                writer.close()
                await writer.wait_closed()
                for _ in range(100):
                    if not self.server.queries:
                        break
                    await asyncio.sleep(0.01)
        self.assertFalse(self.server.queries)
        self.assertEqual((await self.exchange(self.request()))[0], 200)

    async def test_cli_config_check_and_sigterm_owned_process_lifecycle(self):
        # Only own a new synthetic control-plane process, never a game/server.
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            selected_port = reservation.getsockname()[1]
        self.config_value["listen_port"] = selected_port
        private(self.config_path, json.dumps(self.config_value).encode())
        command = [sys.executable, "-B", "-m", "fifth_freedom_control", "--config", str(self.config_path)]
        environment = dict(os.environ, PYTHONPATH=str(ROOT / "src/public"))
        checked = await asyncio.create_subprocess_exec(*command, "--check-config", env=environment,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        output, errors = await asyncio.wait_for(checked.communicate(), 5)
        self.assertEqual((checked.returncode, output, errors), (0, b"CONTROL_CONFIG_VALID\n", b""))
        process = await asyncio.create_subprocess_exec(*command, env=environment,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        try:
            self.assertEqual(await asyncio.wait_for(process.stdout.readline(), 5), b"CONTROL_READY\n")
            process.send_signal(signal.SIGTERM)
            output, errors = await asyncio.wait_for(process.communicate(), 5)
            self.assertEqual((process.returncode, output, errors), (0, b"", b""))
            with self.assertRaises(OSError):
                await asyncio.open_connection("127.0.0.1", selected_port)
        finally:
            if process.returncode is None:
                process.kill()
                await process.wait()


if __name__ == "__main__":
    unittest.main()
