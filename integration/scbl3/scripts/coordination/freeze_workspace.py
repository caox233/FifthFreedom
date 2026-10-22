#!/usr/bin/env python3
"""Create an immutable SCBL source freeze with a SHA256 manifest.

Git paths are read as NUL-delimited bytes so non-ASCII names and spaces are
never parsed as quoted display text. Generated workspace roots and build
outputs are excluded explicitly; Rust ``src/bin`` source directories remain.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path, PurePosixPath
from typing import Callable


EXCLUDED_TOP_LEVEL = {".git", "backups", "release-output", "temp", "validation"}
REQUIRED_IGNORED_INPUTS = {
    "client/ScblPublicLauncher/EmbeddedFiles/FusionFix.zip",
}
BUILD_DIRECTORY_PARENTS = {
    "client/SCBL.Client.Tests",
    "client/SCBL.Updater",
    "client/SCBL.Updater.Tests",
    "client/ScblPublicLauncher",
}
MAX_SNAPSHOT_ATTEMPTS = 3
PROFILE_PREFIXES = {
    "launcher": (
        "VERSION_CLIENT",
        "COMPONENT_VERSIONS.json",
        "CHANGELOG.md",
        "client/SCBL.Version.props",
        "client/ScblPublicLauncher",
        "client/SCBL.Client.Tests",
        "client/SCBL.Updater",
        "client/SCBL.Updater.Tests",
        "client/Shared",
        "client/distribution",
        "client/easytier",
        "client/scbl-process-router",
        "server/manager/scblctl/client_component_catalog.json",
        "shared/api",
    ),
    "hooks": (
        "Cargo.toml",
        "Cargo.lock",
        "rust-toolchain.toml",
        "COMPONENT_VERSIONS.json",
        "client/hooks",
        "scripts/build-rust-components.ps1",
        # Cargo loads every workspace member before selecting -p hooks.
        # Keep the complete Rust workspace structurally valid while only the
        # requested package is compiled.
        "server/dedicated-server",
        "server/quazal",
        "shared/api",
    ),
    "dedicated": (
        "Cargo.toml",
        "Cargo.lock",
        "rust-toolchain.toml",
        "COMPONENT_VERSIONS.json",
        "client/hooks",
        "scripts/build-rust-components.ps1",
        "server/dedicated-server",
        "server/quazal",
        "shared/api",
    ),
    "runtime": (
        "Cargo.toml",
        "Cargo.lock",
        "rust-toolchain.toml",
        "COMPONENT_VERSIONS.json",
        "server",
        "shared/api",
    ),
    "platform-tools": (
        "scripts/release",
        "server/manager",
        "server/test_coordination_tools.py",
        "server/test_freeze_workspace.py",
        "server/test_workspace_cleanup.py",
    ),
}


class WorkspaceChangedError(RuntimeError):
    pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", required=True)
    parser.add_argument(
        "--profile",
        choices=("full", *PROFILE_PREFIXES),
        default="full",
        help="freeze only the selected component dependency closure",
    )
    return parser.parse_args()


def repository_root() -> Path:
    return Path(__file__).resolve().parents[2]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_excluded(relative: str) -> bool:
    path = PurePosixPath(relative)
    if not path.parts or path.parts[0] in EXCLUDED_TOP_LEVEL:
        return True
    for parent in BUILD_DIRECTORY_PARENTS:
        parent_parts = PurePosixPath(parent).parts
        if path.parts[: len(parent_parts)] == parent_parts:
            remainder = path.parts[len(parent_parts) :]
            if remainder and remainder[0] in {"bin", "obj", "publish-single"}:
                return True
    if path.parts[:2] == ("server", "packaging") and "dist" in path.parts[2:3]:
        return True
    return False


def profile_includes(relative: str, profile: str) -> bool:
    if profile == "full":
        return True
    return any(
        relative == prefix or relative.startswith(prefix.rstrip("/") + "/")
        for prefix in PROFILE_PREFIXES[profile]
    )


def listed_relative_paths(root: Path, profile: str = "full") -> list[str]:
    result = subprocess.run(
        ["git", "-C", str(root), "-c", "core.quotepath=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        check=True,
        capture_output=True,
    )
    paths = {
        item.decode("utf-8")
        for item in result.stdout.split(b"\0")
        if item
        and not is_excluded(item.decode("utf-8"))
        and profile_includes(item.decode("utf-8"), profile)
    }
    # Release assets may be intentionally ignored globally by extension while
    # still being required immutable Launcher/Client Files inputs. Include only
    # the exact managed paths; never sweep arbitrary ignored binaries or ZIPs.
    paths.update(
        relative
        for relative in REQUIRED_IGNORED_INPUTS
        if profile_includes(relative, profile)
        and (root / Path(*PurePosixPath(relative).parts)).is_file()
    )
    return sorted(paths)


def available_relative_paths(root: Path, profile: str = "full") -> list[str]:
    return [
        relative
        for relative in listed_relative_paths(root, profile)
        if (root / Path(*PurePosixPath(relative).parts)).is_file()
    ]


def copy_snapshot(
    root: Path,
    freeze: Path,
    relative_paths: list[str],
    copy_file: Callable[[Path, Path], object],
) -> list[dict[str, object]]:
    manifest: list[dict[str, object]] = []
    for relative in relative_paths:
        source = root / Path(*PurePosixPath(relative).parts)
        target = freeze / Path(*PurePosixPath(relative).parts)
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            copy_file(source, target)
        except (FileNotFoundError, NotADirectoryError) as exc:
            if getattr(exc, "winerror", None) == 206:
                raise
            raise WorkspaceChangedError(
                f"workspace file disappeared during freeze: {relative}"
            ) from exc
        manifest.append(
            {
                "path": relative,
                "size": target.stat().st_size,
                "sha256": sha256_file(target),
            }
        )
    return manifest


def verify_snapshot_sources(
    root: Path,
    relative_paths: list[str],
    manifest: list[dict[str, object]],
    profile: str = "full",
) -> None:
    if available_relative_paths(root, profile) != relative_paths:
        raise WorkspaceChangedError("workspace file list changed during freeze")
    for item in manifest:
        relative = str(item["path"])
        source = root / Path(*PurePosixPath(relative).parts)
        try:
            if source.stat().st_size != item["size"] or sha256_file(source) != item["sha256"]:
                raise WorkspaceChangedError(f"workspace file changed during freeze: {relative}")
        except (FileNotFoundError, NotADirectoryError) as exc:
            raise WorkspaceChangedError(
                f"workspace file disappeared during freeze: {relative}"
            ) from exc


def write_manifest(destination: Path, manifest: list[dict[str, object]]) -> dict[str, object]:
    manifest_path = destination / "freeze-input.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    return {
        "files": len(manifest),
        "bytes": sum(int(item["size"]) for item in manifest),
        "manifest": str(manifest_path),
        "manifestSha256": sha256_file(manifest_path),
    }


def create_freeze(
    root: Path,
    destination: Path,
    *,
    copy_file: Callable[[Path, Path], object] = shutil.copy2,
    max_attempts: int = MAX_SNAPSHOT_ATTEMPTS,
    staging_root: Path | None = None,
    profile: str = "full",
) -> dict[str, object]:
    if max_attempts <= 0:
        raise ValueError("max_attempts must be positive")
    if destination.exists():
        raise FileExistsError(f"Destination already exists: {destination}")

    staging_root = destination.parent if staging_root is None else staging_root
    staging_root.mkdir(parents=True, exist_ok=True)
    last_error: BaseException | None = None
    for attempt in range(1, max_attempts + 1):
        staging = staging_root / f".sf-{os.getpid()}-{attempt}"
        if staging.exists():
            raise FileExistsError(f"Freeze staging path already exists: {staging}")
        freeze = staging / "freeze"
        freeze.mkdir(parents=True)
        try:
            if profile != "full" and profile not in PROFILE_PREFIXES:
                raise ValueError(f"unknown freeze profile: {profile}")
            relative_paths = available_relative_paths(root, profile)
            manifest = copy_snapshot(root, freeze, relative_paths, copy_file)
            verify_snapshot_sources(root, relative_paths, manifest, profile)
            summary = write_manifest(staging, manifest)
            summary["profile"] = profile
            if destination.exists():
                raise FileExistsError(f"Destination already exists: {destination}")
            destination.parent.mkdir(parents=True, exist_ok=True)
            staging.rename(destination)
            summary["manifest"] = str(destination / "freeze-input.json")
            return summary
        except WorkspaceChangedError as exc:
            last_error = exc
            shutil.rmtree(staging, ignore_errors=True)
            if attempt == max_attempts:
                break
        except BaseException:
            shutil.rmtree(staging, ignore_errors=True)
            raise

    raise WorkspaceChangedError(
        f"workspace did not remain stable for {max_attempts} freeze attempts"
    ) from last_error


def main() -> int:
    args = parse_args()
    root = repository_root()
    destination = Path(args.destination).resolve()
    allowed_root = (root / "temp" / (os.environ.get("COMPUTERNAME") or "UNKNOWN") / "release").resolve()
    try:
        destination.relative_to(allowed_root)
    except ValueError as exc:
        raise SystemExit(f"Destination must be inside {allowed_root}: {destination}") from exc
    if destination.exists():
        raise SystemExit(f"Destination already exists: {destination}")

    summary = create_freeze(
        root,
        destination,
        staging_root=allowed_root,
        profile=args.profile,
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
