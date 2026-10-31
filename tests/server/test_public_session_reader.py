"""Exercise the production public read adapter with synthetic migrated SQLite."""
from contextlib import closing
from dataclasses import replace
from datetime import datetime, timezone
import hashlib
import ipaddress
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control.session_reader import (
        AdmissionError, AuthenticatedGameIdentity, ReleaseIdentity, ServerPolicy, SessionReader,
    )
finally:
    sys.path.pop(0)

NOW = datetime(2026, 10, 22, 21, 0, 0, tzinfo=timezone.utc)
STAMP = "2026-10-22 21:00:00"
RELEASE = ReleaseIdentity("synthetic-release", "synthetic-compat", "a" * 64)
NETWORK = ipaddress.ip_network("10.66.0.0/24")
MIGRATIONS = ROOT / "src/native/server/dedicated-server/src/storage/migrations"


def declaration(release=RELEASE):
    return {
        "schema": 1, "release_id": release.release_id, "compat_id": release.compat_id,
        "manifest_sha256": release.manifest_sha256,
        **{role: {"release_id": release.release_id, "compat_id": release.compat_id}
           for role in ("launcher", "main_dll", "gameplay_dll", "server")},
    }


class PublicSessionReaderTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-public-reader-")
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "synthetic.sqlite"
        with closing(sqlite3.connect(self.path)) as connection, connection:
            for migration in sorted(MIGRATIONS.glob("*.sql")):
                connection.executescript(migration.read_text(encoding="utf-8"))
        self.policy = ServerPolicy(RELEASE, NETWORK)
        self.reader = SessionReader(self.path, self.policy)
        self.identity = AuthenticatedGameIdentity(
            1000, "fixture-1000", 1, "10.66.0.10", "scbl-lan/2/game/mod",
            f"{1000:032x}", RELEASE.storage_key,
        )
        self.add_room(10, [1000, 1001])

    def add_room(self, room, users, mode=2, release=RELEASE):
        with closing(sqlite3.connect(self.path)) as connection, connection:
            for user in users:
                connection.execute("INSERT INTO users(id,username,auth_version) VALUES(?,?,1)", (user, f"fixture-{user}"))
                connection.execute(
                    "INSERT INTO game_runs(user_id,instance,mode,source_ip,seen_at,release_identity) VALUES(?,?,?,?,?,?)",
                    (user, f"{user:032x}", mode, f"10.66.0.{user - 990}", STAMP, release.storage_key),
                )
            # Actual schema triggers compare freshness with its own clock on
            # creation. The fixture initially uses CURRENT_TIMESTAMP, then puts
            # all persisted seen_at values on a deterministic test clock.
            connection.execute("UPDATE game_runs SET seen_at=CURRENT_TIMESTAMP")
            connection.execute("INSERT INTO game_sessions(id,type_id,creator_id,release_identity) VALUES(?,1,?,?)", (room, users[0], release.storage_key))
            connection.executemany("INSERT INTO participants(game_id,user_id) VALUES(?,?)", [(room, user) for user in users])
            connection.execute("UPDATE game_runs SET seen_at=?", (STAMP,))

    def mutate(self, sql, parameters=()):
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute(sql, parameters)

    def read(self, identity=None, package=None, reader=None, **options):
        return (reader or self.reader).read(identity or self.identity, declaration() if package is None else package,
                                           protocol_version=options.pop("protocol_version", 1), now=NOW, **options)

    def test_current_room_is_read_only_and_uses_run_source_not_station_urls(self):
        self.mutate("INSERT INTO station_urls(user_id,url) VALUES(1001,'prudp:/address=10.66.0.99;port=21127')")
        before = hashlib.sha256(self.path.read_bytes()).hexdigest()
        rooms = self.read()
        self.assertEqual(len(rooms), 1)
        room = rooms[0]
        self.assertEqual((room.session_id, room.host_user_id, room.gameplay_mode), (10, 1000, 2))
        self.assertEqual([(m.user_id, m.source_ip) for m in room.members], [(1000, "10.66.0.10"), (1001, "10.66.0.11")])
        self.assertEqual(hashlib.sha256(self.path.read_bytes()).hexdigest(), before)

    def test_retired_requester_is_rejected(self):
        self.mutate("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=1000")
        with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
            self.read()

    def test_retired_host_or_guest_cannot_appear_as_a_live_room(self):
        self.mutate("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=1001")
        self.assertEqual(self.read(), ())
        self.mutate("UPDATE game_runs SET retired_at=NULL WHERE user_id=1001")
        self.mutate("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=1000")
        guest = replace(self.identity, user_id=1001, username="fixture-1001", source_ip="10.66.0.11", game_instance=f"{1001:032x}")
        self.assertEqual(self.read(guest), ())

    def test_stale_and_future_requester_runs_fail_closed(self):
        for seen in ["2026-10-22 20:58:29", "2026-10-22 21:00:01", "not-a-timestamp"]:
            with self.subTest(seen=seen):
                self.mutate("UPDATE game_runs SET seen_at=? WHERE user_id=1000", (seen,))
                with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
                    self.read()

    def test_auth_version_username_nonce_and_source_must_match_current_run(self):
        for changed in [dict(auth_version=2), dict(username="different-fixture"),
                        dict(game_instance="f" * 32), dict(source_ip="10.66.0.12")]:
            with self.subTest(changed=changed), self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
                self.read(replace(self.identity, **changed))
        self.mutate("UPDATE users SET auth_version=2 WHERE id=1000")
        with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
            self.read()

    def test_legacy_unknown_product_protocol_and_bad_instance_are_rejected(self):
        for product in ["scbl-lan/1/official", "scbl-lan/3/game/mod", "Fifth Freedom", "scbl-lan/2/game/MOD"]:
            with self.subTest(product=product), self.assertRaisesRegex(AdmissionError, "GAME_PRODUCT_REQUIRED"):
                self.read(replace(self.identity, product=product))
        for version in [0, 2, True, "1"]:
            with self.subTest(version=version), self.assertRaisesRegex(AdmissionError, "PROTOCOL_VERSION_UNSUPPORTED"):
                self.read(protocol_version=version)
        for nonce in ["", "0" * 32, "F" * 32, "a" * 31]:
            with self.subTest(nonce=nonce), self.assertRaisesRegex(AdmissionError, "GAME_INSTANCE_INVALID"):
                self.read(replace(self.identity, game_instance=nonce))

    def test_all_four_role_declarations_and_release_tuple_are_required(self):
        for role in ["launcher", "main_dll", "gameplay_dll", "server"]:
            package = declaration()
            del package[role]
            with self.subTest(role=role), self.assertRaisesRegex(AdmissionError, "PACKAGE_MIXED"):
                self.read(package=package)
        for release, reason in [
            (ReleaseIdentity("other", RELEASE.compat_id, RELEASE.manifest_sha256), "RELEASE_MISMATCH"),
            (ReleaseIdentity(RELEASE.release_id, "other", RELEASE.manifest_sha256), "COMPAT_MISMATCH"),
            (ReleaseIdentity(RELEASE.release_id, RELEASE.compat_id, "b" * 64), "PACKAGE_CATALOG_MISMATCH"),
        ]:
            with self.subTest(reason=reason), self.assertRaisesRegex(AdmissionError, reason):
                self.read(package=declaration(release))
        with self.assertRaisesRegex(AdmissionError, "AUTH_RELEASE_MISMATCH"):
            self.read(replace(self.identity, release_identity=""))

    def test_observer_requires_server_policy_and_exact_admitted_run(self):
        acknowledged = replace(self.identity, observer_admitted_run=self.identity.game_instance)
        with self.assertRaisesRegex(AdmissionError, "OB_NOT_ALLOWED"):
            self.read(acknowledged, observer_requested=True)
        allowed = SessionReader(self.path, replace(self.policy, observer_allowed=True))
        for identity in [self.identity, replace(self.identity, observer_admitted_run="f" * 32)]:
            with self.assertRaisesRegex(AdmissionError, "OB_NOT_ALLOWED"):
                self.read(identity, reader=allowed, observer_requested=True)
        self.assertEqual(len(self.read(acknowledged, reader=allowed, observer_requested=True)), 1)
        self.assertEqual(len(self.read(reader=allowed)), 1)

    def test_mode_switch_or_other_release_run_does_not_relabel_old_room(self):
        for mode, release in [(1, RELEASE.storage_key), (2, ReleaseIdentity("other", "other", "b" * 64).storage_key)]:
            with self.subTest(mode=mode, release=release):
                self.mutate("UPDATE game_runs SET retired_at=CURRENT_TIMESTAMP WHERE user_id=1001")
                self.mutate("INSERT INTO game_runs(user_id,instance,mode,source_ip,seen_at,release_identity) VALUES(1001,?,?, '10.66.0.11',?,?)",
                            (f"{mode}{release[2:10]}", mode, STAMP, release))
                self.assertEqual(self.read(), ())

    def test_destroyed_and_unrelated_rooms_are_not_exposed(self):
        self.add_room(20, [1002, 1003])
        self.assertEqual([room.session_id for room in self.read()], [10])
        self.mutate("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE id=10")
        self.assertEqual(self.read(), ())

    def test_legacy_null_room_or_run_is_never_upgraded_by_current_claims(self):
        migrations = sorted(MIGRATIONS.glob("*.sql"))
        for known_run in (True, False):
            with self.subTest(known_run=known_run):
                path = self.path.with_name(f"legacy-{known_run}.sqlite")
                split = 3 if known_run else 2
                with closing(sqlite3.connect(path)) as connection, connection:
                    for migration in migrations[:split]:
                        connection.executescript(migration.read_text())
                    for user in (1000, 1001):
                        connection.execute("INSERT INTO users(id,username) VALUES(?,?)", (user, f"fixture-{user}"))
                        columns = "user_id,instance,mode,source_ip,seen_at"
                        values = [user, f"{user:032x}", 2, f"10.66.0.{user - 990}", STAMP]
                        if known_run:
                            columns += ",release_identity"
                            values.append(RELEASE.storage_key)
                        connection.execute(f"INSERT INTO game_runs({columns}) VALUES({','.join('?' for _ in values)})", values)
                    connection.execute("INSERT INTO game_sessions(id,type_id,creator_id) VALUES(10,1,1000)")
                    connection.executemany("INSERT INTO participants(game_id,user_id) VALUES(10,?)", [(1000,), (1001,)])
                    for migration in migrations[split:]:
                        connection.executescript(migration.read_text())
                reader = SessionReader(path, self.policy)
                if known_run:
                    self.assertEqual(self.read(reader=reader), ())
                else:
                    with self.assertRaisesRegex(AdmissionError, "AUTH_RUN_REVOKED_OR_STALE"):
                        self.read(reader=reader)

    def test_duplicate_source_addresses_do_not_select_an_ambiguous_owner(self):
        self.mutate("UPDATE game_runs SET source_ip='10.66.0.10' WHERE user_id=1001")
        with self.assertRaisesRegex(AdmissionError, "SESSION_MEMBERSHIP_AMBIGUOUS"):
            self.read()

    def test_official_only_and_network_constraints_do_not_fall_back(self):
        with self.assertRaisesRegex(AdmissionError, "GAMEPLAY_MODE_UNSUPPORTED"):
            self.read(reader=SessionReader(self.path, replace(self.policy, official_only=True)))
        for address in ["10.66.0.0", "10.66.0.255", "192.0.2.9", "::1"]:
            with self.subTest(address=address), self.assertRaisesRegex(AdmissionError, "AUTH_SOURCE_INVALID"):
                self.read(replace(self.identity, source_ip=address))

    def test_missing_database_or_schema_is_not_created_or_silently_accepted(self):
        missing = self.path.with_name("absent.sqlite")
        with self.assertRaisesRegex(AdmissionError, "PUBLIC_SCHEMA_UNAVAILABLE"):
            self.read(reader=SessionReader(missing, self.policy))
        self.assertFalse(missing.exists())
        # Admission happens before database access even for a missing path.
        with self.assertRaisesRegex(AdmissionError, "PACKAGE_IDENTITY_REQUIRED"):
            SessionReader(missing, self.policy).read(self.identity, None, protocol_version=1, now=NOW)
        incomplete = self.path.with_name("incomplete.sqlite")
        with closing(sqlite3.connect(incomplete)) as connection, connection:
            connection.execute("CREATE TABLE users(id INTEGER)")
        with self.assertRaisesRegex(AdmissionError, "PUBLIC_SCHEMA_UNAVAILABLE"):
            self.read(reader=SessionReader(incomplete, self.policy))


if __name__ == "__main__":
    unittest.main()
