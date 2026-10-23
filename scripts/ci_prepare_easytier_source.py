"""Prepare the exact reviewed Linux-only EasyTier source inputs, without building.

The canonical Git archive contains held Windows binaries and stays private.
This derived tree is explicitly incomplete for non-Linux builds. Preparation is
not license closure, runtime admission, or reproduction of an official binary.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import posixpath
import stat
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "scripts/easytier-source.lock.json"
MAX_ARCHIVE = 32 * 1024 * 1024
MAX_CONTENT = 64 * 1024 * 1024


class PreparationError(ValueError):
    """Fixed diagnostic codes; never report file contents."""


def sha(data):
    return hashlib.sha256(data).hexdigest()


def encoded(value):
    return (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()


def safe_name(name):
    return (isinstance(name, str) and name and not name.startswith("/") and "\\" not in name
            and ":" not in name and "\0" not in name
            and all(part not in ("", ".", "..", ".git") for part in name.split("/")))


def checked_bytes(path, row, maximum=MAX_ARCHIVE):
    info = path.lstat()
    if (not stat.S_ISREG(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400
            or info.st_nlink != 1 or type(row["bytes"]) is not int or not 0 < row["bytes"] <= maximum
            or info.st_size != row["bytes"]):
        raise PreparationError("SOURCE_INPUT_INVALID")
    with path.open("rb") as source:
        data = source.read(row["bytes"] + 1)
    if len(data) != row["bytes"] or sha(data) != row["sha256"]:
        raise PreparationError("SOURCE_INPUT_DIGEST_MISMATCH")
    return data


def prebuilt(data):
    return data.startswith((b"MZ", b"\x7fELF", b"!<arch>\n", b"PK\x03\x04", b"PK\x05\x06",
                            b"\0asm", b"\xfe\xed\xfa\xce", b"\xfe\xed\xfa\xcf",
                            b"\xce\xfa\xed\xfe", b"\xcf\xfa\xed\xfe"))


def archive_files(data, lock):
    files, links, names = {}, {}, set()
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
        members = archive.getmembers()
        if len(members) > 5000 or sum(member.size for member in members) > MAX_CONTENT:
            raise PreparationError("SOURCE_ARCHIVE_LIMIT")
        for member in members:
            name = member.name.rstrip("/") if member.isdir() else member.name
            if not safe_name(name) or name in names or member.mode & 0o7000:
                raise PreparationError("SOURCE_ARCHIVE_LAYOUT_INVALID")
            names.add(name)
            if member.issym():
                links[name] = member.linkname
            elif member.isfile():
                files[name] = {"data": archive.extractfile(member).read(), "mode": 0o755 if member.mode & 0o111 else 0o644}
            elif not member.isdir():
                raise PreparationError("SOURCE_ARCHIVE_SPECIAL_ENTRY")
    if links != {row["path"]: row["target"] for row in lock["materialized_links"]}:
        raise PreparationError("SOURCE_LINK_SET_MISMATCH")
    for row in lock["omitted_files"]:
        item = files.pop(row["path"], None)
        if item is None or len(item["data"]) != row["bytes"] or sha(item["data"]) != row["sha256"]:
            raise PreparationError("SOURCE_OMISSION_MISMATCH")
    for name, item in files.items():
        if prebuilt(item["data"]) or PurePosixPath(name).suffix.lower() in {
                ".dll", ".lib", ".sys", ".exe", ".a", ".so", ".dylib", ".jar", ".zip", ".o", ".obj"}:
            raise PreparationError("SOURCE_UNREVIEWED_PREBUILT")
    # These five exact upstream links are materialized from already checked
    # in-archive files. No filesystem link or arbitrary target is followed.
    materialized = []
    for name, target in sorted(links.items()):
        resolved = posixpath.normpath(str(PurePosixPath(name).parent / target))
        if not safe_name(resolved):
            raise PreparationError("SOURCE_LINK_TARGET_INVALID")
        source_rows = {key: item for key, item in files.items() if key == resolved or key.startswith(resolved + "/")}
        if not source_rows:
            raise PreparationError("SOURCE_LINK_TARGET_MISSING")
        for source_name, item in source_rows.items():
            destination = name + source_name[len(resolved):]
            if destination in files:
                raise PreparationError("SOURCE_LINK_COLLISION")
            files[destination] = dict(item)
            materialized.append({"path": destination, "source": source_name, "bytes": len(item["data"]), "sha256": sha(item["data"])})
    return files, materialized


def scan(root):
    rows = {}
    for path in sorted(root.rglob("*")):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise PreparationError("SOURCE_OUTPUT_LINK")
        if path.is_dir():
            continue
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > MAX_CONTENT:
            raise PreparationError("SOURCE_OUTPUT_INVALID")
        data = path.read_bytes()
        if prebuilt(data):
            raise PreparationError("SOURCE_OUTPUT_PREBUILT")
        rows[path.relative_to(root).as_posix()] = {"bytes": len(data), "sha256": sha(data),
            "mode": 0o755 if info.st_mode & 0o111 else 0o644}
    return rows


def prepare(archive, destination, lock, repository=ROOT):
    destination = Path(os.path.abspath(destination))
    if destination.exists() or destination.is_symlink():
        raise PreparationError("SOURCE_OUTPUT_EXISTS")
    for parent in destination.parents:
        if parent.exists() or parent.is_symlink():
            info = parent.lstat()
            if not stat.S_ISDIR(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise PreparationError("SOURCE_OUTPUT_PARENT_INVALID")
    data = checked_bytes(Path(archive), lock["archive"])
    files, materialized = archive_files(data, lock)
    patches = []
    for row in lock["patches"]:
        if not safe_name(row["path"]):
            raise PreparationError("SOURCE_PATCH_PATH_INVALID")
        patches.append((repository / row["path"], checked_bytes(repository / row["path"], row, 1024 * 1024)))
    if sha(files["Cargo.lock"]["data"]) != lock["cargo_lock_sha256"]:
        raise PreparationError("SOURCE_CARGO_LOCK_MISMATCH")
    for row in [*lock["required_notices"], *lock["modified_files"]]:
        if sha(files[row["path"]]["data"]) != row.get("original_sha256", row.get("sha256")):
            raise PreparationError("SOURCE_REQUIRED_BASE_MISMATCH")
    destination.mkdir(parents=True, exist_ok=False)
    for name, item in files.items():
        path = destination / name
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("xb") as handle:
            handle.write(item["data"])
        path.chmod(item["mode"])
    env = os.environ.copy()
    # Prevent nested preparation from discovering the product repository's Git
    # metadata. The build recipe retains the same ceiling for git-version.
    env["GIT_CEILING_DIRECTORIES"] = str(destination.parent)
    for _, patch_data in patches:
        for check in (True, False):
            # Reviewed patches name exact LF byte identities. A runner's global
            # Windows autocrlf setting must not rewrite the resulting source.
            result = subprocess.run(["git", "-c", "core.autocrlf=false", "-c", "core.eol=lf", "apply",
                                    *(["--check"] if check else []), "--whitespace=error-all", "-"],
                                    cwd=destination, env=env, input=patch_data, capture_output=True, timeout=30)
            if result.returncode:
                raise PreparationError("SOURCE_PATCH_REJECTED")
    actual = scan(destination)
    expected = {name: {"bytes": len(item["data"]), "sha256": sha(item["data"]), "mode": item["mode"]}
                for name, item in files.items()}
    for row in lock["modified_files"]:
        expected[row["path"]] = {**expected[row["path"]], "sha256": row["patched_sha256"],
                                 "bytes": actual[row["path"]]["bytes"]}
    if actual != expected:
        raise PreparationError("SOURCE_PATCH_SCOPE_OR_DIGEST_MISMATCH")
    receipt = {"schema": 1, "kind": "distinct-patched-linux-source", "upstream": lock["upstream"],
        "original_archive": lock["archive"], "patches": lock["patches"], "source_files": actual,
        "patch_manifest_sha256": sha(encoded(lock["patches"])),
        "source_tree_sha256": sha(encoded(actual)), "omitted_files": lock["omitted_files"],
        "materialized_links": materialized, "required_notices": lock["required_notices"],
        "cargo_lock_sha256": lock["cargo_lock_sha256"], "build_inputs": lock["build"],
        "admission": lock["admission"], "original_archive_preserved": True,
        "complete_upstream_snapshot": False, "dependency_source_closure_verified": False,
        "runtime_built": False, "runtime_executed": False, "published": False}
    (destination / "FIFTH-FREEDOM-SOURCE-RECEIPT.json").write_bytes(encoded(receipt))
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        result = prepare(args.archive, args.output, json.loads(LOCK.read_bytes()))
        print(json.dumps({"source_tree_sha256": result["source_tree_sha256"], "files": len(result["source_files"]),
                          "omitted": len(result["omitted_files"]), "runtime_built": False, "publication_approved": False}))
        return 0
    except (OSError, ValueError, KeyError, tarfile.TarError, subprocess.SubprocessError) as error:
        print(str(error) if isinstance(error, PreparationError) else "SOURCE_PREPARATION_FAILED")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
