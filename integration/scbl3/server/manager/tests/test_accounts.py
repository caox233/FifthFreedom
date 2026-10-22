from __future__ import annotations

import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from scblctl.accounts import AccountRecoveryError, AccountRecoveryManager, normalize_recovery_code, recovery_code_hash


class AccountRecoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.database = Path(self.temporary.name) / "users.db"
        with closing(sqlite3.connect(self.database)) as connection, connection:
            connection.execute(
                "CREATE TABLE users (username TEXT PRIMARY KEY, recovery_code_hash TEXT, updated_at TEXT)"
            )
            connection.execute("INSERT INTO users (username) VALUES ('sam')")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_issue_updates_only_hash_and_returns_grouped_code(self) -> None:
        code = AccountRecoveryManager(self.database).issue("sam")
        self.assertEqual(24, len(normalize_recovery_code(code)))
        self.assertEqual(5, code.count("-"))
        with closing(sqlite3.connect(self.database)) as connection:
            stored = connection.execute(
                "SELECT recovery_code_hash FROM users WHERE username = 'sam'"
            ).fetchone()[0]
        self.assertEqual(recovery_code_hash(code), stored)
        self.assertNotIn(normalize_recovery_code(code), stored)

    def test_unknown_account_is_rejected(self) -> None:
        with self.assertRaises(AccountRecoveryError):
            AccountRecoveryManager(self.database).issue("unknown")


if __name__ == "__main__":
    unittest.main()
