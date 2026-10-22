from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "server" / "packaging" / "package_builder.py"
SPEC = importlib.util.spec_from_file_location("package_builder", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ServerPackageBuilderTests(unittest.TestCase):
    def test_full_package_is_standard_integrity_checked_zip_with_separate_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            files: dict[str, Path] = {}
            for _, name, _ in MODULE.COMPONENTS:
                path = root / name
                path.write_bytes(("payload:" + name).encode())
                files[name] = path
            migrations = root / "migrations"
            migrations.mkdir()
            (migrations / "20260808120000_fixture.sql").write_text(
                "CREATE TABLE fixture(id INTEGER);", encoding="utf-8"
            )
            runtime_contract = {
                "programArguments": {
                    "dedicated_server": ["--config"],
                    "scbl_update_server.py": ["--root", "--port", "--ipv6", "--bundle-cache"],
                },
                "databaseMigrations": {"20260808120000": "0" * 96},
            }
            output = root / "SCBL-Server-v3.0.0-full.zip"
            component_inputs = {
                name: MODULE.ComponentInput(path, "3.0.0")
                for name, path in files.items()
            }
            with patch.object(
                MODULE, "build_runtime_contract", return_value=runtime_contract
            ):
                package, checksum = MODULE.build_package(
                    kind="full",
                    version="3.0.0",
                    output=output,
                    component_inputs=component_inputs,
                    migration_dir=migrations,
                )
            self.assertEqual(output, package)
            self.assertTrue(checksum.is_file())

            with zipfile.ZipFile(output, "r") as bundle:
                names = set(bundle.namelist())
                manifest = json.loads(bundle.read("scbl-package.json"))
            self.assertEqual("full", manifest["packageType"])
            self.assertEqual(5, len(manifest["components"]))
            self.assertEqual(runtime_contract, manifest["runtimeContract"])
            self.assertEqual({"algorithm": "SHA256"}, manifest["integrityPolicy"])
            self.assertNotIn("scbl-package.sig", names)
            self.assertIn("artifacts/server.dedicated/dedicated_server", names)
            self.assertNotIn("artifacts/server-runtime.tar.gz", names)

    def test_update_package_accepts_one_component(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = root / "scblctl.pyz"
            manager.write_bytes(b"manager")
            output = root / "SCBL-Server-v3.0.1-update.zip"
            MODULE.build_package(
                kind="update",
                version="3.0.1",
                output=output,
                component_inputs={
                    "manager": MODULE.ComponentInput(manager, "2.0.25")
                },
            )
            with zipfile.ZipFile(output, "r") as bundle:
                manifest = json.loads(bundle.read("scbl-package.json"))
            self.assertEqual(["server.manager"], [x["id"] for x in manifest["components"]])
            self.assertIsNone(manifest["runtimeContract"])

    def test_update_package_accepts_all_runtime_components_in_one_transaction(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            files: dict[str, Path] = {}
            for component, name, _ in MODULE.COMPONENTS:
                if component == "server.manager":
                    continue
                path = root / name
                path.write_bytes(("payload:" + name).encode())
                files[name] = path
            runtime_manifest = root / "server-package.json"
            runtime_manifest.write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "programArguments": {},
                        "databaseMigrations": {},
                    }
                ),
                encoding="utf-8",
            )
            migrations = root / "migrations"
            migrations.mkdir()
            (migrations / "20260808120000_fixture.sql").write_text(
                "CREATE TABLE fixture(id INTEGER);", encoding="utf-8"
            )
            contract = {
                "programArguments": {
                    "dedicated_server": ["--config"],
                    "scbl_update_server.py": ["--root", "--port", "--ipv6", "--bundle-cache"],
                },
                "databaseMigrations": {"20260808120000": "0" * 96},
            }
            output = root / "SCBL-Server-v3.0.1-update.zip"
            with patch.object(MODULE, "build_runtime_contract", return_value=contract):
                MODULE.build_package(
                    kind="update",
                    version="3.0.1",
                    output=output,
                    component_inputs={
                        name: MODULE.ComponentInput(path, "3.0.1")
                        for name, path in files.items()
                    },
                    runtime_manifest=runtime_manifest,
                    migration_dir=migrations,
                )
            with zipfile.ZipFile(output, "r") as bundle:
                manifest = json.loads(bundle.read("scbl-package.json"))
            self.assertEqual("update", manifest["packageType"])
            self.assertEqual(4, len(manifest["components"]))
            self.assertEqual(contract, manifest["runtimeContract"])

    def test_update_package_rejects_manager_mixed_with_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = root / "manager"
            dedicated = root / "dedicated"
            manager.write_bytes(b"manager")
            dedicated.write_bytes(b"dedicated")
            with self.assertRaisesRegex(ValueError, "Manager must use its own"):
                MODULE.build_package(
                    kind="update",
                    version="3.0.1",
                    output=root / "mixed-update.zip",
                    component_inputs={
                        "manager": MODULE.ComponentInput(manager, "2.0.25"),
                        "dedicated": MODULE.ComponentInput(dedicated, "2.0.21"),
                    },
                    runtime_manifest=root / "unused.json",
                )

    def test_zip_sidecar_binds_the_complete_package_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = root / "scblctl.pyz"
            manager.write_bytes(b"manager")
            output = root / "server-manager-update.zip"
            MODULE.build_package(
                kind="update",
                version="3.0.1",
                output=output,
                component_inputs={
                    "manager": MODULE.ComponentInput(manager, "2.0.25")
                },
            )
            sidecar = output.with_name(output.name + ".sha256").read_text(encoding="utf-8")
            self.assertEqual(f"{MODULE.sha256(output)}  {output.name}\n", sidecar)


if __name__ == "__main__":
    unittest.main()
