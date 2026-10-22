"""Consume exact current Rust-producer bytes; no Python fixture token issuer.

The native test calls release_for_login + issue_auth_token and compares the
checked-in JSON byte-for-byte. Both key epochs and all identities are synthetic.
"""
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = Path(__file__).with_name("fixtures") / "native_game_token_v1.json"
sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control.authenticated_bridge import AuthBridgeConfig, AuthenticatedSessionBridge
    from fifth_freedom_control.session_reader import AdmissionError, ReleaseIdentity, ServerPolicy, SessionReader
finally:
    sys.path.pop(0)


class NativeTokenFixtureTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
        self.assertEqual(self.fixture["schema"], 1)
        self.assertIn("Synthetic", self.fixture["purpose"])
        self.assertEqual(self.fixture["key_hex"], "71" * 32)
        self.assertEqual(self.fixture["rotated_key_hex"], "72" * 32)
        self.assertEqual(self.fixture["claims"]["username"], "native-bridge-fixture")
        self.assertEqual(self.fixture["now"], 1_792_702_800)
        self.now = self.fixture["now"]
        self.claims = self.fixture["claims"]
        self.package = self.fixture["package"]
        self.release = ReleaseIdentity("native-bridge-synthetic", "native-bridge-compat", "a" * 64)
        self.release.admit(self.package)
        directory = tempfile.TemporaryDirectory(prefix="ff-native-token-fixture-")
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "synthetic.sqlite"
        stamp = datetime.fromtimestamp(self.now, timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        with closing(sqlite3.connect(self.path)) as connection, connection:
            for migration in sorted((ROOT / "src/native/server/dedicated-server/src/storage/migrations").glob("*.sql")):
                connection.executescript(migration.read_text())
            for user, name, nonce, address in [
                (1000, self.claims["username"], self.claims["game_run"]["instance"], self.claims["source_ip"]),
                (1001, "native-bridge-peer", "a" * 32, "10.66.0.11"),
            ]:
                connection.execute("INSERT INTO users(id,username,auth_version) VALUES(?,?,?)",
                                   (user, name, self.claims["auth_version"]))
                connection.execute("INSERT INTO game_runs(user_id,instance,mode,source_ip,release_identity) VALUES(?,?,2,?,?)",
                                   (user, nonce, address, self.release.storage_key))
            connection.execute("INSERT INTO game_sessions(id,type_id,creator_id,release_identity) VALUES(10,1,1000,?)",
                               (self.release.storage_key,))
            connection.executemany("INSERT INTO participants(game_id,user_id) VALUES(10,?)", [(1000,), (1001,)])
            connection.execute("UPDATE game_runs SET seen_at=?", (stamp,))
        self.reader = SessionReader(self.path, ServerPolicy(self.release, ipaddress.ip_network("10.66.0.0/24")))

    def read(self, token_field="token", key_field="key_hex", *, now=None, source=None, token=None):
        bridge = AuthenticatedSessionBridge(self.reader, AuthBridgeConfig.from_hex(self.fixture[key_field]),
                                            clock=lambda: self.now if now is None else now)
        return bridge.read(self.fixture[token_field] if token is None else token,
                           self.claims["source_ip"] if source is None else source,
                           self.package, protocol_version=1)

    def test_current_native_producer_is_admitted_by_python_reader_without_database_writes(self):
        before = hashlib.sha256(self.path.read_bytes()).digest()
        sessions = self.read()
        self.assertEqual([session.session_id for session in sessions], [10])
        self.assertEqual([member.user_id for member in sessions[0].members], [1000, 1001])
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).digest(), before)

    def test_rotated_native_epoch_accepts_its_token_and_rejects_previous_key_and_token(self):
        self.assertEqual(len(self.read("rotated_token", "rotated_key_hex")), 1)
        for token_field, key_field in [("token", "rotated_key_hex"), ("rotated_token", "key_hex")]:
            with self.subTest(token=token_field, key=key_field), self.assertRaisesRegex(AdmissionError, "^AUTH_TOKEN_INVALID$"):
                self.read(token_field, key_field)

    def test_exact_native_token_enforces_source_and_expiry(self):
        with self.assertRaisesRegex(AdmissionError, "^AUTH_TOKEN_WRONG_SOURCE$"):
            self.read(source="10.66.0.11")
        with self.assertRaisesRegex(AdmissionError, "^AUTH_TOKEN_EXPIRED$"):
            self.read(now=self.claims["expires_at"])

    def test_exact_native_token_cannot_outlive_database_revocation_or_run_retirement(self):
        self.assertEqual(len(self.read()), 1)
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute("UPDATE users SET auth_version=auth_version+1 WHERE id=1000")
        with self.assertRaisesRegex(AdmissionError, "^AUTH_RUN_REVOKED_OR_STALE$"):
            self.read()
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute("UPDATE users SET auth_version=? WHERE id=1000", (self.claims["auth_version"],))
            connection.execute("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=1000")
        with self.assertRaisesRegex(AdmissionError, "^AUTH_RUN_REVOKED_OR_STALE$"):
            self.read()

    def test_altered_native_signature_is_rejected(self):
        token = self.fixture["token"]
        start = token.rfind(".") + 1
        changed = token[:start] + ("A" if token[start] != "A" else "B") + token[start + 1:]
        with self.assertRaisesRegex(AdmissionError, "^AUTH_TOKEN_INVALID$"):
            self.read(token=changed)


if __name__ == "__main__":
    unittest.main()
