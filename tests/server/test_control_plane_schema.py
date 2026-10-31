"""Run donor read-only queries against actual LAN migrations and synthetic rows.

These checks document the boundary; they do not enable a public service. No real
database, credentials, saves, system services or network endpoints are accessed.
"""
import base64
from contextlib import contextmanager
import hashlib
import hmac
import importlib
import json
import os
from pathlib import Path
import sqlite3
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
MIGRATIONS = ROOT / "src/native/server/dedicated-server/src/storage/migrations"
sys.path.insert(0, str(ROOT / "integration/scbl3/server"))
try:
    with patch.dict(os.environ, {"SCBL_VIRTUAL_NET": "10.66.0.0/24"}):
        runtime = importlib.import_module("control_plane_runtime")
        auth = importlib.import_module("control_plane_auth")
finally:
    sys.path.pop(0)

REQUIRED_COLUMNS = {
    "users": {"id", "username", "auth_version", "recovery_code_hash", "updated_at"},
    "game_sessions": {"id", "type_id", "creator_id", "destroyed_at"},
    "participants": {"game_id", "user_id"},
    "station_urls": {"user_id", "url"},
}


def apply_migrations(connection, paths):
    # Keep failed migration tests transactional. executescript would otherwise
    # commit an already-open transaction before executing its first statement.
    sql = "\n".join(path.read_text(encoding="utf-8") for path in paths)
    connection.executescript("BEGIN;\n" + sql + "\nCOMMIT;")


class ControlPlaneSchemaTests(unittest.TestCase):
    def setUp(self):
        self.connection = sqlite3.connect(":memory:")
        self.addCleanup(self.connection.close)
        self.connection.execute("PRAGMA foreign_keys = ON")
        apply_migrations(self.connection, sorted(MIGRATIONS.glob("*.sql")))
        self.connection.executemany(
            "INSERT INTO users(id, username, auth_version) VALUES(?, ?, ?)",
            [(1000, "fixture-host", 1), (1001, "fixture-guest", 1)],
        )
        self.connection.commit()

    @contextmanager
    def database(self):
        # The real donor database_factory is a context manager too. Queries in
        # this audit run only against this isolated, in-memory LAN schema.
        yield self.connection

    def identity(self, user_id):
        return runtime.read_authenticated_identity_row(
            user_id, self.database, attempts=1, retry_seconds=0
        )

    def test_required_donor_columns_exist_without_renaming_lan_schema(self):
        for table, required in REQUIRED_COLUMNS.items():
            with self.subTest(table=table):
                columns = {row[1] for row in self.connection.execute(f"PRAGMA table_info({table})")}
                self.assertTrue(required <= columns, f"Missing {table} columns: {required - columns}")
        self.assertEqual(self.identity(1000), ("fixture-host", 1))
        self.assertIsNone(self.identity(9999))
        self.assertTrue(runtime.account_exists("fixture-host", True, self.database))
        self.assertFalse(runtime.account_exists("missing-fixture", True, self.database))
        self.assertEqual(runtime.database_health(True, self.database), (True, 3))

    def test_missing_identity_mapping_raises_instead_of_inventing_auth_version(self):
        connection = sqlite3.connect(":memory:")
        self.addCleanup(connection.close)
        connection.execute("CREATE TABLE users(id INTEGER, username TEXT)")
        @contextmanager
        def incomplete_database():
            yield connection
        with self.assertRaisesRegex(sqlite3.OperationalError, "auth_version"):
            runtime.read_authenticated_identity_row(
                1000, incomplete_database, attempts=1, retry_seconds=0
            )

    def token(self, **changes):
        # An explicitly synthetic signing key; never loaded from the environment.
        key = b"server-01-synthetic-auth-key-only"
        payload = dict(user_id=1000, username="fixture-host", source_ip="10.66.0.10",
                       issued_at=1000, expires_at=1600, auth_version=1)
        payload.update(changes)
        segment = base64.urlsafe_b64encode(json.dumps(payload).encode()).rstrip(b"=")
        body = b"v1." + segment
        signature = base64.urlsafe_b64encode(hmac.new(key, body, hashlib.sha256).digest()).rstrip(b"=")
        return (body + b"." + signature).decode(), key

    def test_donor_auth_reads_lan_revocation_and_rejects_invalid_claims(self):
        token, key = self.token()
        def verify(value=token, source="10.66.0.10", now=1100, signing_key=key):
            return auth.validate_control_auth_token(value, source, key=signing_key,
                                                    identity_loader=self.identity, now=now)
        self.assertEqual(verify().user_id, 1000)
        cases = [
            (dict(source="10.66.0.11"), "auth_token_wrong_source"),
            (dict(now=1600), "auth_token_expired"),
            (dict(signing_key=b""), "auth_token_key_unavailable"),
            (dict(signing_key=b"other-synthetic-key"), "invalid_auth_token"),
        ]
        for kwargs, reason in cases:
            with self.subTest(reason=reason), self.assertRaises(auth.ControlAuthError) as caught:
                verify(**kwargs)
            self.assertEqual(caught.exception.reason, reason)
        self.connection.execute("UPDATE users SET auth_version=2 WHERE id=1000")
        with self.assertRaises(auth.ControlAuthError) as caught:
            verify()
        self.assertEqual(caught.exception.reason, "auth_token_revoked")

    def seed_session(self):
        identity = "1|synthetic-release|synthetic-compat|" + "a" * 64
        for user, ip in [(1000, "10.66.0.10"), (1001, "10.66.0.11")]:
            self.connection.execute(
                "INSERT INTO game_runs(user_id,instance,mode,source_ip,release_identity) VALUES(?,?,2,?,?)",
                (user, f"synthetic-run-{user}", ip, identity),
            )
            self.connection.execute("INSERT INTO station_urls(user_id,url) VALUES(?,?)", (user, f"prudp:/address={ip};port=21127"))
        self.connection.execute("INSERT INTO game_sessions(id,type_id,creator_id,release_identity) VALUES(10,1,1000,?)", (identity,))
        self.connection.executemany("INSERT INTO participants(game_id,user_id) VALUES(10,?)", [(1000,), (1001,)])
        self.connection.commit()

    def test_donor_session_projection_reads_retained_table_names(self):
        self.seed_session()
        sessions = runtime.load_authoritative_sessions(True, self.database)
        self.assertEqual(set(sessions), {"10.66.0.10", "10.66.0.11"})
        session = sessions["10.66.0.10"]
        self.assertEqual((session.session_id, session.host_user_id, session.participant_count), (10, 1000, 2))
        self.connection.execute("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE id=10")
        self.assertEqual(runtime.load_authoritative_sessions(True, self.database), {})

    def test_audit_donor_projection_is_not_release_or_run_admission(self):
        # Known blocker, not an approved behavior: donor's query still advertises
        # the room after native runs retire. Keep Public disabled until replaced.
        self.seed_session()
        self.connection.execute("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP")
        sessions = runtime.load_authoritative_sessions(True, self.database)
        self.assertIn("10.66.0.10", sessions)
        # The retained LAN schema independently denies stale participation.
        with self.assertRaisesRegex(sqlite3.IntegrityError, "participant.*mismatch"):
            self.connection.execute("UPDATE participants SET user_id=user_id WHERE game_id=10")


