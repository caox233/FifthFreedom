from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scblctl.backup import BackupManager
from scblctl.paths import RuntimePaths


class BackupTests(unittest.TestCase):
    def test_list_is_newest_first_and_ignores_unrelated_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = BackupManager(RuntimePaths(root / "server.toml", root / "state"))
            manager.backup_root = root
            older = root / "scbl-backup-20260801T000000Z.tar.gz"
            newer = root / "scbl-backup-20260802T000000Z.tar.gz"
            older.write_bytes(b"old")
            newer.write_bytes(b"new")
            (root / "other.tar.gz").write_bytes(b"ignore")
            older.touch()
            newer.touch()
            newer_time = older.stat().st_mtime + 10
            import os

            os.utime(newer, (newer_time, newer_time))
            self.assertEqual([newer, older], [item.path for item in manager.list()])

    def test_default_prune_keeps_three_backups(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = BackupManager(RuntimePaths(root / "server.toml", root / "state"))
            manager.backup_root = root
            for day in range(1, 6):
                archive = root / f"scbl-backup-202608{day:02d}T000000Z.tar.gz"
                archive.write_bytes(str(day).encode())
                archive.with_name(archive.name + ".sha256").write_text("digest")
            with patch.object(BackupManager, "require_root"):
                removed = manager.prune()
            self.assertEqual(2, len(removed))
            self.assertEqual(3, len(manager.list()))
            self.assertFalse(any(path.with_name(path.name + ".sha256").exists() for path in removed))


if __name__ == "__main__":
    unittest.main()
