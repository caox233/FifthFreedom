from __future__ import annotations

import importlib.util
import json
import struct
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/coordination/scbl_task.py"
CMD_PATH = ROOT / "scripts/coordination/scbl-task.cmd"

SPEC = importlib.util.spec_from_file_location("scbl_task_entry", MODULE_PATH)
assert SPEC and SPEC.loader
scbl_task = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = scbl_task
SPEC.loader.exec_module(scbl_task)


def write_toolchain_config(root: Path, channel: str = "nightly-2025-10-15") -> None:
    root.mkdir(parents=True, exist_ok=True)
    (root / "rust-toolchain.toml").write_text(
        f'[toolchain]\nchannel = "{channel}"\n', encoding="utf-8"
    )


def create_fake_toolchain(tmp_path: Path, channel: str = "nightly-2025-10-15"):
    repository = tmp_path / "repository with spaces"
    write_toolchain_config(repository, channel)
    rustup = tmp_path / "profile with spaces" / ".rustup"
    toolchain = rustup / "toolchains" / f"{channel}-x86_64-pc-windows-msvc"
    bin_dir = toolchain / "bin"
    bin_dir.mkdir(parents=True)
    paths = tuple(bin_dir / scbl_task.executable_name(name) for name in ("cargo", "rustc", "rustfmt"))
    for path in paths:
        path.write_bytes(b"test tool placeholder")
    return repository, rustup, toolchain, paths


def nightly_probe(command, **_kwargs):
    name = Path(command[0]).stem.lower()
    return SimpleNamespace(
        returncode=0,
        stdout=f"{name} 1.92.0-nightly (test 2025-10-14)\n",
        stderr="",
    )


def run_cmd(script: Path, *arguments: str, environment: dict[str, str] | None = None):
    if os.name != "nt":
        raise unittest.SkipTest("the bootstrap is a Windows cmd script")
    return subprocess.run(
        [os.environ.get("COMSPEC", "cmd.exe"), "/d", "/c", "call", str(script), *arguments],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=environment,
    )


