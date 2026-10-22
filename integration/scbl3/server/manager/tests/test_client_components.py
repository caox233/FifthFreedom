from __future__ import annotations

import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from scblctl.client_components import (
    COMPONENTS,
    ClientComponentError,
    ClientComponentPublisher,
    download_online_component,
)


class ClientComponentTests(unittest.TestCase):
    def test_component_catalog_loads_from_built_zipapp_in_arbitrary_cwd(self) -> None:
        manager_root = Path(__file__).resolve().parents[1]
        expected_components = sorted(COMPONENTS)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "build" / "scblctl.pyz"
            arbitrary_cwd = root / "unrelated-working-directory"
            arbitrary_cwd.mkdir()
            subprocess.run(
                [
                    sys.executable,
                    str(manager_root / "build.py"),
                    "--output",
                    str(archive),
                ],
                cwd=arbitrary_cwd,
                check=True,
                capture_output=True,
                text=True,
            )
            environment = os.environ.copy()
            environment["PYTHONPATH"] = str(archive)
            environment["PYTHONDONTWRITEBYTECODE"] = "1"
            result = subprocess.run(
                [
                    sys.executable,
                    "-c",
                    (
                        "import json; "
                        "from scblctl.client_components import "
                        "ClientComponentPublisher, COMPONENTS; "
                        "print(json.dumps(sorted(COMPONENTS)))"
                    ),
                ],
                cwd=arbitrary_cwd,
                env=environment,
                check=True,
                capture_output=True,
                text=True,
            )
            self.assertEqual(expected_components, json.loads(result.stdout))

    def test_component_catalog_is_the_exact_cross_tool_contract(self) -> None:
        self.assertEqual(
            {
                "launcher": ("SCBL.exe", "components", "updater-restart"),
                "hooks": ("uplay_r1_loader.dll", "components", "before-game-start"),
                "route-guard": ("route-guard.zip", "components", "next-launch"),
                "easytier": ("easytier-windows-x86_64.zip", "components", "next-launch"),
                "updater": ("SCBL.Updater.exe", "components", "next-launch"),
                "client-files": ("client-files.zip", "files", "immediate"),
            },
            {
                name: (
                    spec.filename,
                    spec.section,
                    spec.update_mode,
                )
                for name, spec in COMPONENTS.items()
            },
        )

    def test_update_owner_keeps_manifest_readable_by_scbl_services(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / "client_components_v2.json"
            target.write_text("{}", encoding="utf-8")
            account = type("Account", (), {"pw_uid": 1201})()
            group = type("Group", (), {"gr_gid": 1202})()
            with (
                patch("scblctl.client_components.pwd") as pwd_module,
                patch("scblctl.client_components.grp") as grp_module,
                patch("scblctl.client_components.os.chown", create=True) as chown,
            ):
                pwd_module.getpwnam.return_value = account
                grp_module.getgrnam.return_value = group
                ClientComponentPublisher._set_update_owner(target)
            chown.assert_called_once_with(target, 1201, 1202)

    def test_client_component_patch_verifies_only_declared_components(self) -> None:
        import hashlib

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "client.scblpatch"
            payload = b"launcher"
            digest = hashlib.sha256(payload).hexdigest()
            manifest = {
                "schemaVersion": 1,
                "packageType": "scbl-client-components-patch",
                "components": [
                    {
                        "component": "launcher",
                        "version": "2.0.7",
                        "path": "artifacts/launcher/SCBL.exe",
                        "file": "SCBL.exe",
                        "updateMode": "updater-restart",
                        "size": len(payload),
                        "sha256": digest,
                    }
                ],
            }
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("scbl-client-patch.json", json.dumps(manifest))
                bundle.writestr("artifacts/launcher/SCBL.exe", payload)
            verified = ClientComponentPublisher(root / "updates").verify_patch(archive)
            self.assertEqual(("launcher",), tuple(x.component for x in verified.artifacts))
            self.assertEqual("2.0.7", verified.artifacts[0].version)
    def test_component_patch_has_no_minimum_launcher_contract(self) -> None:
        import hashlib

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "missing-minimum.scblpatch"
            payload = b"launcher"
            manifest = {
                "schemaVersion": 1,
                "packageType": "scbl-client-components-patch",
                "components": [{
                    "component": "launcher",
                    "version": "2.0.7",
                    "path": "artifacts/launcher/SCBL.exe",
                    "file": "SCBL.exe",
                    "updateMode": "updater-restart",
                    "size": len(payload),
                    "sha256": hashlib.sha256(payload).hexdigest(),
                }],
            }
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("scbl-client-patch.json", json.dumps(manifest))
                bundle.writestr("artifacts/launcher/SCBL.exe", payload)
            verified = ClientComponentPublisher(root / "updates").verify_patch(archive)
            self.assertEqual("2.0.7", verified.artifacts[0].version)

    def test_patch_staging_uses_persistent_update_disk(self) -> None:
        import hashlib

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "SCBL-Client-Patch-v2.0.7.scblpatch"
            payload = b"launcher"
            digest = hashlib.sha256(payload).hexdigest()
            manifest = {
                "schemaVersion": 1,
                "packageType": "scbl-client-components-patch",
                "components": [
                    {
                        "component": "launcher",
                        "version": "2.0.7",
                        "path": "artifacts/launcher/SCBL.exe",
                        "file": "SCBL.exe",
                        "updateMode": "updater-restart",
                        "size": len(payload),
                        "sha256": digest,
                    }
                ],
            }
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("scbl-client-patch.json", json.dumps(manifest))
                bundle.writestr("artifacts/launcher/SCBL.exe", payload)
            publisher = ClientComponentPublisher(root / "data" / "client-updates")
            with (
                patch.object(ClientComponentPublisher, "_require_root"),
                patch(
                    "scblctl.client_components.tempfile.TemporaryDirectory",
                    wraps=tempfile.TemporaryDirectory,
                ) as temporary_directory,
            ):
                publisher.publish_patch(archive, channel="test")
            self.assertEqual(
                publisher.root.parent,
                temporary_directory.call_args.kwargs["dir"],
            )

    def test_stable_launcher_patch_does_not_rewrite_full_package_manifest(self) -> None:
        import hashlib

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            updates = root / "client-updates"
            updates.mkdir()
            full_manifest = updates / "client_update_manifest.json"
            original = {"version": "2.0.10", "fullPackage": "releases/2.0.10/full.zip"}
            full_manifest.write_text(json.dumps(original), encoding="utf-8")

            archive = root / "launcher.scblpatch"
            payload = b"launcher-2.0.11"
            digest = hashlib.sha256(payload).hexdigest()
            manifest = {
                "schemaVersion": 1,
                "packageType": "scbl-client-components-patch",
                "components": [{
                    "component": "launcher",
                    "version": "2.0.11",
                    "path": "artifacts/launcher/SCBL.exe",
                    "file": "SCBL.exe",
                    "updateMode": "updater-restart",
                    "size": len(payload),
                    "sha256": digest,
                }],
            }
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("scbl-client-patch.json", json.dumps(manifest))
                bundle.writestr("artifacts/launcher/SCBL.exe", payload)

            publisher = ClientComponentPublisher(updates)
            with patch.object(ClientComponentPublisher, "_require_root"):
                publisher.publish_patch(archive, channel="stable")

            self.assertEqual(original, json.loads(full_manifest.read_text(encoding="utf-8")))
            stable = json.loads(publisher.manifest_path("stable").read_text(encoding="utf-8"))
            self.assertEqual("2.0.11", stable["components"]["launcher"]["version"])

    def test_client_files_are_published_outside_legacy_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "client-files.zip"
            source.write_bytes(b"portable maintenance files")
            publisher = ClientComponentPublisher(root / "updates")
            with patch.object(ClientComponentPublisher, "_require_root"):
                entry = publisher.publish(
                    "client-files", "2.0.19", source, channel="stable"
                )

            manifest = json.loads(
                publisher.manifest_path("stable").read_text(encoding="utf-8")
            )
            self.assertNotIn("client-files", manifest["components"])
            self.assertEqual(entry, manifest["files"]["client-files"])
            self.assertNotIn("minLauncherVersion", entry)

    def test_component_manifest_rejects_unknown_legacy_fields(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, _ = self.publish(root, "2.0.1")
            path = publisher.manifest_path("test")
            manifest = json.loads(path.read_text(encoding="utf-8"))
            manifest["components"]["hooks"]["minLauncherVersion"] = "2.0.0"
            path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(ClientComponentError, "未知字段"):
                publisher.load_manifest("test")

    def publish(self, root: Path, version: str, content: bytes = b"hooks"):
        source = root / "uplay_r1_loader.dll"
        source.write_bytes(content)
        publisher = ClientComponentPublisher(root / "updates")
        with patch.object(ClientComponentPublisher, "_require_root"):
            return publisher, publisher.publish(
                "hooks", version, source, channel="test"
            )

    def test_server_modes_are_independent_component_channels(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, test_entry = self.publish(root, "2.0.1")
            stable_source = root / "uplay_r1_loader.dll"
            stable_source.write_bytes(b"stable hooks")
            with patch.object(ClientComponentPublisher, "_require_root"):
                stable_entry = publisher.publish(
                    "hooks", "3.0.0", stable_source, channel="stable"
                )
            test = json.loads(
                publisher.manifest_path("test").read_text(encoding="utf-8")
            )
            stable = json.loads(
                publisher.manifest_path("stable").read_text(encoding="utf-8")
            )
            self.assertEqual(test_entry, test["components"]["hooks"])
            self.assertEqual(stable_entry, stable["components"]["hooks"])
            self.assertNotEqual(
                test["components"]["hooks"]["sha256"],
                stable["components"]["hooks"]["sha256"],
            )

    def test_component_artifacts_and_manifest_backups_keep_three_generations(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher = ClientComponentPublisher(root / "updates")
            source = root / "uplay_r1_loader.dll"
            with patch.object(ClientComponentPublisher, "_require_root"):
                for revision in range(1, 6):
                    source.write_bytes(f"hooks-{revision}".encode())
                    publisher.publish(
                        "hooks",
                        f"2.0.{revision}",
                        source,
                        channel="test",
                    )
            versions = sorted(
                path.name
                for path in (publisher.artifacts_root / "hooks").iterdir()
                if path.is_dir()
            )
            backups = list(
                (publisher.backups_root / "test").glob(
                    "client_components_v2.*.json"
                )
            )
            self.assertEqual(["2.0.3", "2.0.4", "2.0.5"], versions)
            self.assertEqual(3, len(backups))

    def test_release_notes_are_preserved_without_signature_sidecar(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "uplay_r1_loader.dll"
            source.write_bytes(b"hooks with notes")
            publisher = ClientComponentPublisher(root / "updates")
            notes = {
                "zh": ["  修复大厅邀请  ", "提高稳定性"],
                "en": ["  Fix lobby invites  ", "Improve stability"],
            }
            with patch.object(ClientComponentPublisher, "_require_root"):
                entry = publisher.publish(
                    "hooks",
                    "2.0.2",
                    source,
                    channel="test",
                    release_notes=notes,
                )

            expected = {
                "zh": ["修复大厅邀请", "提高稳定性"],
                "en": ["Fix lobby invites", "Improve stability"],
            }
            self.assertEqual(expected, entry["releaseNotes"])
            self.assertEqual(
                expected,
                json.loads(
                    publisher.manifest_path("test").read_text(encoding="utf-8")
                )["components"]["hooks"]["releaseNotes"],
            )
            self.assertFalse(
                publisher.manifest_path("test").with_name(
                    publisher.manifest_path("test").name + ".sig"
                ).exists()
            )

    def test_release_note_boundaries_and_old_missing_field(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "uplay_r1_loader.dll"
            source.write_bytes(b"hooks")
            publisher = ClientComponentPublisher(root / "updates")
            exact = "界" * 240
            with patch.object(ClientComponentPublisher, "_require_root"):
                entry = publisher.publish(
                    "hooks",
                    "2.0.1",
                    source,
                    channel="test",
                    release_notes={"zh": [exact] * 6, "en": ["note"] * 6},
                )
            self.assertEqual(240, len(entry["releaseNotes"]["zh"][0]))

            invalid = dict(entry)
            invalid["version"] = "2.0.2"
            invalid["url"] = "/components/artifacts/hooks/2.0.2/uplay_r1_loader.dll"
            invalid["releaseNotes"] = {"zh": [exact + "界"], "en": ["note"]}
            with self.assertRaisesRegex(ClientComponentError, "过长"):
                publisher._validate_entry("hooks", invalid)

            invalid["releaseNotes"] = {
                "zh": ["说明"] * 7,
                "en": ["note"],
            }
            with self.assertRaisesRegex(ClientComponentError, "1～6"):
                publisher._validate_entry("hooks", invalid)

            legacy = dict(entry)
            legacy.pop("releaseNotes")
            publisher._validate_entry("hooks", legacy)

    def test_immutable_component_version_rejects_changed_content(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, _ = self.publish(root, "2.0.1")
            source = root / "uplay_r1_loader.dll"
            source.write_bytes(b"different")
            with patch.object(ClientComponentPublisher, "_require_root"):
                with self.assertRaisesRegex(ClientComponentError, "补丁版本必须高于当前版本"):
                    publisher.publish("hooks", "2.0.1", source, channel="test")

    def test_component_versions_use_one_numeric_contract(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "easytier-windows-x86_64.zip"
            source.write_bytes(b"easytier")
            publisher = ClientComponentPublisher(root / "updates", refresh_descriptor=False)
            with patch.object(ClientComponentPublisher, "_require_root"):
                entry = publisher.publish("easytier", "2.6.4", source, channel="test")
                with self.assertRaisesRegex(ClientComponentError, "X.Y.Z"):
                    publisher.publish(
                        "easytier",
                        "easytier-2026.08.04.12",
                        source,
                        channel="test",
                    )
            self.assertEqual("2.6.4", entry["version"])

    def test_patch_version_must_be_strictly_higher_than_channel(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, _ = self.publish(root, "2.0.1")
            source = root / "uplay_r1_loader.dll"
            source.write_bytes(b"different")
            with patch.object(ClientComponentPublisher, "_require_root"):
                with self.assertRaisesRegex(ClientComponentError, "补丁版本必须高于当前版本"):
                    publisher.publish("hooks", "2.0.1", source, channel="test")
                with self.assertRaisesRegex(ClientComponentError, "补丁版本必须高于当前版本"):
                    publisher.publish("hooks", "2.0.0", source, channel="test")

    def test_patch_prevalidates_every_component_before_publishing(self) -> None:
        import hashlib

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher = ClientComponentPublisher(root / "updates")
            launcher = root / "SCBL.exe"
            launcher.write_bytes(b"existing launcher")
            hooks = root / "uplay_r1_loader.dll"
            hooks.write_bytes(b"existing hooks")
            with patch.object(ClientComponentPublisher, "_require_root"):
                publisher.publish("launcher", "2.0.2", launcher, channel="test")
                publisher.publish("hooks", "2.0.2", hooks, channel="test")

            archive = root / "mixed.scblpatch"
            payloads = {
                "launcher": ("2.0.3", "SCBL.exe", "updater-restart", b"new launcher"),
                "hooks": ("2.0.2", "uplay_r1_loader.dll", "before-game-start", b"same-version hooks"),
            }
            components = []
            with zipfile.ZipFile(archive, "w") as bundle:
                for component, (version, filename, update_mode, payload) in payloads.items():
                    path = f"artifacts/{component}/{filename}"
                    bundle.writestr(path, payload)
                    components.append({
                        "component": component,
                        "version": version,
                        "path": path,
                        "file": filename,
                        "updateMode": update_mode,
                        "size": len(payload),
                        "sha256": hashlib.sha256(payload).hexdigest(),
                    })
                bundle.writestr("scbl-client-patch.json", json.dumps({
                    "schemaVersion": 1,
                    "packageType": "scbl-client-components-patch",
                    "components": components,
                }))

            with patch.object(ClientComponentPublisher, "_require_root"):
                with self.assertRaisesRegex(ClientComponentError, "补丁版本必须高于当前版本"):
                    publisher.publish_patch(archive, channel="test")
            manifest = publisher.load_manifest("test")
            self.assertEqual("2.0.2", manifest["components"]["launcher"]["version"])

    def test_artifact_version_directory_is_traversable_by_update_service(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            publisher, _ = self.publish(root, "2.0.1")
            directory = publisher.artifacts_root / "hooks" / "2.0.1"
            mode = stat.S_IMODE(directory.stat().st_mode)
            if os.name == "posix":
                self.assertEqual(0o755, mode)
            else:
                self.assertEqual(0o111, mode & 0o111)

    def test_online_download_fetches_only_metadata_and_selected_component(self) -> None:
        metadata = {
            "schemaVersion": 2,
            "component": "hooks",
            "version": "2.0.1",
            "file": "uplay_r1_loader.dll",
            "sha256": "",
            "size": 5,
            "updateMode": "before-game-start",
        }
        payload = b"hooks"
        import hashlib

        metadata["sha256"] = hashlib.sha256(payload).hexdigest()
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary)
            with patch(
                "scblctl.client_components._download_https",
                side_effect=[json.dumps(metadata).encode(), payload],
            ) as download:
                result = download_online_component(
                    "caox233/SCBL",
                    "hooks",
                    channel="stable",
                    destination=destination,
                )
            self.assertEqual("2.0.1", result.version)
            self.assertEqual(payload, result.source.read_bytes())
            self.assertEqual(2, download.call_count)
            self.assertIn(
                "/client-component-hooks-stable/component.json",
                download.call_args_list[0].args[0],
            )

    def test_online_download_rejects_metadata_for_another_component(self) -> None:
        metadata = {
            "schemaVersion": 2,
            "component": "updater",
            "version": "2.0.1",
            "file": "SCBL.Updater.exe",
            "sha256": "0" * 64,
            "size": 1,
            "updateMode": "next-launch",
        }
        with tempfile.TemporaryDirectory() as temporary:
            with patch(
                "scblctl.client_components._download_https",
                return_value=json.dumps(metadata).encode(),
            ):
                with self.assertRaisesRegex(ClientComponentError, "名称或文件名"):
                    download_online_component(
                        "caox233/SCBL",
                        "hooks",
                        channel="stable",
                        destination=Path(temporary),
                    )


if __name__ == "__main__":
    unittest.main()
