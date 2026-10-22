from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from scblctl.announcements import AnnouncementError, AnnouncementManager


class AnnouncementTests(unittest.TestCase):
    def make_manager(self, root: Path) -> AnnouncementManager:
        (root / "client_update_manifest.json").write_text(
            json.dumps(
                {
                    "version": "2.0.0",
                    "updateMode": "full-package",
                    "fullPackage": "releases/2.0.0/client.zip",
                    "fullPackageSha256": "0" * 64,
                }
            ),
            encoding="utf-8",
        )
        return AnnouncementManager(root, refresh_descriptor=False)

    def test_active_announcement_is_written_atomically_with_launcher_schema(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            manager = self.make_manager(Path(temporary))
            status = manager.set(
                "active", title="维护通知", body="今晚维护", level="warning"
            )
            self.assertTrue(status.enabled)
            payload = json.loads(status.path.read_text(encoding="utf-8"))
            self.assertEqual("warning", payload["level"])
            self.assertFalse(payload["showOnce"])

    def test_active_ticker_accepts_text_beyond_previous_character_limit(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            manager = self.make_manager(Path(temporary))
            long_title = "公告" * 100
            long_body = "不限字数的滚动内容" * 600

            status = manager.set("active", title=long_title, body=long_body)

            self.assertEqual(long_title, status.title)
            self.assertEqual(long_body, status.body)

    def test_update_announcement_is_embedded_only_for_matching_client(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = self.make_manager(root)
            manager.set("update", title="更新", body="请更新", version="2.0.0")
            manifest = json.loads(
                (root / "client_update_manifest.json").read_text(encoding="utf-8")
            )
            self.assertTrue(manifest["updateAnnouncement"]["enabled"])
            self.assertEqual(
                {"enabled": False}, manager.update_payload_for_version("2.0.1")
            )

    def test_component_announcement_can_leave_full_package_manifest_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = self.make_manager(root)
            manifest_path = root / "client_update_manifest.json"
            before = manifest_path.read_bytes()

            status = manager.set(
                "update",
                title="组件更新",
                body="仅更新组件",
                version="2.0.1",
                sync_client_manifest=False,
            )

            self.assertTrue(status.enabled)
            self.assertEqual("2.0.1", status.version)
            self.assertEqual(before, manifest_path.read_bytes())

    def test_empty_announcement_cannot_be_enabled(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            manager = self.make_manager(Path(temporary))
            manager.clear("startup")
            with self.assertRaisesRegex(AnnouncementError, "不能启用"):
                manager.set_enabled("startup", True)

    def test_announcement_files_keep_shared_scbl_group(self) -> None:
        account = SimpleNamespace(pw_uid=1201)
        group = SimpleNamespace(gr_gid=1202)
        target = Path("/var/lib/scbl/client-updates/active_announcement.json")
        with (
            patch("scblctl.announcements.pwd") as pwd_module,
            patch("scblctl.announcements.grp") as grp_module,
            patch("scblctl.announcements.os.chown", create=True) as chown,
        ):
            pwd_module.getpwnam.return_value = account
            grp_module.getgrnam.return_value = group
            AnnouncementManager._set_owner(target)
        chown.assert_called_once_with(target, 1201, 1202)

    def test_descriptor_failure_restores_announcement_and_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = self.make_manager(root)
            manager.set(
                "update", title="旧公告", body="旧内容", version="2.0.0"
            )
            announcement = manager.path_for("update")
            manifest = root / "client_update_manifest.json"
            before_announcement = announcement.read_bytes()
            before_manifest = manifest.read_bytes()
            manager = AnnouncementManager(root, refresh_descriptor=True)
            with patch(
                "scblctl.server_descriptor.refresh_server_descriptor",
                side_effect=RuntimeError("descriptor failed"),
            ):
                with self.assertRaisesRegex(RuntimeError, "descriptor failed"):
                    manager.set(
                        "update", title="新公告", body="新内容", version="2.0.0"
                    )
            self.assertEqual(before_announcement, announcement.read_bytes())
            self.assertEqual(before_manifest, manifest.read_bytes())


if __name__ == "__main__":
    unittest.main()
