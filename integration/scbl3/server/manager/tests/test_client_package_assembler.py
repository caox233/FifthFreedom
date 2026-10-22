from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from scblctl.client_components import COMPONENTS
from scblctl.client_package_assembler import (
    ClientPackageAssembler,
    ClientPackageAssemblyError,
)
from scblctl.client_publish import ClientPublisher


class ClientPackageAssemblerTests(unittest.TestCase):
    def seed_components(self, root: Path, *, omit: str | None = None) -> None:
        versions = {
            "launcher": "3.0.5",
            "hooks": "2.0.148",
            "route-guard": "2.0.5",
            "easytier": "2.6.4",
            "updater": "2.0.10",
            "client-files": "2.0.28",
        }
        payloads = {
            "launcher": b"launcher",
            "hooks": b"hooks",
            "updater": b"updater",
            "route-guard": self.make_zip(
                {
                    "scbl-process-router.exe": b"router",
                    "WinDivert.dll": b"windivert",
                    "WinDivert64.sys": b"driver",
                }
            ),
            "easytier": self.make_zip(
                {
                    "easytier-core.exe": b"core",
                    "easytier-cli.exe": b"cli",
                    "Packet.dll": b"packet",
                    "wintun.dll": b"wintun",
                }
            ),
            "client-files": self.make_client_files(),
        }
        manifest = {
            "schemaVersion": 2,
            "channel": "stable",
            "generatedAt": "2026-08-23T00:00:00Z",
            "components": {},
            "files": {},
        }
        for component, spec in COMPONENTS.items():
            if component == omit:
                continue
            payload = payloads[component]
            version = versions[component]
            path = root / "components" / "artifacts" / component / version / spec.filename
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
            entry = {
                "version": version,
                "sha256": hashlib.sha256(payload).hexdigest(),
                "size": len(payload),
                "url": f"/components/artifacts/{component}/{version}/{spec.filename}",
                "updateMode": spec.update_mode,
                "required": True,
            }
            manifest[spec.section][component] = entry
        manifest_path = root / "components" / "channels" / "stable" / "client_components_v2.json"
        manifest_path.parent.mkdir(parents=True, exist_ok=True)
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    @staticmethod
    def make_zip(files: dict[str, bytes]) -> bytes:
        import io

        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as bundle:
            for name, payload in files.items():
                bundle.writestr(name, payload)
        return output.getvalue()

    def make_client_files(self) -> bytes:
        files = {
            "components/hooks/mod/scbl_gameplay.dll": b"gameplay",
            "components/hooks/mod/scbl_gameplay.dll.sha256": b"gameplay-sidecar",
            "assets/readme.txt": b"asset",
        }
        manifest = {
            "schemaVersion": 1,
            "files": [
                {
                    "path": name,
                    "size": len(payload),
                    "sha256": hashlib.sha256(payload).hexdigest(),
                }
                for name, payload in files.items()
            ],
            "delete": ["obsolete.txt"],
        }
        all_files = {f"files/{name}": payload for name, payload in files.items()}
        all_files["client-files-manifest.json"] = json.dumps(manifest).encode()
        return self.make_zip(all_files)

    def test_builds_verified_full_package_from_current_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.seed_components(root)
            package = ClientPackageAssembler(root, channel="stable").build(root / "out")
            self.assertEqual("3.0.5", package.version)
            ClientPublisher(root, refresh_descriptor=False).verify(package.archive)
            with zipfile.ZipFile(package.archive) as bundle:
                names = set(bundle.namelist())
                self.assertIn("SCBL.exe", names)
                self.assertIn("components/route-guard/WinDivert64.payload.sys", names)
                self.assertIn("components/hooks/mod/scbl_gameplay.dll", names)
                manifest = json.loads(bundle.read("client_package_manifest.json"))
            self.assertEqual("3.0.5", manifest["clientVersion"])
            self.assertEqual("2.0.148", manifest["componentVersions"]["hooks"])
            self.assertEqual(
                hashlib.sha256(b"hooks").hexdigest(),
                manifest["bootstrapHooksSha256"],
            )
            repeated = ClientPackageAssembler(root, channel="stable").build(
                root / "out-repeated"
            )
            self.assertEqual(package.sha256, repeated.sha256)
            publisher = ClientPublisher(root, channel="stable")
            with patch.object(ClientPublisher, "require_root"):
                published = publisher.publish(package.archive)
            self.assertEqual(package.sha256, published.sha256)
            self.assertEqual(package.archive.read_bytes(), (root / "SCBL.zip").read_bytes())

    def test_rejects_incomplete_component_baseline(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.seed_components(root, omit="updater")
            with self.assertRaisesRegex(ClientPackageAssemblyError, "缺少"):
                ClientPackageAssembler(root, channel="stable").build(root / "out")


if __name__ == "__main__":
    unittest.main()
