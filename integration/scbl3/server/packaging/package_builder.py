"""Deterministic, non-interactive server package assembly library."""

from __future__ import annotations

import hashlib
import json
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path
from typing import Any, NamedTuple


COMPONENTS = (
    ("server.manager", "manager", "artifacts/server.manager/scblctl.pyz"),
    ("server.dedicated", "dedicated", "artifacts/server.dedicated/dedicated_server"),
    (
        "server.control-plane",
        "control_plane",
        "artifacts/server.control-plane/scbl_control_plane.pyz",
    ),
    (
        "server.update-server",
        "update_server",
        "artifacts/server.update-server/scbl_update_server.py",
    ),
    ("server.assets", "assets", "artifacts/server.assets/data/mp_balancing.ini"),
)
RUNTIME_COMPONENTS = {component for component, _, _ in COMPONENTS} - {"server.manager"}
VERSION = re.compile(r"\d+\.\d+\.\d+")
PROGRAM_ARGUMENT = re.compile(r"--[a-z0-9][a-z0-9-]*")
PROGRAM_COMPONENTS = {
    "server.dedicated": ("dedicated_server", {"--config"}),
    "server.update-server": (
        "scbl_update_server.py",
        {"--root", "--port", "--ipv6", "--bundle-cache"},
    ),
}
MIGRATION_NAME = re.compile(r"(?P<version>\d+)_[A-Za-z0-9_-]+\.sql")


class ComponentInput(NamedTuple):
    path: Path
    version: str


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def build_package(
    *,
    kind: str,
    version: str,
    output: Path,
    component_inputs: dict[str, ComponentInput],
    runtime_manifest: Path | None = None,
    migration_dir: Path | None = None,
) -> tuple[Path, Path]:
    if kind not in {"full", "update"}:
        raise ValueError("kind must be full or update")
    if not VERSION.fullmatch(version):
        raise ValueError("version must be X.Y.Z")
    if output.suffix.lower() != ".zip":
        raise ValueError("output must end with .zip")
    if output.exists():
        raise FileExistsError(f"output already exists: {output}")

    components: list[dict[str, Any]] = []
    sources: dict[str, Path] = {}
    for component, name, archive_path in COMPONENTS:
        item = component_inputs.get(name)
        if item is None:
            continue
        source = item.path.resolve(strict=True)
        if not source.is_file() or source.is_symlink():
            raise ValueError(f"component is not a regular file: {source}")
        component_version = item.version
        if not VERSION.fullmatch(component_version):
            raise ValueError(f"invalid version for {component}: {component_version}")
        components.append(
            {
                "id": component,
                "version": component_version,
                "path": archive_path,
                "size": source.stat().st_size,
                "sha256": sha256(source),
            }
        )
        sources[component] = source

    present = {item["id"] for item in components}
    all_components = {item[0] for item in COMPONENTS}
    unknown = sorted(set(component_inputs) - {name for _, name, _ in COMPONENTS})
    if unknown:
        raise ValueError(f"unknown component inputs: {', '.join(unknown)}")
    if kind == "full" and present != all_components:
        raise ValueError("full package requires all five SCBL-owned server components")
    if kind == "update" and "server.manager" in present and len(present) != 1:
        raise ValueError("Manager must use its own update package")
    if not components:
        raise ValueError("package contains no components")

    contract = None
    if present & RUNTIME_COMPONENTS:
        base_contract = (
            read_runtime_contract(runtime_manifest.resolve(strict=True))
            if runtime_manifest is not None
            else None
        )
        if kind == "full":
            if migration_dir is None:
                raise ValueError("full package requires migration_dir")
            if runtime_manifest is not None:
                raise ValueError("full package derives its contract; omit runtime_manifest")
            contract = build_runtime_contract(
                sources,
                base=None,
                migration_dir=migration_dir.resolve(strict=True),
            )
        else:
            if base_contract is None:
                raise ValueError("runtime component update requires current runtime_manifest")
            resolved_migrations = (
                migration_dir.resolve(strict=True)
                if migration_dir is not None
                else None
            )
            if "server.dedicated" in present and resolved_migrations is None:
                raise ValueError("Dedicated update requires migration_dir")
            contract = build_runtime_contract(
                sources,
                base=base_contract,
                migration_dir=resolved_migrations,
            )

    manifest = {
        "schemaVersion": 3,
        "packageType": kind,
        "releaseVersion": version,
        "integrityPolicy": {"algorithm": "SHA256"},
        "components": sorted(components, key=lambda item: item["id"]),
        "runtimeContract": contract,
    }
    manifest_bytes = (
        json.dumps(
            manifest,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
        + "\n"
    ).encode("utf-8")
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(
        output,
        "x",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=6,
        strict_timestamps=True,
    ) as bundle:
        write_bytes(bundle, "scbl-package.json", manifest_bytes, executable=False)
        for item in sorted(components, key=lambda value: value["path"]):
            write_file(
                bundle,
                item["path"],
                sources[item["id"]],
                executable=item["id"]
                in {
                    "server.manager",
                    "server.dedicated",
                },
            )

    checksum = output.with_name(output.name + ".sha256")
    checksum.write_text(f"{sha256(output)}  {output.name}\n", encoding="utf-8")
    return output, checksum


def read_runtime_contract(path: Path) -> dict[str, Any]:
    try:
        raw: Any = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"invalid runtime manifest: {path}: {exc}") from exc
    if not isinstance(raw, dict) or raw.get("schemaVersion") != 2:
        raise SystemExit("runtime manifest must use schemaVersion 2")
    arguments = raw.get("programArguments")
    migrations = raw.get("databaseMigrations")
    if not isinstance(arguments, dict) or not isinstance(migrations, dict):
        raise SystemExit("runtime manifest lacks programArguments or databaseMigrations")
    return {
        "programArguments": arguments,
        "databaseMigrations": migrations,
    }


