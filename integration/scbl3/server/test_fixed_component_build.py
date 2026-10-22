from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/build/build_component.py"
SPEC = importlib.util.spec_from_file_location("build_component", MODULE_PATH)
assert SPEC and SPEC.loader
build_component = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build_component)


class FixedComponentBuildTests(unittest.TestCase):
    def test_build_entry_has_no_legacy_signing_input(self) -> None:
        self.assertFalse(hasattr(build_component, "trusted_key_ids"))
        source = MODULE_PATH.read_text(encoding="utf-8")
        self.assertNotIn("Ed25519", source)
        self.assertNotIn("public key", source.lower())

    def test_launcher_command_uses_external_cache_and_single_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            command = build_component.build_command(
                "dotnet",
                root / "Launcher.csproj",
                root / "out",
                root / "cache",
                "2.2.45",
            )
        joined = " ".join(str(item) for item in command)
        self.assertIn("BaseIntermediateOutputPath", joined)
        self.assertIn("BaseOutputPath", joined)
        self.assertIn("PublishSingleFile=true", joined)
        self.assertIn("ScblVersion=2.2.45", joined)
        self.assertEqual(1, command.count("publish"))
        self.assertNotIn("Signing", joined)

    def test_projects_exclude_project_local_generated_sources(self) -> None:
        for relative in (
            "client/ScblPublicLauncher/SplinterCellCNLauncher.csproj",
            "client/SCBL.Updater/SCBL.Updater.csproj",
        ):
            source = (ROOT / relative).read_text(encoding="utf-8")
            self.assertIn('<Compile Remove="obj\\**\\*.cs" />', source)

    def test_component_cache_lock_is_single_writer_and_released(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            with build_component.component_lock(cache):
                with self.assertRaises(RuntimeError):
                    with build_component.component_lock(cache):
                        pass
            with build_component.component_lock(cache):
                self.assertTrue((cache / ".build.lock").is_file())

    def test_ci_runs_both_launcher_and_updater_test_projects(self) -> None:
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        solution = "client/ScblPublicLauncher/SplinterCellCNLauncher.sln"
        self.assertIn(f"dotnet restore {solution}", workflow)
        self.assertIn(f"dotnet test {solution}", workflow)
        self.assertNotIn(
            "dotnet test client/SCBL.Client.Tests/SCBL.Client.Tests.csproj",
            workflow,
        )
        self.assertIn("scripts/build-rust-components.ps1 -HooksOnly", workflow)
        self.assertIn(
            "cargo test --locked -p dedicated_server -p quazal -p sc_bl_protocols",
            workflow,
        )


if __name__ == "__main__":
    unittest.main()
