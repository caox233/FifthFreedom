from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from scblctl.release import (
    MANIFEST_NAME,
    REQUIRED_RUNTIME_FILES,
    UNIT_PROGRAM_ARGUMENTS,
    ReleaseError,
    RuntimeManifest,
    create_runtime_manifest,
)


class ReleaseTests(unittest.TestCase):
    PROGRAM_ARGUMENTS = dict(UNIT_PROGRAM_ARGUMENTS)
    DATABASE_MIGRATIONS = {20260808120000: "1" * 96}

    def make_package(self, root: Path) -> None:
        for name in REQUIRED_RUNTIME_FILES:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"payload:{name}".encode())

    def test_create_load_and_verify_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            created = self.create_manifest(root)
            loaded = RuntimeManifest.load(root)
            self.assertEqual(created, loaded)
            loaded.verify(root)
            self.assertTrue((root / MANIFEST_NAME).exists())

    def test_staged_release_is_traversable_by_service_users(self) -> None:
        if __import__("os").name != "posix":
            self.skipTest("POSIX permission assertion")
        from scblctl.release import stage_release

        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            package = workspace / "package"
            package.mkdir()
            self.make_package(package)
            self.create_manifest(package)
            target, _ = stage_release(package, workspace / "releases")
            self.assertEqual(0o755, target.stat().st_mode & 0o777)
            self.assertEqual(0o755, (target / "data").stat().st_mode & 0o777)

    def test_modified_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            self.create_manifest(root)
            (root / "dedicated_server").write_bytes(b"modified")
            with self.assertRaisesRegex(ReleaseError, "校验失败"):
                RuntimeManifest.load(root).verify(root)

    def test_missing_required_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            (root / "scbl_update_server.py").unlink()
            with self.assertRaisesRegex(ReleaseError, "包外程序"):
                self.create_manifest(root)

    def test_manifest_rejects_path_traversal(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / MANIFEST_NAME).write_text(
                '{"schemaVersion":2,"packageType":"scbl-server-runtime",'
                '"version":"2.0.0","files":{"../escape":"' + "0" * 64 + '"},'
                '"programArguments":{"dedicated_server":["--config"],'
                '"scbl_update_server.py":["--root","--port","--ipv6"]},'
                '"databaseMigrations":{"1":"' + "0" * 96 + '"}}',
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ReleaseError, "不安全"):
                RuntimeManifest.load(root)

    def test_schema1_manifest_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.make_package(root)
            files = {
                name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                for name in REQUIRED_RUNTIME_FILES
            }
            (root / MANIFEST_NAME).write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "packageType": "scbl-server-runtime",
                        "version": "2.0.1",
                        "files": files,
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ReleaseError, "不支持"):
                RuntimeManifest.load(root)

    def create_manifest(self, root: Path) -> RuntimeManifest:
        return create_runtime_manifest(
            root,
            "2.0.0",
            program_arguments=self.PROGRAM_ARGUMENTS,
            database_migrations=self.DATABASE_MIGRATIONS,
        )


if __name__ == "__main__":
    unittest.main()
