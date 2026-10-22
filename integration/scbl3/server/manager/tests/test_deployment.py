from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import Mock, patch

from scblctl.config import ServerConfig
from scblctl.deployment import (
    COMPONENT_ARCHIVE_PATHS,
    COMPONENT_TARGETS,
    DeploymentError,
    DeploymentManager,
    _requires_online_guard,
    _installed_manager_version,
    online_package_url,
)
from scblctl.paths import RuntimePaths


class DeploymentTests(unittest.TestCase):
    def make_package(
        self,
        root: Path,
        *,
        kind: str,
        components: list[str],
        include_contract: bool = True,
        tamper: str | None = None,
        integrity_algorithm: str = "SHA256",
    ) -> Path:
        archive = root / f"server-{kind}.zip"
        payloads = {
            component: ("payload:" + component).encode() for component in components
        }
        contract = (
            {
                "programArguments": {
                    "dedicated_server": ["--config"],
                    "easytier-core": [
                        "--config-file",
                        "--rpc-portal",
                        "--console-log-level",
                        "--file-log-level",
                    ],
                    "scbl_update_server.py": [
                        "--root",
                        "--port",
                        "--ipv6",
                        "--bundle-cache",
                    ],
                },
                "databaseMigrations": {"20260808120000": "0" * 96},
            }
            if include_contract
            else None
        )
        manifest = {
            "schemaVersion": 3,
            "packageType": kind,
            "releaseVersion": "3.0.1",
            "integrityPolicy": {"algorithm": integrity_algorithm},
            "components": [
                {
                    "id": component,
                    "version": "3.0.1",
                    "path": COMPONENT_ARCHIVE_PATHS[component],
                    "size": len(payloads[component]),
                    "sha256": hashlib.sha256(payloads[component]).hexdigest(),
                }
                for component in components
            ],
            "runtimeContract": contract,
        }
        manifest_bytes = json.dumps(manifest).encode()
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("scbl-package.json", manifest_bytes)
            for component, payload in payloads.items():
                if component == tamper:
                    payload += b"tampered"
                bundle.writestr(COMPONENT_ARCHIVE_PATHS[component], payload)
        return archive

    def manager(self, root: Path) -> DeploymentManager:
        config = ServerConfig.new(public_host="sc6.example.com")
        return DeploymentManager(
            config,
            RuntimePaths(root / "server.toml", root / "state"),
        )

    def test_full_package_requires_all_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root,
                kind="full",
                components=["server.dedicated"],
            )
            with self.assertRaisesRegex(DeploymentError, "完整安装包组件集合不完整"):
                self.manager(root).verify(package, expected_kind="full")

    def test_update_can_contain_only_changed_component(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root,
                kind="update",
                components=["server.dedicated"],
            )
            verified = self.manager(root).verify(package, expected_kind="patch")
            self.assertEqual("update", verified.package_type)
            self.assertEqual("server.dedicated", verified.artifacts[0].component)
            self.assertEqual("dedicated_server", verified.artifacts[0].target)

    def test_runtime_component_requires_runtime_contract(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root,
                kind="update",
                components=["server.dedicated"],
                include_contract=False,
            )
            with self.assertRaisesRegex(DeploymentError, "runtimeContract"):
                self.manager(root).verify(package, expected_kind="patch")

    def test_component_bytes_are_bound_to_manifest_sha256(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root,
                kind="update",
                components=["server.control-plane"],
                tamper="server.control-plane",
            )
            with self.assertRaisesRegex(DeploymentError, "大小不符"):
                self.manager(root).verify(package, expected_kind="patch")

    def test_manifest_requires_explicit_sha256_integrity_policy(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root,
                kind="update",
                components=["server.manager"],
                include_contract=False,
                integrity_algorithm="none",
            )
            with self.assertRaisesRegex(DeploymentError, "integrityPolicy"):
                self.manager(root).verify(package, expected_kind="patch")

    def test_online_urls_use_one_package_stream_for_both_server_modes(self) -> None:
        config = ServerConfig.new(public_host="sc6.example.com", mode="test")
        self.assertTrue(
            online_package_url(config, kind="full").endswith(
                "/server-latest/SCBL-Server-full.zip"
            )
        )
        self.assertTrue(
            online_package_url(config, kind="patch").endswith(
                "/server-latest/SCBL-Server-update.zip"
            )
        )
        config.server.mode = "stable"
        self.assertTrue(
            online_package_url(config, kind="full").endswith(
                "/server-latest/SCBL-Server-full.zip"
            )
        )

    def test_online_guard_is_automatic_only_for_stable_servers(self) -> None:
        test_config = ServerConfig.new(public_host="sc6.example.com", mode="test")
        stable_config = ServerConfig.new(public_host="sc6.example.com", mode="stable")

        self.assertFalse(_requires_online_guard(test_config, allow_online=False))
        self.assertTrue(_requires_online_guard(stable_config, allow_online=False))
        self.assertFalse(_requires_online_guard(stable_config, allow_online=True))

    def test_installed_manager_version_reads_the_installed_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            manager = Path(temporary) / "scblctl.pyz"
            manager.write_bytes(b"placeholder")
            completed = Mock(returncode=0, stdout="SCBL 3.0.0\n")
            with patch("scblctl.deployment.subprocess.run", return_value=completed) as run:
                self.assertEqual("3.0.0", _installed_manager_version(manager))
            run.assert_called_once_with(
                ("python3", str(manager), "--version"),
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=15,
                check=False,
            )

    def test_component_targets_cover_exact_supported_set(self) -> None:
        self.assertEqual(
            {
                "server.manager",
                "server.dedicated",
                "server.control-plane",
                "server.update-server",
                "server.assets",
            },
            set(COMPONENT_TARGETS),
        )

    def test_same_version_with_different_bytes_is_rejected(self) -> None:
        manager = self.manager(Path("."))
        artifact = Mock(
            component="server.control-plane",
            version="3.0.1",
            sha256="b" * 64,
        )
        package = Mock(artifacts=(artifact,))
        with patch(
            "scblctl.deployment.installed_components",
            return_value={"server.control-plane": ("3.0.1", "a" * 64)},
        ):
            with self.assertRaisesRegex(DeploymentError, "同版本不同字节"):
                manager._validate_versions(package, operation="repair")

    def test_update_rejects_mixed_manager_and_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.make_package(
                root,
                kind="update",
                components=["server.manager", "server.control-plane"],
            )
            manager = self.manager(root)
            with patch.object(manager, "_validate_versions"):
                with patch("scblctl.deployment.Path.is_symlink", return_value=True):
                    with self.assertRaisesRegex(DeploymentError, "独立组件更新包"):
                        manager.apply(package, operation="update")


if __name__ == "__main__":
    unittest.main()
