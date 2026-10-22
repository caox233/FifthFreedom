from __future__ import annotations

import importlib.util
import os
import tempfile
import time
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/maintenance/clean_workspace.py"
SPEC = importlib.util.spec_from_file_location("clean_workspace", MODULE_PATH)
assert SPEC and SPEC.loader
clean_workspace = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(clean_workspace)


class WorkspaceCleanupTests(unittest.TestCase):
    def test_root_validation_is_included_and_active_or_unknown_paths_are_preserved(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            board = root / "docs/coordination/TASK_COORDINATION.md"
            board.parent.mkdir(parents=True)
            board.write_text(
                "| 任务 | 模式 |\n|---|---|\n| DEV-20260822-ACTIVE | NORMAL |\n",
                encoding="utf-8",
            )
            old = time.time() - 3600
            completed = root / "validation/DEV-20260821-COMPLETED"
            active = root / "validation/DEV-20260822-ACTIVE"
            unknown = root / "validation/cache"
            for path in (completed, active, unknown):
                path.mkdir(parents=True)
                (path / "payload.bin").write_bytes(b"data")
                os.utime(path, (old, old))

            selected, preserved = clean_workspace.candidates(
                root,
                "TESTPC",
                quiescence_minutes=15,
                release_retention_days=3,
                now=time.time(),
            )

            self.assertEqual([completed], [path for path, _size in selected])
            reasons = {path: reason for path, reason in preserved}
            self.assertEqual("active-board-row", reasons[active])
            self.assertEqual("unknown-name", reasons[unknown])

    def test_recent_release_is_preserved_but_expired_release_is_selected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            release_root = root / "temp/TESTPC/release"
            expired = release_root / "QA-20260801-OLD"
            recent = release_root / "QA-20260822-NEW"
            for path in (expired, recent):
                path.mkdir(parents=True)
            now = time.time()
            os.utime(expired, (now - 5 * 86400, now - 5 * 86400))
            os.utime(recent, (now, now))

            selected, preserved = clean_workspace.candidates(
                root,
                "TESTPC",
                quiescence_minutes=15,
                release_retention_days=3,
                now=now,
            )

            self.assertEqual([expired], [path for path, _size in selected])
            self.assertEqual([(recent, "recent-write")], preserved)

    def test_python_cache_candidates_are_exact_and_do_not_follow_symlinks(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = root / "server/tool/__pycache__"
            cache.mkdir(parents=True)
            (cache / "tool.cpython-312.pyc").write_bytes(b"cache")
            ordinary = root / "server/tool/cache"
            ordinary.mkdir()
            (ordinary / "keep.bin").write_bytes(b"keep")

            selected = clean_workspace.python_cache_candidates(root)

            self.assertEqual([cache], [path for path, _size in selected])

    def test_build_cache_candidates_use_only_the_fixed_allowlist(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cargo_target = root / "target"
            launcher_bin = root / "client/ScblPublicLauncher/bin"
            rust_source_bin = root / "client/hooks/src/bin"
            unrelated_obj = root / "scratch/obj"
            for path in (cargo_target, launcher_bin, rust_source_bin, unrelated_obj):
                path.mkdir(parents=True)
                (path / "keep-or-delete.bin").write_bytes(b"data")

            selected = clean_workspace.build_cache_candidates(root)

            self.assertEqual(
                [cargo_target, launcher_bin],
                [path for path, _size in selected],
            )


if __name__ == "__main__":
    unittest.main()
