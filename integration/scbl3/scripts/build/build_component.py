#!/usr/bin/env python3
"""Fixed SCBL .NET component build entry with persistent external caches."""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path


def repository_root() -> Path:
    return Path(__file__).resolve().parents[2]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def cache_root() -> Path:
    configured = os.environ.get("SCBL_BUILD_CACHE", "").strip()
    if configured:
        path = Path(configured)
    else:
        base = os.environ.get("LOCALAPPDATA", "").strip() or os.environ.get("USERPROFILE", "").strip()
        if not base:
            raise ValueError("SCBL build cache root is unavailable")
        path = Path(base) / "SCBL" / "build-cache"
    if not path.is_absolute():
        raise ValueError("SCBL build cache must be absolute")
    return path.resolve()


def build_command(
    dotnet: str,
    project: Path,
    output: Path,
    cache: Path,
    version: str,
) -> list[str]:
    return [
        dotnet,
        "publish",
        str(project),
        "-c",
        "Release",
        "-r",
        "win-x86",
        "--self-contained",
        "true",
        "--nologo",
        "/p:PublishSingleFile=true",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "/p:EnableCompressionInSingleFile=true",
        "/p:DebugType=none",
        "/p:DebugSymbols=false",
        f"/p:ScblVersion={version}",
        f"/p:BaseIntermediateOutputPath={cache / 'obj'}{os.sep}",
        f"/p:BaseOutputPath={cache / 'bin'}{os.sep}",
        "-o",
        str(output),
    ]


@contextlib.contextmanager
def component_lock(cache: Path):
    lock = cache / ".build.lock"
    try:
        descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError as exc:
        raise RuntimeError(f"component build cache is already active: {cache}") from exc
    try:
        with os.fdopen(descriptor, "w", encoding="ascii") as output:
            output.write(f"pid={os.getpid()}\n")
        yield
    finally:
        lock.unlink(missing_ok=True)


def copy_distribution(source_root: Path, output: Path) -> None:
    distribution = source_root / "client/distribution"
    if not distribution.is_dir():
        raise RuntimeError(f"client distribution is missing: {distribution}")
    for source in distribution.rglob("*"):
        if not source.is_file() or source.name == "client-files.rules.json":
            continue
        relative = source.relative_to(distribution)
        destination = output / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("component", choices=("launcher", "updater"))
    parser.add_argument("--source-root", type=Path, default=repository_root())
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--receipt", required=True, type=Path)
    parser.add_argument(
        "--version",
        help="immutable candidate version; defaults to the frozen VERSION_CLIENT",
    )
    args = parser.parse_args()
    source_root = args.source_root.resolve(strict=True)
    version = args.version or (source_root / "VERSION_CLIENT").read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("candidate version must use X.Y.Z")
    output = args.output.resolve()
    try:
        output.relative_to(source_root)
    except ValueError:
        pass
    else:
        raise SystemExit("build output must be outside the immutable source root")
    if output.exists() and any(output.iterdir()):
        raise SystemExit(f"immutable build output is not empty: {output}")
    output.mkdir(parents=True, exist_ok=True)
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise SystemExit("dotnet is unavailable")
    cache = cache_root() / args.component
    cache.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ)
    environment["NUGET_PACKAGES"] = str(cache_root() / "nuget-packages")
    environment["DOTNET_CLI_HOME"] = str(cache_root() / "dotnet-home")
    if args.component == "launcher":
        project = source_root / "client/ScblPublicLauncher/SplinterCellCNLauncher.csproj"
        artifact_name = "SCBL.exe"
    else:
        project = source_root / "client/SCBL.Updater/SCBL.Updater.csproj"
        artifact_name = "SCBL.Updater.exe"
    if not project.is_file():
        raise SystemExit(f"component project is missing: {project}")
    try:
        with component_lock(cache):
            completed = subprocess.run(
                build_command(dotnet, project, output, cache, version),
                cwd=source_root,
                env=environment,
                check=False,
            )
    except RuntimeError as exc:
        raise SystemExit(str(exc)) from exc
    if completed.returncode != 0:
        return completed.returncode
    artifact = output / artifact_name
    if not artifact.is_file() or artifact.stat().st_size == 0:
        raise SystemExit(f"formal component artifact is missing: {artifact}")
    if args.component == "launcher":
        copy_distribution(source_root, output)
    receipt = {
        "schemaVersion": 1,
        "operation": "fixed-component-build",
        "status": "completed",
        "component": args.component,
        "version": version,
        "sourceRoot": str(source_root),
        "cacheRoot": str(cache),
        "artifact": {
            "path": str(artifact),
            "size": artifact.stat().st_size,
            "sha256": sha256(artifact),
        },
    }
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
