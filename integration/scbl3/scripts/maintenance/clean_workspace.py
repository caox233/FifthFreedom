#!/usr/bin/env python3
"""Safely prune completed SCBL task artifacts and explicitly selected caches.

The default operation is a dry run.  Only direct task directories below the
three registered validation/release roots are eligible.  Build caches are
eligible only with ``--build-caches`` and come from a fixed allowlist; source
trees, current task rows, recent writes, and unknown directories are never
swept implicitly.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import stat
import time
from pathlib import Path


TASK_ID = re.compile(r"[A-Z][A-Z0-9]*(?:-[A-Z0-9]+){1,15}")


def repository_root() -> Path:
    return Path(__file__).resolve().parents[2]


def active_task_ids(root: Path) -> set[str]:
    board = root / "docs/coordination/TASK_COORDINATION.md"
    if not board.is_file():
        return set()
    active: set[str] = set()
    for line in board.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^\|\s*`?([A-Z][A-Z0-9]*(?:-[A-Z0-9]+){1,15})`?\s*\|", line)
        if match and TASK_ID.fullmatch(match.group(1)):
            active.add(match.group(1))
    return active


def task_roots(root: Path, machine: str) -> tuple[tuple[Path, str], ...]:
    return (
        (root / "validation", "validation"),
        (root / "temp" / machine / "validation", "validation"),
        (root / "temp" / machine / "release", "release"),
    )


def directory_size(path: Path) -> int:
    total = 0
    for current, _directories, files in os.walk(path):
        for name in files:
            try:
                total += (Path(current) / name).stat().st_size
            except OSError:
                # Broken/restricted generated entries must not make the dry-run
                # inventory fail.  Deletion still reports a hard error if the
                # exact generated task root cannot be removed.
                pass
    return total


def remove_readonly(function, path, _exc_info) -> None:
    try:
        os.chmod(path, stat.S_IWRITE)
        function(path)
    except FileNotFoundError:
        # Files can disappear while a large generated tree is being removed;
        # that is already the requested end state.
        return


def remove_tree(path: Path) -> None:
    try:
        shutil.rmtree(path, onerror=remove_readonly)
        return
    except OSError:
        if os.name != "nt" or not path.exists():
            raise
    # Deep compiler output can exceed the legacy Win32 path limit.  The
    # extended-length form is still the same already validated task root; it
    # does not broaden cleanup scope or alter ACLs.
    extended = Path("\\\\?\\" + str(path.resolve()))
    shutil.rmtree(extended, onerror=remove_readonly)


def python_cache_candidates(root: Path) -> list[tuple[Path, int]]:
    """Return only real __pycache__ directories contained by the repository."""

    resolved_root = root.resolve()
    selected: list[tuple[Path, int]] = []
    for path in root.rglob("__pycache__"):
        if not path.is_dir() or path.is_symlink() or ".git" in path.parts:
            continue
        resolved = path.resolve()
        if resolved_root not in resolved.parents or resolved.name != "__pycache__":
            raise RuntimeError(f"python cache escaped repository: {path}")
        selected.append((path, directory_size(path)))
    return sorted(selected)


def build_cache_candidates(root: Path) -> list[tuple[Path, int]]:
    """Return only fixed, rebuildable Cargo and .NET output directories."""

    relative_paths = (
        Path("target"),
        Path("client/ScblPublicLauncher/bin"),
        Path("client/ScblPublicLauncher/obj"),
        Path("client/SCBL.Client.Tests/bin"),
        Path("client/SCBL.Client.Tests/obj"),
        Path("client/SCBL.Updater/bin"),
        Path("client/SCBL.Updater/obj"),
        Path("client/SCBL.Updater.Tests/bin"),
        Path("client/SCBL.Updater.Tests/obj"),
    )
    resolved_root = root.resolve()
    selected: list[tuple[Path, int]] = []
    for relative in relative_paths:
        path = root / relative
        if not path.is_dir() or path.is_symlink():
            continue
        resolved = path.resolve()
        if resolved_root not in resolved.parents or resolved.name not in {"target", "bin", "obj"}:
            raise RuntimeError(f"build cache escaped fixed allowlist: {path}")
        selected.append((path, directory_size(path)))
    return selected


def candidates(
    root: Path,
    machine: str,
    *,
    quiescence_minutes: int,
    release_retention_days: int,
    now: float | None = None,
) -> tuple[list[tuple[Path, int]], list[tuple[Path, str]]]:
    now = time.time() if now is None else now
    active = active_task_ids(root)
    selected: list[tuple[Path, int]] = []
    preserved: list[tuple[Path, str]] = []
    for task_root, kind in task_roots(root, machine):
        if not task_root.is_dir():
            continue
        resolved_root = task_root.resolve()
        cutoff_seconds = (
            release_retention_days * 86400 if kind == "release" else quiescence_minutes * 60
        )
        for path in sorted(item for item in task_root.iterdir() if item.is_dir()):
            resolved = path.resolve()
            if resolved.parent != resolved_root or not TASK_ID.fullmatch(path.name):
                preserved.append((path, "unknown-name"))
                continue
            if path.name in active:
                preserved.append((path, "active-board-row"))
                continue
            if now - path.stat().st_mtime < cutoff_seconds:
                preserved.append((path, "recent-write"))
                continue
            selected.append((path, directory_size(path)))
    return selected, preserved


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="delete eligible directories")
    parser.add_argument("--quiescence-minutes", type=int, default=15)
    parser.add_argument("--release-retention-days", type=int, default=3)
    parser.add_argument("--machine", default=os.environ.get("COMPUTERNAME") or "UNKNOWN")
    parser.add_argument(
        "--python-caches",
        action="store_true",
        help="also delete contained __pycache__ directories",
    )
    parser.add_argument(
        "--build-caches",
        action="store_true",
        help="also delete fixed Cargo target and .NET bin/obj directories",
    )
    args = parser.parse_args()
    if args.quiescence_minutes < 1 or args.release_retention_days < 0:
        parser.error("retention values must be non-negative")
    root = repository_root().resolve()
    selected, preserved = candidates(
        root,
        args.machine,
        quiescence_minutes=args.quiescence_minutes,
        release_retention_days=args.release_retention_days,
    )
    python_selected = python_cache_candidates(root) if args.python_caches else []
    build_selected = build_cache_candidates(root) if args.build_caches else []
    total = sum(size for _path, size in selected)
    for path, size in selected:
        print(f"{'DELETE' if args.apply else 'WOULD_DELETE'}\t{size}\t{path}")
    for path, reason in preserved:
        print(f"PRESERVE\t{reason}\t{path}")
    for path, size in python_selected:
        print(f"{'DELETE' if args.apply else 'WOULD_DELETE'}\t{size}\t{path}")
        total += size
    for path, size in build_selected:
        print(f"{'DELETE' if args.apply else 'WOULD_DELETE'}\t{size}\t{path}")
        total += size
    if args.apply:
        failures: list[str] = []
        for path, _size in selected:
            # Candidate discovery already proved this is a direct child of a
            # registered generated root.  Re-check immediately before delete.
            if not TASK_ID.fullmatch(path.name) or path.resolve().parent not in {
                item[0].resolve() for item in task_roots(root, args.machine) if item[0].exists()
            }:
                raise RuntimeError(f"cleanup target escaped registered task roots: {path}")
            try:
                remove_tree(path)
            except OSError as exc:
                failures.append(f"{path}: {exc}")
        if failures:
            raise RuntimeError("cleanup could not remove exact generated targets: " + "; ".join(failures))
        for path, _size in python_selected:
            resolved = path.resolve()
            if (
                path.is_symlink()
                or resolved.name != "__pycache__"
                or root not in resolved.parents
            ):
                raise RuntimeError(f"python cache escaped repository: {path}")
            remove_tree(path)
        for path, _size in build_selected:
            resolved = path.resolve()
            if (
                path.is_symlink()
                or root not in resolved.parents
                or resolved.name not in {"target", "bin", "obj"}
            ):
                raise RuntimeError(f"build cache escaped fixed allowlist: {path}")
            remove_tree(path)
    print(
        f"SUMMARY\tmode={'apply' if args.apply else 'dry-run'}\t"
        f"directories={len(selected) + len(python_selected) + len(build_selected)}\tbytes={total}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
