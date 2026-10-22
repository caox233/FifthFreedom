from __future__ import annotations

import json
import os
import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .paths import DEPLOYMENT_PATHS
from .retention import RETAINED_GENERATIONS


MANAGED_RELEASE = re.compile(r"\d+\.\d+\.\d+-\d{14}")
SHA256_DIRECTORY = re.compile(r"[0-9a-f]{64}")


class MaintenanceError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class CleanupResult:
    releases: tuple[Path, ...]
    components: tuple[Path, ...]
    bytes_removed: int


def prune_obsolete_runtime_storage(*, apply: bool) -> CleanupResult:
    _require_root()
    releases = Path(DEPLOYMENT_PATHS.releases)
    store = Path(DEPLOYMENT_PATHS.component_store)
    retained_releases = _retained_release_paths()
    if not retained_releases:
        raise MaintenanceError("current/previous 均不可用，拒绝清理")

    release_candidates: list[Path] = []
    if releases.is_dir():
        for path in releases.iterdir():
            if (
                path.is_dir()
                and not path.is_symlink()
                and MANAGED_RELEASE.fullmatch(path.name)
                and path.resolve() not in retained_releases
            ):
                release_candidates.append(path)

    referenced = _referenced_component_digests(retained_releases)
    component_candidates: list[Path] = []
    if store.is_dir():
        for path in store.iterdir():
            if (
                path.is_dir()
                and not path.is_symlink()
                and SHA256_DIRECTORY.fullmatch(path.name)
                and path.name not in referenced
            ):
                component_candidates.append(path)

    selected = [*release_candidates, *component_candidates]
    total = sum(_tree_size(path) for path in selected)
    if apply:
        for path in selected:
            shutil.rmtree(path)
    return CleanupResult(
        tuple(sorted(release_candidates)),
        tuple(sorted(component_candidates)),
        total,
    )


def _require_root() -> None:
    if os.name != "posix" or os.geteuid() != 0:
        raise MaintenanceError("运行时清理只能由 Linux root 执行")


def _retained_release_paths() -> set[Path]:
    retained: set[Path] = set()
    releases = Path(DEPLOYMENT_PATHS.releases).resolve()
    for name in (DEPLOYMENT_PATHS.current, DEPLOYMENT_PATHS.previous):
        link = Path(name)
        if link.is_symlink():
            try:
                target = link.resolve(strict=True)
            except FileNotFoundError:
                continue
            if releases in target.parents:
                retained.add(target)
    if releases.is_dir():
        candidates = sorted(
            (
                path.resolve()
                for path in releases.iterdir()
                if path.is_dir()
                and not path.is_symlink()
                and MANAGED_RELEASE.fullmatch(path.name)
            ),
            key=lambda path: path.name,
            reverse=True,
        )
        for path in candidates:
            if len(retained) >= RETAINED_GENERATIONS:
                break
            retained.add(path)
    return retained


def _referenced_component_digests(releases: set[Path]) -> set[str]:
    result: set[str] = set()
    for release in releases:
        try:
            raw: Any = json.loads(
                (release / "component-versions.json").read_text(encoding="utf-8")
            )
        except (OSError, json.JSONDecodeError):
            raise MaintenanceError(f"保留版本的组件清单无效：{release}")
        if not isinstance(raw, dict):
            raise MaintenanceError(f"保留版本的组件清单无效：{release}")
        for entry in raw.values():
            if isinstance(entry, dict):
                digest = entry.get("sha256")
                if isinstance(digest, str) and SHA256_DIRECTORY.fullmatch(digest):
                    result.add(digest)
    return result


def _tree_size(root: Path) -> int:
    return sum(
        path.stat().st_size
        for path in root.rglob("*")
        if path.is_file() and not path.is_symlink()
    )