def write_test_x86_dll(path: Path, markers: tuple[bytes, ...]) -> None:
    content = bytearray(1024)
    content[:2] = b"MZ"
    struct.pack_into("<I", content, 0x3C, 0x80)
    content[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", content, 0x84, 0x014C)
    struct.pack_into("<H", content, 0x96, 0x2000)
    offset = 0x100
    for marker in markers:
        content[offset:offset + len(marker)] = marker
        offset += len(marker) + 1
    path.write_bytes(content)


def write_test_hooks_dll(path: Path) -> None:
    write_test_x86_dll(path, (scbl_task.HOOKS_CONFIG_MARKER,))


def write_test_plugin_dll(path: Path) -> None:
    write_test_x86_dll(
        path,
        (scbl_task.PLUGIN_INITIALIZE_MARKER, scbl_task.PLUGIN_SHUTDOWN_MARKER),
    )


class CoordinationToolTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.tmp_path = Path(self.temporary_directory.name)

    def tearDown(self):
        self.temporary_directory.cleanup()

    def test_pinned_toolchain_is_resolved_from_paths_with_spaces(self):
        repository, rustup, toolchain, paths = create_fake_toolchain(self.tmp_path)
        tools = scbl_task.resolve_rust_tools(
            repository, {"RUSTUP_HOME": str(rustup)}, runner=nightly_probe
        )
        self.assertEqual(tools.root, toolchain)
        self.assertEqual((tools.cargo, tools.rustc, tools.rustfmt), paths)
        self.assertTrue(all("nightly" in version for version in tools.versions))

    def test_missing_tool_fails_closed(self):
        repository, rustup, _toolchain, paths = create_fake_toolchain(self.tmp_path)
        paths[-1].unlink()
        with self.assertRaisesRegex(scbl_task.ToolchainError, "tool is missing"):
            scbl_task.resolve_rust_tools(
                repository, {"RUSTUP_HOME": str(rustup)}, runner=nightly_probe
            )

    def test_tool_resolving_outside_pinned_bin_fails_closed(self):
        _repository, _rustup, toolchain, paths = create_fake_toolchain(self.tmp_path)
        other = self.tmp_path / "other tool" / scbl_task.executable_name("rustfmt")
        other.parent.mkdir()
        other.write_bytes(b"mismatched tool")
        with self.assertRaisesRegex(scbl_task.ToolchainError, "same pinned bin directory"):
            scbl_task.validate_tool_paths(
                "nightly-2025-10-15", toolchain, (paths[0], paths[1], other)
            )

    def test_stable_version_probe_is_rejected_for_pinned_nightly(self):
        repository, rustup, _toolchain, _paths = create_fake_toolchain(self.tmp_path)

        def stable_probe(command, **_kwargs):
            return SimpleNamespace(
                returncode=0,
                stdout=f"{Path(command[0]).stem} 1.90.0 (stable)\n",
                stderr="",
            )

        with self.assertRaisesRegex(scbl_task.ToolchainError, "incompatible version"):
            scbl_task.resolve_rust_tools(
                repository, {"RUSTUP_HOME": str(rustup)}, runner=stable_probe
            )

    def test_multiple_matching_toolchain_directories_fail_closed(self):
        repository, rustup, _toolchain, _paths = create_fake_toolchain(self.tmp_path)
        duplicate = rustup / "toolchains/nightly-2025-10-15-i686-pc-windows-msvc/bin"
        duplicate.mkdir(parents=True)
        with self.assertRaisesRegex(scbl_task.ToolchainError, "exactly one installed directory"):
            scbl_task.resolve_rust_tools(
                repository, {"RUSTUP_HOME": str(rustup)}, runner=nightly_probe
            )

    def test_child_environment_is_temporary_and_pins_rustc(self):
        repository, rustup, toolchain, _paths = create_fake_toolchain(self.tmp_path)
        tools = scbl_task.resolve_rust_tools(
            repository, {"RUSTUP_HOME": str(rustup)}, runner=nightly_probe
        )
        original = {"PATH": "existing-path", "KEEP": "yes"}
        child = scbl_task.child_environment(tools, original)
        self.assertEqual(original, {"PATH": "existing-path", "KEEP": "yes"})
        self.assertEqual(child["PATH"].split(os.pathsep)[0], str((toolchain / "bin").resolve()))
        self.assertEqual(child["RUSTC"], str(tools.rustc.resolve()))
        self.assertEqual(child["RUSTUP_TOOLCHAIN"], "nightly-2025-10-15")

    def test_passthrough_preserves_argument_vector_and_exit_code(self):
        completed = SimpleNamespace(returncode=37)
        with mock.patch.object(scbl_task.subprocess, "run", return_value=completed) as runner:
            result = scbl_task.run_passthrough(
                ["tool path", "--message", "value with spaces", "--", "literal"],
                environment={"PATH": "test"},
            )
        self.assertEqual(result, 37)
        self.assertEqual(
            runner.call_args.args[0],
            ["tool path", "--message", "value with spaces", "--", "literal"],
        )
        self.assertEqual(runner.call_args.kwargs["env"], {"PATH": "test"})

    def test_hooks_dev_options_default_to_debug_install_and_launch(self):
        options = scbl_task.parse_hooks_dev_options([])
        self.assertFalse(options.release)
        self.assertTrue(options.launch)
        self.assertEqual(options.operation, "install")

    def test_hooks_dev_requires_an_absolute_test_launcher_directory(self):
        with self.assertRaisesRegex(ValueError, "must be absolute"):
            scbl_task.resolve_test_launcher_directory(Path("relative"), {})

    def test_configured_game_directory_is_loaded_from_portable_user_settings(self):
        launcher = self.tmp_path / "launcher"
        game = self.tmp_path / "game"
        game.mkdir()
        (game / "Blacklist_DX11_game.exe").write_bytes(b"game")
        settings = launcher / "data" / "TESTPC" / "TestUser" / "config" / "launcher_settings.json"
        settings.parent.mkdir(parents=True)
        settings.write_text(
            '{"GameDirectory": ' + json.dumps(str(game)) + '}',
            encoding="utf-8",
        )

        resolved = scbl_task.resolve_configured_game_directory(
            launcher,
            {"COMPUTERNAME": "TESTPC", "USERNAME": "TestUser"},
        )

        self.assertEqual(resolved, game.resolve())

    def test_local_hooks_override_is_validated_and_installed_atomically(self):
        launcher = self.tmp_path / "test launcher"
        launcher.mkdir()
        (launcher / "SCBL.exe").write_bytes(b"launcher")
        source = self.tmp_path / "hooks.dll"
        write_test_hooks_dll(source)

        target, digest = scbl_task.install_local_hooks_override(source, launcher)

        self.assertEqual(target, launcher / scbl_task.LOCAL_HOOKS_RELATIVE_PATH)
        self.assertEqual(target.read_bytes(), source.read_bytes())
        self.assertEqual(digest, scbl_task.sha256_file(source))
        self.assertFalse(target.with_name(target.name + ".scbl-new").exists())
        self.assertFalse(target.with_name(target.name + ".scbl-rollback").exists())

    def test_local_hooks_override_rejects_non_x86_dll(self):
        source = self.tmp_path / "hooks.dll"
        write_test_hooks_dll(source)
        content = bytearray(source.read_bytes())
        struct.pack_into("<H", content, 0x84, 0x8664)
        source.write_bytes(content)

        with self.assertRaisesRegex(ValueError, "32-bit x86"):
            scbl_task.validate_local_hooks_dll(source)

    def test_local_plugin_requires_both_abi_exports(self):
        source = self.tmp_path / "scbl_gameplay.dll"
        write_test_x86_dll(source, (scbl_task.PLUGIN_INITIALIZE_MARKER,))

        with self.assertRaisesRegex(ValueError, "scbl_plugin_shutdown"):
            scbl_task.validate_local_plugin_dll(source)

        write_test_plugin_dll(source)
        scbl_task.validate_local_plugin_dll(source)

    def test_disabling_local_development_set_retains_all_dlls(self):
        launcher = self.tmp_path / "test launcher"
        for relative_path in (
            scbl_task.LOCAL_HOOKS_RELATIVE_PATH,
            scbl_task.LOCAL_GAMEPLAY_RELATIVE_PATH,
        ):
            target = launcher / relative_path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(relative_path.name.encode("ascii"))

        disabled = scbl_task.disable_local_development_dlls(launcher)

        self.assertEqual(len(disabled), 2)
        self.assertTrue(all(path.name.endswith(".disabled") and path.is_file() for path in disabled))

    @unittest.skipUnless(os.name == "nt", "the bootstrap is a Windows cmd script")
    def test_cmd_rejects_windowsapps_alias(self):
        local_app_data = self.tmp_path / "Local App Data"
        windows_apps = local_app_data / "Microsoft/WindowsApps"
        windows_apps.mkdir(parents=True)
        (windows_apps / "python.exe").write_bytes(b"alias placeholder")
        environment = {
            key: value
            for key, value in os.environ.items()
            if key.casefold() not in {"path", "localappdata"}
        }
        system_root = os.environ.get("SystemRoot") or os.environ.get("WINDIR") or r"C:\Windows"
        environment["SystemRoot"] = system_root
        environment["LOCALAPPDATA"] = str(local_app_data)
        environment["USERPROFILE"] = str(self.tmp_path / "profile without runtime")
        environment["PATH"] = f"{windows_apps};{system_root}\\System32"
        completed = run_cmd(CMD_PATH, "help", environment=environment)
        self.assertEqual(completed.returncode, 2)
        self.assertIn("rejected WindowsApps Python alias", completed.stderr)
        self.assertIn("could not locate the current codex-primary-runtime Python", completed.stderr)

    @unittest.skipUnless(os.name == "nt", "the bootstrap is a Windows cmd script")
    def test_cmd_missing_python_fails_closed(self):
        environment = {
            key: value for key, value in os.environ.items() if key.casefold() != "path"
        }
        system_root = os.environ.get("SystemRoot") or os.environ.get("WINDIR") or r"C:\Windows"
        environment["SystemRoot"] = system_root
        environment["USERPROFILE"] = str(self.tmp_path / "profile without runtime")
        environment["PATH"] = f"{system_root}\\System32"
        completed = run_cmd(CMD_PATH, "help", environment=environment)
        self.assertEqual(completed.returncode, 2)
        self.assertIn("could not locate the current codex-primary-runtime Python", completed.stderr)

    @unittest.skipUnless(os.name == "nt", "the bootstrap is a Windows cmd script")
    def test_cmd_handles_script_directory_with_spaces(self):
        copied = self.tmp_path / "coordination tools with spaces"
        copied.mkdir()
        copied_cmd = copied / CMD_PATH.name
        shutil.copy2(CMD_PATH, copied_cmd)
        shutil.copy2(MODULE_PATH, copied / MODULE_PATH.name)
        completed = run_cmd(copied_cmd, "help")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("Usage: scbl-task.cmd", completed.stdout)

    def test_cmd_bootstrap_has_no_bundle_version_directory_literal(self):
        content = CMD_PATH.read_text(encoding="utf-8")
        self.assertIn("codex-primary-runtime", content)
        self.assertIn("WindowsApps", content)
        self.assertIsNone(
            re.search(r"codex-primary-runtime[/\\][^\r\n]*[/\\]\d+\.\d+", content)
        )

    @unittest.skipUnless(os.name == "nt", "the bootstrap is a Windows cmd script")
    def test_real_entry_reports_consistent_pinned_versions(self):
        completed = run_cmd(CMD_PATH, "versions")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        lines = [line for line in completed.stdout.splitlines() if line.strip()]
        self.assertEqual(len(lines), 3)
        self.assertTrue(lines[0].startswith("cargo ") and "nightly" in lines[0])
        self.assertTrue(lines[1].startswith("rustc ") and "nightly" in lines[1])
        self.assertTrue(lines[2].startswith("rustfmt ") and "nightly" in lines[2])

    @unittest.skipUnless(os.name == "nt", "the bootstrap is a Windows cmd script")
    def test_real_entry_preserves_rustc_failure_and_argument(self):
        completed = run_cmd(CMD_PATH, "rustc", "--scbl-invalid-probe")
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("scbl-invalid-probe", completed.stderr)


if __name__ == "__main__":
    unittest.main()