def build_runtime_contract(
    sources: dict[str, Path],
    *,
    base: dict[str, Any] | None,
    migration_dir: Path | None,
) -> dict[str, Any]:
    arguments: dict[str, list[str]] = {}
    if base is not None:
        arguments = {
            str(program): list(values)
            for program, values in base["programArguments"].items()
        }
    for component, (program, required) in PROGRAM_COMPONENTS.items():
        source = sources.get(component)
        if source is None:
            continue
        command = (
            (sys.executable, str(source), "--help")
            if source.suffix.lower() == ".py"
            else (str(source), "--help")
        )
        try:
            completed = subprocess.run(
                command,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=20,
                check=False,
            )
        except (OSError, subprocess.SubprocessError) as exc:
            raise SystemExit(f"cannot probe program arguments: {component}") from exc
        declared = sorted(set(PROGRAM_ARGUMENT.findall(completed.stdout + "\n" + completed.stderr)))
        missing = sorted(required - set(declared))
        if completed.returncode != 0 or missing:
            detail = ", ".join(missing) if missing else f"exit={completed.returncode}"
            raise SystemExit(f"program argument contract failed: {component}: {detail}")
        arguments[program] = declared
    required_programs = {program for program, _ in PROGRAM_COMPONENTS.values()}
    if set(arguments) != required_programs:
        raise SystemExit("runtime contract lacks one or more managed programs")

    if migration_dir is None:
        if base is None:
            raise SystemExit("runtime contract requires --migration-dir")
        migrations = dict(base["databaseMigrations"])
    else:
        if not migration_dir.is_dir():
            raise SystemExit("--migration-dir must be a directory")
        migrations: dict[str, str] = {}
        for path in sorted(migration_dir.glob("*.sql")):
            match = MIGRATION_NAME.fullmatch(path.name)
            if match is None or match.group("version") in migrations:
                raise SystemExit(f"invalid or duplicate migration: {path.name}")
            migrations[match.group("version")] = hashlib.sha384(path.read_bytes()).hexdigest()
        if not migrations:
            raise SystemExit("migration directory is empty")
    return {
        "programArguments": {name: arguments[name] for name in sorted(arguments)},
        "databaseMigrations": {name: migrations[name] for name in sorted(migrations)},
    }


def zip_info(name: str, *, executable: bool) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3
    info.external_attr = ((0o100755 if executable else 0o100644) & 0xFFFF) << 16
    return info


def write_bytes(
    bundle: zipfile.ZipFile, name: str, content: bytes, *, executable: bool
) -> None:
    bundle.writestr(zip_info(name, executable=executable), content)


def write_file(
    bundle: zipfile.ZipFile, name: str, source: Path, *, executable: bool
) -> None:
    with source.open("rb") as input_file, bundle.open(
        zip_info(name, executable=executable), "w"
    ) as output:
        shutil.copyfileobj(input_file, output, length=1024 * 1024)
