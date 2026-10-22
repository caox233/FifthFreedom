#!/usr/bin/env python3
"""Build a synthetic full client package and verify the public SCBL 3.0 layout."""

from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    powershell = shutil.which("pwsh") or shutil.which("powershell.exe") or shutil.which("powershell")
    if not powershell:
        print("SKIP: PowerShell is unavailable for the full-package layout contract")
        return

    version = (ROOT / "VERSION_CLIENT").read_text(encoding="utf-8").strip()
    with tempfile.TemporaryDirectory(prefix="scbl-full-layout-") as temporary:
        work = Path(temporary)
        publish = work / "publish"
        output = work / "output"
        payloads = {
            "SCBL.exe": b"launcher",
            "SCBL.Updater.exe": b"updater",
            "components/hooks/uplay_r1_loader.dll": b"hooks",
            "components/hooks/mod/scbl_gameplay.dll": b"gameplay",
            "components/network/easytier-core.exe": b"core",
            "components/network/easytier-cli.exe": b"cli",
            "components/network/Packet.dll": b"packet",
            "components/network/wintun.dll": b"wintun",
            "components/route-guard/scbl-process-router.exe": b"router",
            "components/route-guard/WinDivert.dll": b"windivert-dll",
            "components/route-guard/WinDivert64.payload.sys": b"windivert-sys",
        }
        for relative, content in payloads.items():
            target = publish / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
        for source in (ROOT / "client/distribution").rglob("*"):
            if not source.is_file() or source.name == "client-files.rules.json":
                continue
            target = publish / source.relative_to(ROOT / "client/distribution")
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        generated = publish / "data/TESTPC/TestUser/logs/private.log"
        generated.parent.mkdir(parents=True, exist_ok=True)
        generated.write_text("private", encoding="utf-8")
        hook = publish / "components/hooks/uplay_r1_loader.dll"
        hook_hash = hashlib.sha256(hook.read_bytes()).hexdigest()
        (hook.parent / "uplay_r1_loader.dll.sha256").write_text(
            f"{hook_hash}  uplay_r1_loader.dll\n", encoding="ascii"
        )
        gameplay = publish / "components/hooks/mod/scbl_gameplay.dll"
        gameplay_hash = hashlib.sha256(gameplay.read_bytes()).hexdigest()
        (gameplay.parent / "scbl_gameplay.dll.sha256").write_text(
            f"{gameplay_hash}  scbl_gameplay.dll\n", encoding="ascii"
        )

        result = subprocess.run(
            [
                powershell,
                "-NoProfile",
                "-File",
                str(ROOT / "client/create_client_full_package.ps1"),
                "-Version",
                version,
                "-PublishRoot",
                str(publish),
                "-OutputDir",
                str(output),
                "-Fast",
            ],
            cwd=ROOT,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=60,
            check=False,
        )
        if result.returncode != 0:
            raise AssertionError(result.stdout)

        archive = output / f"SCBL-Client-v{version}-win-x86.zip"
        with zipfile.ZipFile(archive) as bundle:
            names = {entry.filename for entry in bundle.infolist() if not entry.is_dir()}
            assert not any(name.startswith("tools/") for name in names)
            assert not any(name.startswith("data/") for name in names)
            assert "SCBL.exe" in names
            assert "SCBL.Updater.exe" in names
            assert "components/hooks/uplay_r1_loader.dll" in names
            assert "components/hooks/mod/scbl_gameplay.dll" in names
            assert "components/network/easytier-core.exe" in names
            assert "components/route-guard/WinDivert64.payload.sys" in names
            assert "components/route-guard/WinDivert64.sys" not in names
            manifest = json.loads(bundle.read("client_package_manifest.json"))
            assert {item["path"] for item in manifest["files"]} == names - {
                "client_package_manifest.json"
            }

    print("SCBL full-package component layout contract passed")


if __name__ == "__main__":
    main()
