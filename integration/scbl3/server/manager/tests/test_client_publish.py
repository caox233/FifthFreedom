from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from scblctl.client_publish import CLIENT_MANIFEST, REQUIRED_FILES, ClientPublishError, ClientPublisher


class ClientPublishTests(unittest.TestCase):
    def make_package(self, root: Path, *, tamper: bool = False, version: str = "2.0.0") -> Path:
        archive = root / "client.zip"
        files = {name: f"payload:{name}".encode() for name in REQUIRED_FILES}
        manifest = {
            "schemaVersion": 1,
            "clientVersion": version,
            "generatedAt": "2026-08-04T00:00:00Z",
            "bootstrapHooksSha256": "0" * 64,
            "files": [
                {
                    "path": name,
                    "size": len(content),
                    "sha256": hashlib.sha256(content).hexdigest(),
                }
                for name, content in sorted(files.items())
            ],
        }
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
            for name, content in files.items():
                bundle.writestr(name, content + (b"tampered" if tamper and name.endswith(".dll") else b""))
            bundle.writestr(CLIENT_MANIFEST, json.dumps(manifest))
        return archive

    def test_verify_complete_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            package = ClientPublisher().verify(self.make_package(Path(temporary)))
            self.assertEqual("2.0.0", package.version)
            self.assertRegex(package.sha256, r"^[0-9a-f]{64}$")

    def test_verify_rejects_tampered_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ClientPublishError, "大小不符|SHA256 不符"):
                ClientPublisher().verify(self.make_package(Path(temporary), tamper=True))

    def test_verify_rejects_undeclared_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = self.make_package(Path(temporary))
            with zipfile.ZipFile(archive, "a") as bundle:
                bundle.writestr("unexpected.txt", b"unexpected")
            with self.assertRaisesRegex(ClientPublishError, "与清单不一致"):
                ClientPublisher().verify(archive)

    def test_publish_exposes_one_current_version_and_matching_update_announcement(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(root)
            update_root = root / "updates"
            update_root.mkdir()
            (update_root / "client_update_manifest.json").write_text(
                json.dumps({"version": "1.0.0"}), encoding="utf-8"
            )
            (update_root / "update_announcement.json").write_text(
                json.dumps(
                    {
                        "enabled": True,
                        "version": "2.0.0",
                        "title": "更新通知",
                        "body": "请安装正式版",
                        "title_en": "Update",
                        "body_en": "Install the formal release",
                    }
                ),
                encoding="utf-8",
            )
            publisher = ClientPublisher(update_root)
            with patch.object(ClientPublisher, "require_root"):
                publisher.publish(package)
            manifest = json.loads(publisher.manifest_path.read_text(encoding="utf-8"))
            self.assertEqual("2.0.0", manifest["version"])
            self.assertNotIn("minimumVersion", manifest)
            self.assertEqual(["客户端功能与稳定性更新"], manifest["releaseNotes"])
            self.assertEqual(["Client features and stability update"], manifest["releaseNotesEn"])
            self.assertTrue(manifest["updateAnnouncement"]["enabled"])
            self.assertNotIn("networkBootstrap", manifest)
            self.assertEqual("SCBL.zip", manifest["fullPackage"])
            self.assertEqual(
                "releases/2.0.0/SCBL-Client-v2.0.0-win-x86.zip",
                manifest["immutableFullPackage"],
            )
            self.assertEqual((update_root / manifest["fullPackage"]).stat().st_size, manifest["fullPackageSize"])
            self.assertEqual(
                (update_root / manifest["fullPackage"]).stat().st_ino,
                (update_root / manifest["immutableFullPackage"]).stat().st_ino,
            )

    def test_published_files_keep_shared_scbl_group(self) -> None:
        account = SimpleNamespace(pw_uid=1201)
        group = SimpleNamespace(gr_gid=1202)
        target = Path("/var/lib/scbl/client-updates/client_update_manifest.json")
        with (
            patch("scblctl.client_publish.pwd") as pwd_module,
            patch("scblctl.client_publish.grp") as grp_module,
            patch("scblctl.client_publish.os.chown", create=True) as chown,
        ):
            pwd_module.getpwnam.return_value = account
            grp_module.getgrnam.return_value = group
            ClientPublisher._set_update_owner(target)
        chown.assert_called_once_with(target, 1201, 1202)

    def test_publish_creates_matching_update_announcement_when_missing(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            update_root = root / "updates"
            update_root.mkdir()
            publisher = ClientPublisher(update_root)
            with patch.object(ClientPublisher, "require_root"):
                publisher.publish(
                    self.make_package(root, version="3.0.0"),
                    release_notes=["修复首次安装更新流程"],
                    release_notes_en=["Fix first-install update flow"],
                )
            announcement = json.loads(
                (update_root / "update_announcement.json").read_text(encoding="utf-8")
            )
            manifest = json.loads(publisher.manifest_path.read_text(encoding="utf-8"))
            self.assertTrue(announcement["enabled"])
            self.assertEqual("3.0.0", announcement["version"])
            self.assertEqual("修复首次安装更新流程", announcement["body"])
            self.assertEqual("Fix first-install update flow", announcement["body_en"])
            self.assertEqual("修复首次安装更新流程", manifest["updateAnnouncement"]["body"])

    def test_complete_package_can_roll_back_and_reuse_identical_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            update_root = root / "updates"
            update_root.mkdir()
            publisher = ClientPublisher(update_root)
            with patch.object(ClientPublisher, "require_root"):
                publisher.publish(self.make_package(root, version="2.0.2"))
                rollback = self.make_package(root, version="2.0.1")
                publisher.publish(rollback)
                publisher.publish(rollback)
            self.assertEqual("2.0.1", publisher.current_version())

    def test_uploaded_full_package_atomically_replaces_fixed_download(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            update_root = root / "updates"
            update_root.mkdir()
            publisher = ClientPublisher(update_root)
            with patch.object(ClientPublisher, "require_root"):
                first = publisher.publish(self.make_package(root, version="2.0.0"))
                first_digest = hashlib.sha256((update_root / "SCBL.zip").read_bytes()).hexdigest()
                second = publisher.publish(self.make_package(root, version="2.0.1"))
            self.assertNotEqual(first.sha256, second.sha256)
            self.assertNotEqual(first_digest, hashlib.sha256((update_root / "SCBL.zip").read_bytes()).hexdigest())
            self.assertEqual(second.sha256, hashlib.sha256((update_root / "SCBL.zip").read_bytes()).hexdigest())

    def test_complete_package_history_keeps_latest_three(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            update_root = root / "updates"
            update_root.mkdir()
            publisher = ClientPublisher(update_root)
            with patch.object(ClientPublisher, "require_root"):
                for version in ("2.0.0", "2.0.1", "2.0.2", "2.0.3"):
                    publisher.publish(self.make_package(root, version=version))
            self.assertEqual(
                ["2.0.1", "2.0.2", "2.0.3"],
                sorted(path.name for path in (update_root / "releases").iterdir()),
            )

    def test_descriptor_failure_restores_manifest_and_new_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            update_root = root / "updates"
            update_root.mkdir()
            manifest = update_root / "client_update_manifest.json"
            manifest.write_bytes(b'{"version":"1.0.0"}\n')
            publisher = ClientPublisher(update_root)
            with (
                patch.object(ClientPublisher, "require_root"),
                patch(
                    "scblctl.server_descriptor.refresh_server_descriptor",
                    side_effect=RuntimeError("descriptor failed"),
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "descriptor failed"):
                    publisher.publish(self.make_package(root, version="2.0.0"))
            self.assertEqual(b'{"version":"1.0.0"}\n', manifest.read_bytes())
            self.assertFalse((update_root / "releases" / "2.0.0").exists())
            self.assertFalse((update_root / "update_announcement.json").exists())

    def test_channel_publish_requires_every_component_before_full_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            update_root = root / "updates"
            update_root.mkdir()
            publisher = ClientPublisher(update_root, channel="test")
            with self.assertRaisesRegex(ClientPublishError, "全部组件补丁"):
                publisher.require_component_baseline()


if __name__ == "__main__":
    unittest.main()