class MigrationTransactionTests(unittest.TestCase):
    def connection(self):
        connection = sqlite3.connect(":memory:")
        self.addCleanup(connection.close)
        paths = sorted(MIGRATIONS.glob("*.sql"))
        apply_migrations(connection, paths[:1])
        connection.execute("INSERT INTO users(id,username,auth_version) VALUES(1000,'synthetic-preserved',7)")
        connection.commit()
        return connection, paths[1:]

    def test_additive_upgrade_preserves_identity_and_revocation(self):
        connection, remaining = self.connection()
        before = connection.execute("SELECT * FROM users ORDER BY id").fetchall()
        apply_migrations(connection, remaining)
        self.assertEqual(connection.execute("SELECT * FROM users ORDER BY id").fetchall(), before)
        self.assertIn("release_identity", {row[1] for row in connection.execute("PRAGMA table_info(game_sessions)")})

    def test_failed_transaction_rolls_back_schema_and_retains_synthetic_users(self):
        connection, remaining = self.connection()
        before = connection.execute("SELECT * FROM users ORDER BY id").fetchall()
        sql = "\n".join(path.read_text(encoding="utf-8") for path in remaining)
        with self.assertRaises(sqlite3.OperationalError):
            connection.executescript("BEGIN;\n" + sql + "\nSELECT * FROM intentionally_missing_migration_target;\nCOMMIT;")
        connection.rollback()
        self.assertEqual(connection.execute("SELECT * FROM users ORDER BY id").fetchall(), before)
        self.assertNotIn("release_identity", {row[1] for row in connection.execute("PRAGMA table_info(game_sessions)")})
        self.assertIsNone(connection.execute("SELECT name FROM sqlite_master WHERE name='game_runs'").fetchone())


if __name__ == "__main__":
    unittest.main()
