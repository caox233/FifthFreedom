"""Build deterministic native-Windows SCBL server packages."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tempfile
import zipfile
from pathlib import Path
from typing import Iterable


STAMP = (2026, 1, 1, 0, 0, 0)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--manager", type=Path)
    parser.add_argument("--host", type=Path)
    parser.add_argument("--updater", type=Path)
    parser.add_argument("--dedicated", type=Path)
    parser.add_argument("--python-embed", type=Path)
    parser.add_argument(
        "--components",
        nargs="+",
        choices=("manager", "host", "updater", "dedicated", "control-plane", "update-server", "assets", "python"),
        help="Omit for a full package; otherwise create an update ZIP with only these components.",
    )
    return parser.parse_args()


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def info(name: str) -> zipfile.ZipInfo:
    item = zipfile.ZipInfo(name, STAMP)
    item.compress_type = zipfile.ZIP_DEFLATED
    item.create_system = 0
    item.external_attr = 0
    return item


def write_bytes(bundle: zipfile.ZipFile, name: str, payload: bytes) -> None:
    bundle.writestr(info(name.replace("\\", "/")), payload)


def write_file(bundle: zipfile.ZipFile, name: str, source: Path) -> None:
    write_bytes(bundle, name, source.read_bytes())


def make_zip(path: Path, entries: Iterable[tuple[str, Path | bytes]]) -> Path:
    if path.exists():
        path.unlink()
    with zipfile.ZipFile(path, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for name, source in sorted(entries, key=lambda item: item[0]):
            write_bytes(bundle, name, source if isinstance(source, bytes) else source.read_bytes())
    return path


def prepare_python(source: Path, destination: Path) -> Path:
    with zipfile.ZipFile(source, "r") as input_bundle, zipfile.ZipFile(
        destination, "w", zipfile.ZIP_DEFLATED, compresslevel=6
    ) as output_bundle:
        found_path = False
        for item in sorted(input_bundle.infolist(), key=lambda value: value.filename):
            if item.is_dir():
                continue
            payload = input_bundle.read(item)
            if item.filename.lower().endswith("._pth"):
                payload = b"python312.zip\n.\n..\\control\n..\\update\n"
                found_path = True
            write_bytes(output_bundle, item.filename, payload)
        if not found_path:
            raise SystemExit("Python embeddable package lacks python312._pth")
    return destination


def component(
    identifier: str,
    version: str,
    archive_path: str,
    source: Path,
    *,
    target: str,
    destination: str,
    extract: bool = False,
) -> dict[str, object]:
    return {
        "id": identifier,
        "version": version,
        "path": archive_path,
        "size": source.stat().st_size,
        "sha256": digest(source),
        "target": target,
        "destination": destination,
        "extract": extract,
    }


def main() -> int:
    args = parse_args()
    root = args.root.resolve(strict=True)
    versions = json.loads((root / "COMPONENT_VERSIONS.json").read_text(encoding="utf-8"))
    release_version = str(versions["serverReleaseVersion"])
    server_versions = versions["serverComponents"]
    requested = set(args.components or ("manager", "host", "updater", "dedicated", "control-plane", "update-server", "assets", "python"))

    def require_path(name: str, value: Path | None) -> Path:
        if value is None:
            raise SystemExit(f"--{name} is required for the selected components")
        return value.resolve(strict=True)

    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="scbl-windows-package-", dir=output) as temporary_text:
        temporary = Path(temporary_text)
        sources: list[tuple[str, Path]] = []
        components: list[dict[str, object]] = []

        def add(identifier: str, version: str, archive_path: str, source: Path, *, target: str, destination: str, extract: bool = False) -> None:
            sources.append((archive_path, source))
            components.append(component(identifier, version, archive_path, source, target=target, destination=destination, extract=extract))

        if "manager" in requested:
            add("server.manager", str(server_versions["manager"]), "artifacts/bootstrap/SCBL.Server.exe", require_path("manager", args.manager), target="bootstrap", destination="SCBL.Server.exe")
        if "host" in requested:
            add("server.host", release_version, "artifacts/bootstrap/SCBL.Server.Host.exe", require_path("host", args.host), target="bootstrap", destination="SCBL.Server.Host.exe")
        if "updater" in requested:
            add("server.updater", release_version, "artifacts/bootstrap/SCBL.Server.Update.exe", require_path("updater", args.updater), target="bootstrap", destination="SCBL.Server.Update.exe")
        if "dedicated" in requested:
            add("server.dedicated", str(server_versions["dedicated"]), "artifacts/runtime/dedicated/SCBL.Dedicated.exe", require_path("dedicated", args.dedicated), target="runtime", destination="dedicated/SCBL.Dedicated.exe")
        if "control-plane" in requested:
            control_zip = make_zip(temporary / "control-plane.zip", [(name, root / "server" / name) for name in ("scbl_control_plane.py", "control_plane_auth.py", "control_plane_domain.py", "control_plane_gameplay.py", "control_plane_runtime.py")])
            add("server.control-plane", str(server_versions["controlPlane"]), "artifacts/runtime/control-plane.zip", control_zip, target="runtime", destination="control", extract=True)
        if "update-server" in requested:
            update_zip = make_zip(temporary / "update-server.zip", [("scbl_update_server.py", root / "server" / "scbl_update_server.py")])
            add("server.update-server", str(server_versions["updateServer"]), "artifacts/runtime/update-server.zip", update_zip, target="runtime", destination="update", extract=True)
        if "assets" in requested:
            assets_zip = make_zip(temporary / "assets.zip", [("mp_balancing.ini", root / "server" / "dedicated-server" / "assets" / "mp_balancing.ini")])
            add("server.assets", str(server_versions["assets"]), "artifacts/runtime/assets.zip", assets_zip, target="runtime", destination="assets", extract=True)
        if "python" in requested:
            python_zip = prepare_python(require_path("python-embed", args.python_embed), temporary / "python-embed.zip")
            add("runtime.python", "3.12.10", "artifacts/runtime/python-embed.zip", python_zip, target="runtime", destination="python", extract=True)

        is_full = args.components is None
        manifest = {
            "schemaVersion": 1,
            "packageType": "windows-full" if is_full else "windows-update",
            "releaseVersion": release_version,
            "components": sorted(components, key=lambda item: str(item["id"])),
        }
        manifest_bytes = (json.dumps(manifest, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        suffix = "full" if is_full else "-".join(sorted(requested)) + "-update"
        full_name = "SCBL-Windows-Server-full.zip" if is_full else f"SCBL-Windows-Server-v{release_version}-{suffix}.zip"
        full_path = output / full_name
        if full_path.exists():
            full_path.unlink()
        with zipfile.ZipFile(full_path, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            write_bytes(bundle, "scbl-windows-package.json", manifest_bytes)
            for name, source in sorted(sources):
                write_file(bundle, name, source)
        (output / f"{full_name}.sha256").write_text(f"{digest(full_path)}  {full_name}\n", encoding="utf-8")

        if not is_full:
            print(full_path)
            print(f"update_sha256={digest(full_path)}")
            return 0

        installer_name = f"SCBL-Server-Windows-v{release_version}.zip"
        installer = output / installer_name
        if installer.exists():
            installer.unlink()
        readme = (
            "SCBL 原生 Windows 服务端\r\n\r\n"
            "1. 解压全部文件。\r\n"
            "2. 双击 SCBL.Server.exe。\r\n"
            "3. 填写服务器身份、地址与端口。\r\n"
            "4. 点击首次安装。\r\n\r\n"
            "程序会注册 Windows 服务、添加精确防火墙规则并按需下载 EasyTier。\r\n"
        ).encode("utf-8-sig")
        with zipfile.ZipFile(installer, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            write_file(bundle, "SCBL.Server.exe", args.manager)
            write_file(bundle, f"payload/{full_name}", full_path)
            write_bytes(bundle, "安装说明.txt", readme)
        (output / f"{installer_name}.sha256").write_text(f"{digest(installer)}  {installer_name}\n", encoding="utf-8")

    print(full_path)
    print(installer)
    print(f"full_sha256={digest(full_path)}")
    print(f"installer_sha256={digest(installer)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
