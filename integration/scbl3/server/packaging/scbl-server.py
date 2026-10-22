#!/usr/bin/env python3
"""Single human-facing build entry for SCBL 3.0 server ZIPs."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
import zipapp
from pathlib import Path

from package_builder import ComponentInput, build_package


ROOT = Path(__file__).resolve().parents[2]
MIGRATIONS = ROOT / "server/dedicated-server/src/storage/migrations"
COMPONENTS = {
    "manager": ("manager", "manager"),
    "dedicated": ("dedicated", "dedicated"),
    "control-plane": ("control-plane", "controlPlane"),
    "update-server": ("update-server", "updateServer"),
    "assets": ("assets", "assets"),
}
RUNTIME_COMPONENTS = set(COMPONENTS) - {"manager"}
ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(
        description="Build one integrity-checked SCBL server full ZIP or component update ZIP"
    )
    subcommands = result.add_subparsers(dest="operation", required=True)
    full = subcommands.add_parser("full", help="compile required sources and build first-install ZIP")
    add_common(full)
    full.add_argument(
        "--client-package",
        type=Path,
        help="optional verified SCBL client full ZIP bundled for first install",
    )
    full.add_argument(
        "--client-patch",
        type=Path,
        help="optional matching all-component .scblpatch bundled for first install",
    )
    update = subcommands.add_parser(
        "update",
        help="compile all four Runtime components into one existing-server update ZIP",
    )
    add_common(update)
    update.add_argument(
        "--runtime-manifest",
        type=Path,
        default=Path("/opt/scbl/current/server-package.json"),
        help="current /opt/scbl/current/server-package.json",
    )
    component = subcommands.add_parser(
        "component",
        help="build one or more changed components into one update ZIP",
    )
    component.add_argument("components", nargs="+", choices=tuple(COMPONENTS))
    add_common(component)
    component.add_argument(
        "--runtime-manifest",
        type=Path,
        help="current /opt/scbl/current/server-package.json; required for runtime components",
    )
    return result


def add_common(command: argparse.ArgumentParser) -> None:
    command.add_argument("--release-version")
    command.add_argument(
        "--output-dir",
        type=Path,
        default=ROOT / ".work/server-packages",
    )
    command.add_argument(
        "--cargo-target-dir",
        type=Path,
        default=os.environ.get("CARGO_TARGET_DIR", ROOT / ".work/cargo-linux"),
    )


def run(command: list[str], *, environment: dict[str, str] | None = None) -> None:
    completed = subprocess.run(command, cwd=ROOT, env=environment, check=False)
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


def resolve_build_tool(name: str) -> str | None:
    resolved = shutil.which(name)
    if resolved is not None:
        return resolved
    if name == "cargo":
        rustup_cargo = Path.home() / ".cargo/bin/cargo"
        if rustup_cargo.is_file() and os.access(rustup_cargo, os.X_OK):
            return str(rustup_cargo)
    return None


def require_dedicated_build_tools() -> dict[str, str]:
    resolved = {name: resolve_build_tool(name) for name in ("cargo", "protoc")}
    missing = [name for name, path in resolved.items() if path is None]
    if missing:
        packages = "rustup/cargo" if missing == ["cargo"] else "protobuf-compiler"
        if len(missing) > 1:
            packages = "rustup/cargo and protobuf-compiler"
        raise SystemExit(
            "Missing Linux server build tool(s): "
            + ", ".join(missing)
            + f". Install {packages} before starting the build."
        )
    return {name: path for name, path in resolved.items() if path is not None}


def build_control_plane_archive(output: Path) -> None:
    source_root = ROOT / "server"
    staging = output.parent / "control-plane-app"
    shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True)
    shutil.copy2(source_root / "scbl_control_plane.py", staging / "__main__.py")
    shutil.copy2(source_root / "control_plane_domain.py", staging / "control_plane_domain.py")
    shutil.copy2(source_root / "control_plane_auth.py", staging / "control_plane_auth.py")
    shutil.copy2(source_root / "control_plane_gameplay.py", staging / "control_plane_gameplay.py")
    shutil.copy2(source_root / "control_plane_runtime.py", staging / "control_plane_runtime.py")
    output.unlink(missing_ok=True)
    zipapp.create_archive(
        staging,
        target=output,
        interpreter="/usr/bin/env python3",
        compressed=True,
    )


def build_installer_bundle(
    full_package: Path,
    output_dir: Path,
    release_version: str,
    *,
    source_root: Path = ROOT,
    client_package: Path | None = None,
    client_patch: Path | None = None,
) -> tuple[Path, Path]:
    """Bundle the verified full package with the single interactive installer."""

    package_checksum = full_package.with_name(full_package.name + ".sha256")
    setup = source_root / "server/bootstrap/setup.sh"
    installer = source_root / "server/bootstrap/install.sh"
    if (client_package is None) != (client_patch is None):
        raise SystemExit("Client package and component patch must be supplied together")
    optional = () if client_package is None else (client_package, client_patch)
    required = (full_package, package_checksum, setup, installer, *optional)
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise SystemExit("Installer bundle input is missing: " + ", ".join(missing))

    output = output_dir / f"SCBL-Server-v{release_version}-installer.zip"
    output.unlink(missing_ok=True)
    entries = [
        ("SCBL-Installer.sh", setup, 0o755),
        ("install.sh", installer, 0o755),
        (full_package.name, full_package, 0o644),
        (package_checksum.name, package_checksum, 0o644),
    ]
    if client_package is not None and client_patch is not None:
        entries.extend(
            (
                (client_package.name, client_package, 0o644),
                (client_patch.name, client_patch, 0o644),
            )
        )
    with zipfile.ZipFile(
        output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
    ) as archive:
        for name, source, mode in entries:
            info = zipfile.ZipInfo(name, ZIP_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (mode & 0xFFFF) << 16
            archive.writestr(info, source.read_bytes())

    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    sidecar = output.with_name(output.name + ".sha256")
    sidecar.write_text(f"{digest}  {output.name}\n", encoding="ascii")
    return output, sidecar


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    if os.name != "posix":
        raise SystemExit(
            "SCBL server packages must be built on the registered Linux build machine"
        )
    versions = json.loads((ROOT / "COMPONENT_VERSIONS.json").read_text(encoding="utf-8"))
    component_versions = versions["serverComponents"]
    release_version = args.release_version or versions["serverReleaseVersion"]
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    build_root = output_dir / f"inputs-{release_version}"
    build_root.mkdir(parents=True, exist_ok=True)
    if args.operation == "full":
        selected = set(COMPONENTS)
    elif args.operation == "update":
        selected = set(RUNTIME_COMPONENTS)
    else:
        if len(set(args.components)) != len(args.components):
            raise SystemExit("component names must be unique")
        selected = set(args.components)
        if "manager" in selected and len(selected) != 1:
            raise SystemExit("Manager must use its own update ZIP")
    dedicated_tools: dict[str, str] = {}
    if "dedicated" in selected:
        dedicated_tools = require_dedicated_build_tools()
    inputs: dict[str, Path] = {}

    if "manager" in selected:
        manager = build_root / "scblctl.pyz"
        manager.unlink(missing_ok=True)
        manager.with_name(manager.name + ".sha256").unlink(missing_ok=True)
        run([sys.executable, str(ROOT / "server/manager/build.py"), "--output", str(manager)])
        inputs["manager"] = manager

    if "dedicated" in selected:
        cargo_target = args.cargo_target_dir.resolve()
        environment = dict(os.environ)
        environment["CARGO_TARGET_DIR"] = str(cargo_target)
        run(
            [
                dedicated_tools["cargo"],
                "build",
                "--locked",
                "--release",
                "-p",
                "dedicated_server",
            ],
            environment=environment,
        )
        dedicated = cargo_target / "release/dedicated_server"
        if not dedicated.is_file():
            raise SystemExit("Dedicated release artifact is missing")
        inputs["dedicated"] = dedicated

    fixed = {
        "update-server": ROOT / "server/scbl_update_server.py",
        "assets": ROOT / "server/dedicated-server/assets/mp_balancing.ini",
    }
    for component, source in fixed.items():
        if component in selected:
            inputs[component] = source

    if "control-plane" in selected:
        control_plane = build_root / "scbl_control_plane.pyz"
        build_control_plane_archive(control_plane)
        inputs["control-plane"] = control_plane

    if args.operation == "full":
        suffix = "full"
    elif args.operation == "update":
        suffix = "update"
    else:
        suffix = f"{'-'.join(sorted(selected))}-update"
    output = output_dir / f"SCBL-Server-v{release_version}-{suffix}.zip"
    runtime_manifest = None
    if args.operation == "update":
        runtime_manifest = args.runtime_manifest
    elif args.operation == "component" and selected != {"manager"}:
        runtime_manifest = args.runtime_manifest or Path(
            "/opt/scbl/current/server-package.json"
        )
    component_inputs: dict[str, ComponentInput] = {}
    for component in sorted(selected):
        option, version_key = COMPONENTS[component]
        component_inputs[option.replace("-", "_")] = ComponentInput(
            inputs[component], component_versions[version_key]
        )
    _package, checksum = build_package(
        kind="full" if args.operation == "full" else "update",
        version=release_version,
        output=output,
        component_inputs=component_inputs,
        runtime_manifest=runtime_manifest,
        migration_dir=(
            MIGRATIONS
            if args.operation == "full" or "dedicated" in selected
            else None
        ),
    )
    print(f"READY: {output}")
    print(f"SHA256: {checksum}")
    if args.operation == "full":
        installer_bundle, installer_checksum = build_installer_bundle(
            output,
            output_dir,
            release_version,
            client_package=args.client_package,
            client_patch=args.client_patch,
        )
        print(f"INSTALLER: {installer_bundle}")
        print(f"INSTALLER SHA256: {installer_checksum}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
