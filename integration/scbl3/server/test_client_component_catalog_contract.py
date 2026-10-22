#!/usr/bin/env python3
"""Cross-tool contract test for SCBL client component metadata."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


def run_powershell(script: Path, *arguments: str, expected_success: bool = True) -> subprocess.CompletedProcess[str]:
    executable = (
        shutil.which("pwsh")
        or shutil.which("powershell.exe")
        or shutil.which("powershell")
    )
    if not executable:
        raise AssertionError("Windows PowerShell is required for the component contract test")
    result = subprocess.run(
        [
            executable,
            "-NoProfile",
            "-File",
            str(script),
            *arguments,
        ],
        cwd=REPOSITORY_ROOT,
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=60,
        check=False,
    )
    if (result.returncode == 0) != expected_success:
        raise AssertionError(
            f"unexpected exit code {result.returncode} for {script.name}:\n{result.stdout}"
        )
    return result


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = (
    REPOSITORY_ROOT
    / "server"
    / "manager"
    / "scblctl"
    / "client_component_catalog.json"
)


def main() -> None:
    if not (
        shutil.which("pwsh")
        or shutil.which("powershell.exe")
        or shutil.which("powershell")
    ):
        print("SKIP: Windows PowerShell is not available for the cross-tool contract test")
        return

    catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    assert catalog["schemaVersion"] == 1
    assert catalog["components"]["client-files"] == {
        "fileName": "client-files.zip",
        "section": "files",
        "updateMode": "immediate",
    }

    sys.path.insert(0, str(REPOSITORY_ROOT / "server" / "manager"))
    from scblctl.client_components import ClientComponentPublisher  # noqa: PLC0415

    assemble = REPOSITORY_ROOT / "scripts" / "release" / "assemble-client-components.ps1"
    build_patch = REPOSITORY_ROOT / "scripts" / "release" / "build-client-component-patch.ps1"
    with tempfile.TemporaryDirectory(prefix="scbl-component-contract-") as temporary:
        root = Path(temporary)
        publish_root = root / "publish"
        component_root = root / "components"
        publish_root.mkdir()
        gameplay = publish_root / "components" / "hooks" / "mod" / "scbl_gameplay.dll"
        gameplay.parent.mkdir(parents=True)
        gameplay.write_bytes(b"gameplay")
        gameplay.with_name(gameplay.name + ".sha256").write_text(
            hashlib.sha256(b"gameplay").hexdigest() + "  scbl_gameplay.dll\n",
            encoding="ascii",
        )

        run_powershell(
            assemble,
            "-PublishRoot",
            str(publish_root),
            "-OutputRoot",
            str(component_root),
            "-Components",
            "client-files",
        )
        metadata = json.loads(
            (component_root / "client-files" / "component.json").read_text(
                encoding="utf-8-sig"
            )
        )
        spec = catalog["components"]["client-files"]
        assert metadata["file"] == spec["fileName"]
        assert metadata["updateMode"] == spec["updateMode"]
        assert "minLauncherVersion" not in metadata

        with zipfile.ZipFile(component_root / "client-files" / spec["fileName"]) as client_files:
            file_manifest = json.loads(
                client_files.read("client-files-manifest.json").decode("utf-8-sig")
            )
        assert {
            item["path"] for item in file_manifest["files"]
        }.issuperset({
            "components/hooks/mod/scbl_gameplay.dll",
            "components/hooks/mod/scbl_gameplay.dll.sha256",
        })
        assert file_manifest["delete"], "delete-only client file bundles must remain publishable"

        archive = root / "client-files.scblpatch"
        run_powershell(
            build_patch,
            "-ComponentRoot",
            str(component_root),
            "-Output",
            str(archive),
            "-Components",
            "client-files",
        )
        with zipfile.ZipFile(archive) as bundle:
            patch_manifest = json.loads(
                bundle.read("scbl-client-patch.json").decode("utf-8-sig")
            )
        patch_entry = patch_manifest["components"][0]
        assert "minLauncherVersion" not in patch_entry

        verified = ClientComponentPublisher(root / "updates").verify_patch(archive)
        assert verified.artifacts[0].component == "client-files"

        rejected = run_powershell(
            build_patch,
            "-ComponentRoot",
            str(component_root),
            "-Output",
            str(root / "unknown.scblpatch"),
            "-Components",
            "unknown",
            expected_success=False,
        )
        assert "Unknown client component" in rejected.stdout

    assert not Path(temporary).exists()
    print("client component catalog cross-tool contract passed")


if __name__ == "__main__":
    os.environ.setdefault("PYTHONDONTWRITEBYTECODE", "1")
    main()
