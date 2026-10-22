from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from scblctl.config import ServerConfig
from scblctl.server_descriptor import ServerDescriptorError, ServerDescriptorManager


class ServerDescriptorTests(unittest.TestCase):
    def _write_channel(self, root: Path, channel: str, version: str) -> str:
        artifact = root / "components" / "artifacts" / "launcher" / version / "SCBL.exe"
        artifact.parent.mkdir(parents=True, exist_ok=True)
        artifact.write_bytes(f"launcher-{channel}-{version}".encode())
        payload = {
            "schemaVersion": 2,
            "channel": channel,
            "generatedAt": "2026-08-22T00:00:00Z",
            "networkBootstrap": {"obsolete": True},
            "components": {
                "launcher": {
                    "version": version,
                    "sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
                    "size": artifact.stat().st_size,
                    "url": f"/components/artifacts/launcher/{version}/SCBL.exe",
                    "updateMode": "next-launch",
                    "required": True,
                }
            },
            "files": {},
        }
        raw = (json.dumps(payload, ensure_ascii=False) + "\n").encode()
        path = root / "components" / "channels" / channel / "client_components_v2.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
        return hashlib.sha256(raw).hexdigest()

    def test_single_document_merges_server_client_network_and_announcements(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stable_generation = self._write_channel(root, "stable", "3.0.4")
            self._write_channel(root, "test", "3.1.0")
            package = root / "releases" / "3.0.4" / "SCBL-Client-v3.0.4-win-x86.zip"
            package.parent.mkdir(parents=True)
            package.write_bytes(b"PK-test-package")
            (root / "client_update_manifest.json").write_text(
                json.dumps(
                    {
                        "version": "3.0.4",
                        "fullPackage": package.relative_to(root).as_posix(),
                        "fullPackageSha256": hashlib.sha256(package.read_bytes()).hexdigest(),
                        "fullPackageSize": package.stat().st_size,
                    }
                ),
                encoding="utf-8",
            )
            (root / "active_announcement.json").write_text(
                json.dumps({"enabled": True, "id": "notice-1", "title": "公告", "body": "欢迎"}),
                encoding="utf-8",
            )
            config = ServerConfig.new(public_host="sc6.example.com", mode="stable")
            config.network.secret = "server-specific-network-secret"
            config.services.public_update_base_url = "http://sc6.example.com:18080"
            path = ServerDescriptorManager(root).write(config)
            payload = json.loads(path.read_text(encoding="utf-8"))

            self.assertEqual(1, payload["schemaVersion"])
            self.assertEqual("stable", payload["server"]["mode"])
            self.assertEqual("3.0.4", payload["client"]["version"])
            self.assertEqual(stable_generation, payload["client"]["componentGeneration"])
            self.assertEqual("sc6.example.com", payload["network"]["publicHost"])
            self.assertEqual(["quic", "wss"], payload["network"]["transports"])
            self.assertEqual(21126, payload["dedicated"]["gamePort"])
            self.assertEqual(50051, payload["dedicated"]["authPort"])
            self.assertNotIn("gameplay", payload)
            self.assertEqual("公告", payload["announcements"]["active"]["title"])
            self.assertEqual("3.0.4", payload["announcements"]["update"]["version"])
            self.assertEqual("update-3.0.4", payload["announcements"]["update"]["id"])
            self.assertNotIn("networkBootstrap", json.dumps(payload))
            self.assertNotIn("scbl-features.toml", json.dumps(payload))

    def test_custom_update_notice_must_match_current_client_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_channel(root, "stable", "3.0.5")
            (root / "update_announcement.json").write_text(
                json.dumps(
                    {
                        "enabled": True,
                        "id": "old-update",
                        "version": "3.0.4",
                        "title": "旧公告",
                        "body": "不能描述新版本",
                    }
                ),
                encoding="utf-8",
            )

            payload = ServerDescriptorManager(root).build(ServerConfig.new())

            self.assertEqual("update-3.0.5", payload["announcements"]["update"]["id"])
            self.assertEqual("3.0.5", payload["announcements"]["update"]["version"])

    def test_server_mode_selects_only_its_latest_channel(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_channel(root, "stable", "3.0.0")
            self._write_channel(root, "test", "3.1.0")
            config = ServerConfig.new(mode="test", server_id="test-server")
            payload = ServerDescriptorManager(root).build(config)
            self.assertEqual("3.1.0", payload["client"]["version"])
            self.assertEqual("test", payload["server"]["mode"])
            self.assertTrue(payload["network"]["useOriginHost"])

    def test_legacy_component_fields_are_rejected_instead_of_silently_carried(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_channel(root, "stable", "3.0.0")
            path = root / "components" / "channels" / "stable" / "client_components_v2.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["components"]["launcher"]["minLauncherVersion"] = "2.0.0"
            path.write_text(json.dumps(payload), encoding="utf-8")

            with self.assertRaisesRegex(ServerDescriptorError, "未知字段"):
                ServerDescriptorManager(root).build(ServerConfig.new())

    def test_full_package_path_must_stay_under_update_root(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_channel(root, "stable", "3.0.0")
            (root / "client_update_manifest.json").write_text(
                json.dumps(
                    {
                        "version": "3.0.0",
                        "fullPackage": "../escape.zip",
                        "fullPackageSha256": "0" * 64,
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ServerDescriptorError, "路径"):
                ServerDescriptorManager(root).build(ServerConfig.new())

    def test_component_or_full_package_drift_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_channel(root, "stable", "3.0.0")
            component = root / "components" / "artifacts" / "launcher" / "3.0.0" / "SCBL.exe"
            component.write_bytes(b"tampered")
            with self.assertRaisesRegex(ServerDescriptorError, "组件文件"):
                ServerDescriptorManager(root).build(ServerConfig.new())

    def test_corrupt_dependency_keeps_previous_public_descriptor(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._write_channel(root, "stable", "3.0.0")
            manager = ServerDescriptorManager(root)
            config = ServerConfig.new()
            manager.write(config)
            before = manager.path.read_bytes()

            (root / "active_announcement.json").write_text("{broken", encoding="utf-8")
            with self.assertRaisesRegex(ServerDescriptorError, "公告不是有效 JSON"):
                manager.write(config)
            self.assertEqual(before, manager.path.read_bytes())

            (root / "active_announcement.json").unlink()
            (root / "client_update_manifest.json").write_text("[]", encoding="utf-8")
            with self.assertRaisesRegex(ServerDescriptorError, "完整包清单必须是 JSON 对象"):
                manager.write(config)
            self.assertEqual(before, manager.path.read_bytes())


if __name__ == "__main__":
    unittest.main()
