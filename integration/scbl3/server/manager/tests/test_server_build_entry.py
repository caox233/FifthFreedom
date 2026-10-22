from __future__ import annotations

import importlib.util
import tempfile
import unittest
import zipfile
from pathlib import Path
import sys
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "server" / "packaging" / "scbl-server.py"
sys.path.insert(0, str(SCRIPT.parent))
SPEC = importlib.util.spec_from_file_location("scbl_server_build", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ServerBuildEntryTests(unittest.TestCase):
    def test_control_plane_builds_as_one_modular_zipapp(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "scbl_control_plane.pyz"
            MODULE.build_control_plane_archive(output)

            self.assertTrue(output.is_file())
            with zipfile.ZipFile(output, "r") as archive:
                self.assertEqual(
                    {
                        "__main__.py",
                        "control_plane_auth.py",
                        "control_plane_domain.py",
                        "control_plane_gameplay.py",
                        "control_plane_runtime.py",
                    },
                    set(archive.namelist()),
                )

    def test_easytier_is_not_a_server_package_component(self) -> None:
        choices = MODULE.parser()._subparsers._group_actions[0].choices["component"]._actions[1].choices
        self.assertNotIn("easytier", choices)

    def test_component_command_accepts_multiple_unique_runtime_components(self) -> None:
        parsed = MODULE.parser().parse_args(
            ["component", "dedicated", "control-plane"]
        )
        self.assertEqual(["dedicated", "control-plane"], parsed.components)

    def test_component_command_rejects_duplicate_components(self) -> None:
        with patch.object(MODULE.os, "name", "posix"):
            with self.assertRaisesRegex(SystemExit, "must be unique"):
                MODULE.main(["component", "dedicated", "dedicated"])

    def test_component_command_rejects_manager_runtime_mix(self) -> None:
        with patch.object(MODULE.os, "name", "posix"):
            with self.assertRaisesRegex(SystemExit, "Manager must use its own"):
                MODULE.main(["component", "manager", "control-plane"])

    def test_existing_server_update_is_one_explicit_runtime_operation(self) -> None:
        parsed = MODULE.parser().parse_args(["update"])
        self.assertEqual("update", parsed.operation)
        self.assertEqual(
            Path("/opt/scbl/current/server-package.json"),
            parsed.runtime_manifest,
        )

    def test_dedicated_build_tools_fail_before_compilation(self) -> None:
        with patch.object(
            MODULE,
            "resolve_build_tool",
            side_effect=lambda name: None if name == "protoc" else "/tool",
        ):
            with self.assertRaisesRegex(SystemExit, "protoc.*protobuf-compiler"):
                MODULE.require_dedicated_build_tools()

    def test_cargo_falls_back_to_the_rustup_home_without_shell_sourcing(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            cargo = Path(temporary) / ".cargo/bin/cargo"
            cargo.parent.mkdir(parents=True)
            cargo.write_text("cargo", encoding="ascii")
            cargo.chmod(0o755)
            with patch.object(MODULE.shutil, "which", return_value=None), patch.object(
                MODULE.Path, "home", return_value=Path(temporary)
            ):
                self.assertEqual(str(cargo), MODULE.resolve_build_tool("cargo"))

    def test_installer_bundle_contains_menu_core_installer_and_full_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source_root = root / "source"
            bootstrap = source_root / "server/bootstrap"
            bootstrap.mkdir(parents=True)
            (bootstrap / "setup.sh").write_text("#!/usr/bin/env bash\n", encoding="utf-8")
            (bootstrap / "install.sh").write_text("#!/usr/bin/env bash\n", encoding="utf-8")
            output_dir = root / "output"
            output_dir.mkdir()
            full = output_dir / "SCBL-Server-v3.0.11-full.zip"
            full.write_bytes(b"full-package")
            full.with_name(full.name + ".sha256").write_text(
                "0" * 64 + f"  {full.name}\n", encoding="ascii"
            )

            bundle, checksum = MODULE.build_installer_bundle(
                full, output_dir, "3.0.11", source_root=source_root
            )

            self.assertEqual("SCBL-Server-v3.0.11-installer.zip", bundle.name)
            self.assertTrue(checksum.is_file())
            with zipfile.ZipFile(bundle, "r") as archive:
                self.assertEqual(
                    {
                        "SCBL-Installer.sh",
                        "install.sh",
                        full.name,
                        full.name + ".sha256",
                    },
                    set(archive.namelist()),
                )
                self.assertEqual(b"full-package", archive.read(full.name))

    def test_installer_bundle_can_include_one_matching_client_release(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source_root = root / "source"
            bootstrap = source_root / "server/bootstrap"
            bootstrap.mkdir(parents=True)
            (bootstrap / "setup.sh").write_text("#!/usr/bin/env bash\n", encoding="utf-8")
            (bootstrap / "install.sh").write_text("#!/usr/bin/env bash\n", encoding="utf-8")
            output_dir = root / "output"
            output_dir.mkdir()
            full = output_dir / "SCBL-Server-v3.0.12-full.zip"
            full.write_bytes(b"server")
            full.with_name(full.name + ".sha256").write_text(
                "0" * 64 + f"  {full.name}\n", encoding="ascii"
            )
            client = root / "SCBL-Client-v3.0.4-win-x86.zip"
            patch_file = root / "SCBL-Client-v3.0.4-components.scblpatch"
            client.write_bytes(b"client")
            patch_file.write_bytes(b"patch")

            bundle, _checksum = MODULE.build_installer_bundle(
                full,
                output_dir,
                "3.0.12",
                source_root=source_root,
                client_package=client,
                client_patch=patch_file,
            )

            with zipfile.ZipFile(bundle, "r") as archive:
                self.assertIn(client.name, archive.namelist())
                self.assertIn(patch_file.name, archive.namelist())

    def test_installer_bundle_rejects_half_client_release(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source_root = root / "source"
            bootstrap = source_root / "server/bootstrap"
            bootstrap.mkdir(parents=True)
            (bootstrap / "setup.sh").write_text("#!/usr/bin/env bash\n", encoding="utf-8")
            (bootstrap / "install.sh").write_text("#!/usr/bin/env bash\n", encoding="utf-8")
            output_dir = root / "output"
            output_dir.mkdir()
            full = output_dir / "SCBL-Server-v3.0.12-full.zip"
            full.write_bytes(b"server")
            full.with_name(full.name + ".sha256").write_text(
                "0" * 64 + f"  {full.name}\n", encoding="ascii"
            )
            client = root / "SCBL-Client-v3.0.4-win-x86.zip"
            client.write_bytes(b"client")

            with self.assertRaisesRegex(SystemExit, "supplied together"):
                MODULE.build_installer_bundle(
                    full,
                    output_dir,
                    "3.0.12",
                    source_root=source_root,
                    client_package=client,
                )

    def test_interactive_installer_uses_short_confirmation_and_safe_repair(self) -> None:
        setup = (ROOT / "server/bootstrap/setup.sh").read_text(encoding="utf-8")
        installer = (ROOT / "server/bootstrap/install.sh").read_text(encoding="utf-8")

        self.assertNotIn("请输入 INSTALL", setup)
        self.assertIn("确认这是全新服务器并开始安装 [Y/n]", setup)
        self.assertIn("确认执行强制修复安装 [y/N]", setup)
        self.assertIn("--force-reinstall", setup)
        self.assertIn("deploy repair --package", installer)
        self.assertIn("保留现有配置、网络密钥和数据库", installer)
        self.assertIn("SCBL publish-components", setup)
        self.assertIn("SCBL publish-client", setup)
        self.assertIn("是否现在安装并配置 IPv6 DDNS-Go [y/N]", setup)
        self.assertIn("publish_bundled_client_if_empty", setup)
        self.assertIn("修复安装保持原发布内容，不重复覆盖", setup)


if __name__ == "__main__":
    unittest.main()
