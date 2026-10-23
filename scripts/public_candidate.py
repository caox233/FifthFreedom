"""Build/verify a reproducible control-plane candidate, never a game release.

Only Python source, its notice and the root license enter the zipapp. Runtime
configuration, TLS material, issuer keys, databases and saves are never inputs.
Verification does not extract or execute anything and is not release admission.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import stat
import zipfile

PROGRAM = "FifthFreedom-Control.pyz"
MANIFEST = "candidate.json"
ENTRY = b"from fifth_freedom_control.__main__ import main\nraise SystemExit(main())\n"
MAX_ARCHIVE = 8 * 1024 * 1024
MAX_MEMBER = 1024 * 1024
MAX_FILES = 64


class CandidateError(ValueError):
    """Bounded diagnostics without source paths or runtime content."""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def _regular(path, limit):
    # Build inputs belong to the reviewed checkout. Reject accidental links,
    # special files and oversized inputs before reading their contents.
    if not stat.S_ISREG(path.lstat().st_mode) or path.stat().st_size > limit:
        raise CandidateError("CANDIDATE_INPUT_INVALID")
    return path.read_bytes()


def _name_allowed(name):
    return name in {"__main__.py", "LICENSE", "fifth_freedom_control/NOTICE.md"} or re.fullmatch(
        r"fifth_freedom_control/[a-z][a-z0-9_]*\.py|fifth_freedom_control/__[a-z]+__\.py", name
    ) is not None


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise CandidateError("CANDIDATE_MANIFEST_INVALID")
        result[key] = value
    return result


def build_candidate(root: Path, output: Path, source_commit: str):
    if re.fullmatch(r"[0-9a-f]{40}", source_commit) is None:
        raise CandidateError("CANDIDATE_REVISION_INVALID")
    root, output = Path(root), Path(output)
    package = root / "src/public/fifth_freedom_control"
    if package.is_symlink() or not package.is_dir():
        raise CandidateError("CANDIDATE_INPUT_INVALID")
    files = {"__main__.py": ENTRY, "LICENSE": _regular(root / "LICENSE", MAX_MEMBER)}
    for path in sorted(package.iterdir()):
        if path.suffix == ".py" or path.name == "NOTICE.md":
            name = f"fifth_freedom_control/{path.name}"
            if not _name_allowed(name):
                raise CandidateError("CANDIDATE_INPUT_INVALID")
            files[name] = _regular(path, MAX_MEMBER)
    required = {"fifth_freedom_control/__main__.py", "fifth_freedom_control/__init__.py",
                "fifth_freedom_control/NOTICE.md"}
    if not required <= files.keys() or len(files) > MAX_FILES:
        raise CandidateError("CANDIDATE_INPUT_INVALID")
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data, compresslevel=9)
    payload = stream.getvalue()
    if len(payload) > MAX_ARCHIVE:
        raise CandidateError("CANDIDATE_INPUT_INVALID")
    manifest = {
        "schema": 1, "component": "fifth-freedom-control-plane-candidate",
        "source_commit": source_commit, "minimum_python": "3.11",
        "complete_game_package": False, "native_public_profile_enabled": False,
        "program": {"name": PROGRAM, "bytes": len(payload), "sha256": digest(payload)},
        "files": {name: {"bytes": len(data), "sha256": digest(data)} for name, data in sorted(files.items())},
    }
    # A candidate gets its own new output directory; never overwrite evidence.
    output.mkdir(parents=True, exist_ok=False)
    with (output / PROGRAM).open("xb") as handle:
        handle.write(payload)
    with (output / MANIFEST).open("x", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    verify_candidate(output)
    return manifest


def verify_candidate(directory: Path):
    directory = Path(directory)
    if directory.is_symlink() or {p.name for p in directory.iterdir()} != {PROGRAM, MANIFEST}:
        raise CandidateError("CANDIDATE_CONTENTS_INVALID")
    try:
        manifest = json.loads(_regular(directory / MANIFEST, MAX_MEMBER), object_pairs_hook=_unique)
        payload = _regular(directory / PROGRAM, MAX_ARCHIVE)
        if (type(manifest) is not dict or set(manifest) != {"schema", "component", "source_commit",
                "minimum_python", "complete_game_package", "native_public_profile_enabled", "program", "files"}
                or type(manifest["schema"]) is not int or manifest["schema"] != 1
                or manifest["component"] != "fifth-freedom-control-plane-candidate"
                or not isinstance(manifest["source_commit"], str)
                or re.fullmatch(r"[0-9a-f]{40}", manifest["source_commit"]) is None
                or manifest["minimum_python"] != "3.11"
                or manifest["complete_game_package"] is not False
                or manifest["native_public_profile_enabled"] is not False
                or manifest["program"] != {"name": PROGRAM, "bytes": len(payload), "sha256": digest(payload)}):
            raise CandidateError("CANDIDATE_MANIFEST_INVALID")
        files = manifest["files"]
        if type(files) is not dict or not 5 <= len(files) <= MAX_FILES:
            raise CandidateError("CANDIDATE_MANIFEST_INVALID")
        required = {"__main__.py", "LICENSE", "fifth_freedom_control/NOTICE.md",
                    "fifth_freedom_control/__init__.py", "fifth_freedom_control/__main__.py"}
        if not required <= files.keys():
            raise CandidateError("CANDIDATE_MANIFEST_INVALID")
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            members = archive.infolist()
            if len(members) != len(files) or {m.filename for m in members} != files.keys():
                raise CandidateError("CANDIDATE_CONTENTS_INVALID")
            for member in members:
                name = member.filename
                if (not _name_allowed(name) or PurePosixPath(name).is_absolute()
                        or member.file_size > MAX_MEMBER or member.flag_bits & 1
                        or stat.S_IFMT(member.external_attr >> 16) != stat.S_IFREG
                        or member.date_time != (1980, 1, 1, 0, 0, 0)):
                    raise CandidateError("CANDIDATE_CONTENTS_INVALID")
                data = archive.read(member)
                if files[name] != {"bytes": len(data), "sha256": digest(data)}:
                    raise CandidateError("CANDIDATE_HASH_MISMATCH")
                if name == "__main__.py" and data != ENTRY:
                    raise CandidateError("CANDIDATE_ENTRY_INVALID")
        return manifest
    except CandidateError:
        raise
    except (OSError, ValueError, TypeError, KeyError, zipfile.BadZipFile, RuntimeError):
        raise CandidateError("CANDIDATE_INVALID") from None


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    build = sub.add_parser("build")
    build.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    build.add_argument("--output", type=Path, required=True)
    build.add_argument("--source-commit", required=True)
    verify = sub.add_parser("verify")
    verify.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        result = (build_candidate(args.root, args.output, args.source_commit) if args.command == "build"
                  else verify_candidate(args.directory))
    except (CandidateError, OSError) as error:
        print(str(error) if isinstance(error, CandidateError) else "CANDIDATE_IO_FAILED")
        return 1
    print(json.dumps({"result": "verified", "component": result["component"],
                      "source_commit": result["source_commit"], "sha256": result["program"]["sha256"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
