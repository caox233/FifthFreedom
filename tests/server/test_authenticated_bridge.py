"""Synthetic v1 token verification through the real reader and migrated SQLite."""
import base64
from contextlib import closing
from dataclasses import replace
from datetime import datetime, timezone
import hashlib
import hmac
import ipaddress
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control.authenticated_bridge import AuthBridgeConfig, AuthenticatedSessionBridge
    from fifth_freedom_control.session_reader import AdmissionError, ReleaseIdentity, ServerPolicy, SessionReader
finally:
    sys.path.pop(0)

KEY = bytes([0x11]) * 32  # Synthetic fixed key, matching the retained Rust test key.
NOW = 1_792_702_800  # A deterministic fixture clock; never an expiry bypass.
STAMP = datetime.fromtimestamp(NOW, timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
RELEASE = ReleaseIdentity("synthetic-release", "synthetic-compat", "a" * 64)
NONCE = "0123456789abcdef0123456789abcdef"


def package():
    return {"schema": 1, "release_id": RELEASE.release_id, "compat_id": RELEASE.compat_id,
            "manifest_sha256": RELEASE.manifest_sha256,
            **{role: {"release_id": RELEASE.release_id, "compat_id": RELEASE.compat_id}
               for role in ("launcher", "main_dll", "gameplay_dll", "server")}}


def claims(**changes):
    # Field order and shape follow retained Rust AuthTokenClaims serialization.
    result = dict(release=RELEASE.storage_key, user_id=1000, username="bridge-fixture-host",
                  source_ip="10.66.0.10", issued_at=NOW - 10, expires_at=NOW + 600,
                  auth_version=1, game_run={"product": "scbl-lan/2/game/mod", "instance": NONCE})
    result.update(changes)
    return result


def signed(payload=None, *, key=KEY, raw=None):
    # Test-only synthetic issuer using the preserved v1 envelope and stdlib HMAC.
    data = raw if raw is not None else json.dumps(claims() if payload is None else payload, ensure_ascii=False, separators=(",", ":")).encode()
    segment = base64.urlsafe_b64encode(data).rstrip(b"=")
    body = b"v1." + segment
    signature = base64.urlsafe_b64encode(hmac.new(key, body, hashlib.sha256).digest()).rstrip(b"=")
    return (body + b"." + signature).decode()


class AuthenticatedBridgeTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="ff-auth-bridge-")
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "synthetic.sqlite"
        with closing(sqlite3.connect(self.path)) as connection, connection:
            for migration in sorted((ROOT / "src/native/server/dedicated-server/src/storage/migrations").glob("*.sql")):
                connection.executescript(migration.read_text())
            for user, name, nonce, address in [(1000, "bridge-fixture-host", NONCE, "10.66.0.10"),
                                               (1001, "bridge-fixture-peer", "a" * 32, "10.66.0.11")]:
                connection.execute("INSERT INTO users(id,username,auth_version) VALUES(?,?,1)", (user, name))
                connection.execute("INSERT INTO game_runs(user_id,instance,mode,source_ip,release_identity) VALUES(?,?,2,?,?)",
                                   (user, nonce, address, RELEASE.storage_key))
            connection.execute("INSERT INTO game_sessions(id,type_id,creator_id,release_identity) VALUES(10,1,1000,?)", (RELEASE.storage_key,))
            connection.executemany("INSERT INTO participants(game_id,user_id) VALUES(10,?)", [(1000,), (1001,)])
            connection.execute("UPDATE game_runs SET seen_at=?", (STAMP,))
        self.policy = ServerPolicy(RELEASE, ipaddress.ip_network("10.66.0.0/24"))
        self.reader = SessionReader(self.path, self.policy)
        self.bridge = AuthenticatedSessionBridge(self.reader, AuthBridgeConfig(KEY), clock=lambda: NOW)

    def read(self, token=None, **changes):
        return self.bridge.read(signed() if token is None else token, changes.pop("source_ip", "10.66.0.10"),
                                changes.pop("package", package()), protocol_version=changes.pop("protocol_version", 1), **changes)

    def test_signed_game_identity_reaches_read_only_current_session(self):
        before = hashlib.sha256(self.path.read_bytes()).hexdigest()
        sessions = self.read()
        self.assertEqual([session.session_id for session in sessions], [10])
        self.assertEqual([member.user_id for member in sessions[0].members], [1000, 1001])
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).hexdigest(), before)

    def test_wrong_source_and_wrong_signature_are_rejected(self):
        with self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_WRONG_SOURCE"):
            self.read(source_ip="10.66.0.11")
        with self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
            self.read(signed(key=bytes([0x22]) * 32))
        token = signed()
        with self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
            self.read(token[:-1] + ("A" if token[-1] != "A" else "B"))

    def test_expiry_future_issue_and_ttl_bounds_match_preserved_contract(self):
        cases = [
            (claims(issued_at=NOW - 600, expires_at=NOW), "AUTH_TOKEN_EXPIRED"),
            (claims(issued_at=NOW + 61, expires_at=NOW + 661), "AUTH_TOKEN_FUTURE_ISSUED"),
            (claims(issued_at=NOW, expires_at=NOW + 299), "AUTH_TOKEN_INVALID"),
            (claims(issued_at=NOW, expires_at=NOW + 604801), "AUTH_TOKEN_INVALID"),
        ]
        for value, reason in cases:
            with self.subTest(reason=reason), self.assertRaisesRegex(AdmissionError, reason):
                self.read(signed(value))
        self.assertEqual(len(self.read(signed(claims(issued_at=NOW, expires_at=NOW + 300)))), 1)
        self.assertEqual(len(self.read(signed(claims(issued_at=NOW, expires_at=NOW + 604800)))), 1)

    def test_database_revocation_is_rechecked_on_every_request(self):
        token = signed()
        self.assertEqual(len(self.read(token)), 1)
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute("UPDATE users SET auth_version=2 WHERE id=1000")
        with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
            self.read(token)

    def test_unknown_retired_or_different_current_run_is_rejected(self):
        for payload in [claims(user_id=9999), claims(game_run={"product": "scbl-lan/2/game/mod", "instance": "f" * 32})]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
                self.read(signed(payload))
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=1000")
        with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
            self.read()

    def test_preserved_donor_golden_token_is_not_upgraded_to_new_game_admission(self):
        # Literal vector from donor Rust auth_token_tests::fixed_token_vector_is_valid.
        # Correct signature/time/source reaches the stricter release gate; tamper
        # fails signature verification first. No synthetic identity is invented.
        token = "v1.eyJ1c2VyX2lkIjo0MiwidXNlcm5hbWUiOiJDYW5vbmljYWxQbGF5ZXIiLCJzb3VyY2VfaXAiOiIxMC42Ni4wLjQyIiwiaXNzdWVkX2F0IjoxNzAwMDAwMDAwLCJleHBpcmVzX2F0IjoxNzAwMDg2NDAwLCJhdXRoX3ZlcnNpb24iOjN9.5GGWk0JTcbhyPcXHWHH5msYvAv9RLkxjyPknzbGcaHQ"
        bridge = AuthenticatedSessionBridge(self.reader, AuthBridgeConfig(KEY), clock=lambda: 1700000000)
        with self.assertRaisesRegex(AdmissionError, "AUTH_RELEASE_REQUIRED"):
            bridge.read(token, "10.66.0.42", package(), protocol_version=1)
        with self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
            bridge.read(token[:-1] + "R", "10.66.0.42", package(), protocol_version=1)

    def test_release_and_game_fields_are_signed_and_cannot_be_supplied_by_package(self):
        for payload, reason in [
            (claims(release=None), "AUTH_RELEASE_REQUIRED"),
            (claims(release="1|other|other|" + "b" * 64), "AUTH_RELEASE_MISMATCH"),
            (claims(release="2|r|c|" + "a" * 64), "AUTH_RELEASE_INVALID"),
            (claims(game_run=None), "AUTH_GAME_RUN_REQUIRED"),
            (claims(game_run={"product": "scbl-lan/1/official", "instance": NONCE}), "GAME_PRODUCT_REQUIRED"),
            (claims(game_run={"product": "scbl-lan/2/game/mod", "instance": "0" * 32}), "GAME_INSTANCE_INVALID"),
        ]:
            with self.subTest(reason=reason), self.assertRaisesRegex(AdmissionError, reason):
                self.read(signed(payload))
        with self.assertRaisesRegex(AdmissionError, "PACKAGE_IDENTITY_REQUIRED"):
            self.read(package=None)
        with self.assertRaisesRegex(AdmissionError, "PACKAGE_MIXED"):
            self.read(package={**package(), "gameplay_dll": None})
        with self.assertRaisesRegex(AdmissionError, "PROTOCOL_VERSION_UNSUPPORTED"):
            self.read(protocol_version=2)

    def test_existing_token_does_not_authorize_observer_even_with_forged_extension(self):
        allowed = SessionReader(self.path, replace(self.policy, observer_allowed=True))
        bridge = AuthenticatedSessionBridge(allowed, AuthBridgeConfig(KEY), clock=lambda: NOW)
        for token in [signed(), signed(claims(observer_admitted_run=NONCE))]:
            with self.assertRaisesRegex(AdmissionError, "OB_NOT_ALLOWED"):
                bridge.read(token, "10.66.0.10", package(), protocol_version=1, observer_requested=True)

    def test_oversized_nonascii_noncanonical_and_unknown_version_tokens_fail_closed(self):
        token = signed()
        for value in ["x" * 4097, "机密", "v2" + token[2:], token + ".extra", token + "=", "", b"token"]:
            with self.subTest(kind=type(value).__name__), self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
                self.read(value)

    def test_signed_duplicate_json_keys_and_ill_typed_claims_are_rejected(self):
        raw = json.dumps(claims(), separators=(",", ":")).encode()
        duplicate = raw[:-1] + b',"user_id":1001}'
        with self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
            self.read(signed(raw=duplicate))
        for key, value in [("user_id", True), ("user_id", 0x100000000), ("auth_version", 0),
                           ("issued_at", 1.5), ("expires_at", 0x8000000000000000),
                           ("username", "\ud800"), ("username", "x" * 65), ("username", "x\x00y")]:
            with self.subTest(field=key), self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
                self.read(signed(raw=json.dumps(claims(**{key: value}), separators=(",", ":")).encode()))
        for raw in [b"[]", b"null", b'{"user_id":NaN}', b"[" * 1100 + b"]" * 1100]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_TOKEN_INVALID"):
                self.read(signed(raw=raw))

    def test_missing_database_is_not_created_after_signature_verification(self):
        missing = self.path.with_name("absent.sqlite")
        bridge = AuthenticatedSessionBridge(SessionReader(missing, self.policy), AuthBridgeConfig(KEY), clock=lambda: NOW)
        with self.assertRaisesRegex(AdmissionError, "PUBLIC_SCHEMA_UNAVAILABLE"):
            bridge.read(signed(), "10.66.0.10", package(), protocol_version=1)
        self.assertFalse(missing.exists())

    def test_config_and_exception_representations_do_not_expose_secrets(self):
        config = AuthBridgeConfig(KEY)
        self.assertNotIn(KEY.hex(), repr(config))
        self.assertNotIn(repr(KEY), repr(config))
        with self.assertRaises(AdmissionError) as caught:
            self.read("synthetic-sensitive-token-sentinel")
        self.assertEqual(str(caught.exception), "AUTH_TOKEN_INVALID")
        self.assertIsNone(caught.exception.__cause__)

    def test_missing_invalid_key_and_bridge_version_are_rejected(self):
        for value in [None, b""]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_KEY_UNAVAILABLE"):
                AuthBridgeConfig(value)
        for value in [b"short", b"a" * 33, "a" * 32, bytearray(KEY)]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_CONFIG_INVALID"):
                AuthBridgeConfig(value)
        for value in [None, ""]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_KEY_UNAVAILABLE"):
                AuthBridgeConfig.from_hex(value)
        for value in ["a" * 63, "g" * 64, " " + KEY.hex(), KEY.hex() + "\n"]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_CONFIG_INVALID"):
                AuthBridgeConfig.from_hex(value)
        self.assertEqual(AuthBridgeConfig.from_hex(KEY.hex().upper()), AuthBridgeConfig(KEY))
        for version in [0, 2, True, "1"]:
            with self.assertRaisesRegex(AdmissionError, "AUTH_BRIDGE_VERSION_UNSUPPORTED"):
                AuthBridgeConfig(KEY, version)

    def test_clock_failure_and_invalid_clock_are_sanitized(self):
        for value in [-1, NOW + 0.5, True, 253402300800]:
            bridge = AuthenticatedSessionBridge(self.reader, AuthBridgeConfig(KEY), clock=lambda: value)
            with self.assertRaisesRegex(AdmissionError, "AUTH_CLOCK_INVALID"):
                bridge.read(signed(), "10.66.0.10", package(), protocol_version=1)
        def failure():
            raise ValueError("synthetic-sensitive-clock-sentinel")
        bridge = AuthenticatedSessionBridge(self.reader, AuthBridgeConfig(KEY), clock=failure)
        with self.assertRaises(AdmissionError) as caught:
            bridge.read(signed(), "10.66.0.10", package(), protocol_version=1)
        self.assertEqual(str(caught.exception), "AUTH_CLOCK_INVALID")
        self.assertIsNone(caught.exception.__cause__)


if __name__ == "__main__":
    unittest.main()
