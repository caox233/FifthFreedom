from __future__ import annotations

import importlib.util
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts" / "coordination" / "freeze_workspace.py"
SPEC = importlib.util.spec_from_file_location("freeze_workspace", MODULE_PATH)
assert SPEC and SPEC.loader
freeze_workspace = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(freeze_workspace)


def initialize_repository(root: Path) -> None:
    subprocess.run(["git", "init", "-q", str(root)], check=True)
    subprocess.run(["git", "-C", str(root), "config", "user.email", "test@example.invalid"], check=True)
    subprocess.run(["git", "-C", str(root), "config", "user.name", "SCBL Test"], check=True)
    (root / "one.txt").write_text("one", encoding="utf-8")
    (root / "two.txt").write_text("two", encoding="utf-8")
    subprocess.run(["git", "-C", str(root), "add", "one.txt", "two.txt"], check=True)


class FreezeWorkspaceTests(unittest.TestCase):
    def test_task_validation_outputs_are_never_source_inputs(self) -> None:
        self.assertTrue(
            freeze_workspace.is_excluded(
                "validation/TASK/workspace/client/project/obj/generated.file"
            )
        )
        self.assertFalse(
            freeze_workspace.is_excluded(
                "client/ScblPublicLauncher/Services/NetworkOrchestrator.cs"
            )
        )

    def test_exact_ignored_release_assets_are_still_frozen(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            initialize_repository(root)
            ignored = root / "client" / "ScblPublicLauncher" / "EmbeddedFiles" / "FusionFix.zip"
            ignored.parent.mkdir(parents=True)
            ignored.write_bytes(b"immutable-asset")
            (root / ".gitignore").write_text("*.zip\n", encoding="utf-8")

            paths = freeze_workspace.available_relative_paths(root)

            self.assertIn(
                "client/ScblPublicLauncher/EmbeddedFiles/FusionFix.zip",
                paths,
            )

    def test_launcher_profile_excludes_server_and_hooks_sources(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            files = {
                "VERSION_CLIENT": "1.0.0",
                "client/ScblPublicLauncher/App.xaml.cs": "launcher",
                "client/SCBL.Client.Tests/Test.cs": "test",
                "client/Shared/ClientDataLayout.cs": "shared client source",
                "client/hooks/src/lib.rs": "hooks",
                "server/dedicated-server/src/main.rs": "server",
            }
            for relative, content in files.items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            subprocess.run(["git", "-C", str(root), "add", "."], check=True)

            paths = freeze_workspace.available_relative_paths(root, "launcher")

            self.assertIn("VERSION_CLIENT", paths)
            self.assertIn("client/ScblPublicLauncher/App.xaml.cs", paths)
            self.assertIn("client/SCBL.Client.Tests/Test.cs", paths)
            self.assertIn("client/Shared/ClientDataLayout.cs", paths)
            self.assertNotIn("client/hooks/src/lib.rs", paths)
            self.assertNotIn("server/dedicated-server/src/main.rs", paths)

    def test_rust_profiles_include_the_complete_shared_api_crate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            files = {
                "Cargo.toml": "[workspace]\n",
                "client/hooks/Cargo.toml": "[package]\nname='hooks'\nversion='1.0.0'\n",
                "shared/api/Cargo.toml": "[package]\nname='api'\nversion='1.0.0'\n",
                "shared/api/build.rs": "fn main() {}\n",
                "shared/api/src/lib.rs": "pub fn ready() {}\n",
                "shared/api/proto/users.proto": "syntax = 'proto3';\n",
            }
            for relative, content in files.items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            subprocess.run(["git", "-C", str(root), "add", "."], check=True)

            paths = freeze_workspace.available_relative_paths(root, "hooks")

            self.assertIn("shared/api/Cargo.toml", paths)
            self.assertIn("shared/api/build.rs", paths)
            self.assertIn("shared/api/src/lib.rs", paths)
            self.assertIn("shared/api/proto/users.proto", paths)

    def test_hooks_profile_keeps_other_workspace_members_loadable(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            files = {
                "Cargo.toml": "[workspace]\nmembers=['client/hooks','server/dedicated-server']\n",
                "client/hooks/Cargo.toml": "[package]\nname='hooks'\nversion='1.0.0'\n",
                "client/hooks/src/lib.rs": "pub fn hooks() {}\n",
                "scripts/build-rust-components.ps1": "# fixed build entry\n",
                "server/dedicated-server/Cargo.toml": "[package]\nname='dedicated'\nversion='1.0.0'\n",
                "server/dedicated-server/src/lib.rs": "pub fn dedicated() {}\n",
            }
            for relative, content in files.items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            subprocess.run(["git", "-C", str(root), "add", "."], check=True)

            paths = freeze_workspace.available_relative_paths(root, "hooks")

            self.assertIn("client/hooks/src/lib.rs", paths)
            self.assertIn("scripts/build-rust-components.ps1", paths)
            self.assertIn("server/dedicated-server/Cargo.toml", paths)
            self.assertIn("server/dedicated-server/src/lib.rs", paths)

    def test_transient_copy_race_restarts_from_clean_staging_snapshot(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repository = root / "repository"
            repository.mkdir()
            initialize_repository(repository)
            destination = root / "release" / "candidate" / "freeze"
            attempts = 0

            def flaky_copy(source: Path, target: Path) -> object:
                nonlocal attempts
                attempts += 1
                if attempts == 1:
                    raise FileNotFoundError("injected CopyFile2 race")
                return shutil.copy2(source, target)

            summary = freeze_workspace.create_freeze(
                repository,
                destination,
                copy_file=flaky_copy,
                max_attempts=2,
                staging_root=root / "release",
            )

            self.assertEqual(2, summary["files"])
            self.assertEqual(
                "one",
                (destination / "freeze" / "one.txt").read_text(encoding="utf-8"),
            )
            self.assertEqual(
                "two",
                (destination / "freeze" / "two.txt").read_text(encoding="utf-8"),
            )
            self.assertEqual([], list((root / "release").glob(".sf-*")))

    def test_persistent_copy_race_leaves_no_formal_or_staging_freeze(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repository = root / "repository"
            repository.mkdir()
            initialize_repository(repository)
            destination = root / "release" / "candidate" / "freeze"

            with self.assertRaises(freeze_workspace.WorkspaceChangedError):
                freeze_workspace.create_freeze(
                    repository,
                    destination,
                    copy_file=lambda _source, _target: (_ for _ in ()).throw(
                        FileNotFoundError("persistent injected race")
                    ),
                    max_attempts=2,
                    staging_root=root / "release",
                )

            self.assertFalse(destination.exists())
            self.assertEqual([], list((root / "release").glob(".sf-*")))

    def test_non_transient_long_path_failure_is_not_retried(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repository = root / "repository"
            repository.mkdir()
            initialize_repository(repository)
            destination = root / "release" / "candidate" / "freeze"
            attempts = 0

            def long_path_failure(_source: Path, _target: Path) -> object:
                nonlocal attempts
                attempts += 1
                error = FileNotFoundError("injected Windows long path failure")
                error.winerror = 206
                raise error

            with self.assertRaises(FileNotFoundError):
                freeze_workspace.create_freeze(
                    repository,
                    destination,
                    copy_file=long_path_failure,
                    max_attempts=3,
                    staging_root=root / "release",
                )

            self.assertEqual(1, attempts)
            self.assertFalse(destination.exists())
            self.assertEqual([], list((root / "release").glob(".sf-*")))


if __name__ == "__main__":
    unittest.main()
