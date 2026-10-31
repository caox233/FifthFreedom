"""Verify explicitly cached public-network archives against the reviewed lock, without installation."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "scripts/public-network-dependencies.lock.json"
MAX_ARCHIVE_BYTES = 64 * 1024 * 1024
MAX_EXPANDED_BYTES = 512 * 1024 * 1024


def safe_relative(name: str) -> bool:
    return (isinstance(name, str) and bool(name) and not name.startswith("/")
            and "\\" not in name and ":" not in name and "\0" not in name
            and all(part not in ("", ".", "..") for part in name.split("/")))


def matches(data: bytes, row: dict) -> bool:
    return (type(row["bytes"]) is int and len(data) == row["bytes"]
            and hashlib.sha256(data).hexdigest() == row["sha256"])


def verify(lock: dict, cache: Path, root: Path = ROOT) -> tuple[dict, int]:
    if lock.get("schema") != 1 or not lock.get("artifacts"):
        raise ValueError("invalid_lock")
    results, notices = [], []
    for row in lock["artifacts"]:
        identity, filename = row["id"], row["filename"]
        if (not re.fullmatch(r"[a-z0-9._-]+", identity) or not safe_relative(filename)
                or len(PurePosixPath(filename).parts) != 1
                or type(row["bytes"]) is not int or not 0 < row["bytes"] <= MAX_ARCHIVE_BYTES):
            raise ValueError("invalid_lock_artifact")
        status = "invalid"
        path = cache / filename
        try:
            if path.is_symlink():
                raise ValueError("linked_archive")
            if not stat.S_ISREG(path.stat().st_mode):
                raise ValueError("nonregular_archive")
            with path.open("rb") as stream:
                content = stream.read(row["bytes"] + 1)
            if not matches(content, row):
                raise ValueError("archive_identity_mismatch")
            with zipfile.ZipFile(io.BytesIO(content)) as archive:
                entries = archive.infolist()
                names = [entry.filename for entry in entries]
                if (len(names) != len(set(names)) or any(not safe_relative(name.rstrip("/")) for name in names)
                        or sum(entry.file_size for entry in entries) > MAX_EXPANDED_BYTES):
                    raise ValueError("invalid_archive_layout")
                for member in row["required_members"]:
                    if (not safe_relative(member["name"]) or type(member["bytes"]) is not int
                            or not 0 < member["bytes"] <= MAX_EXPANDED_BYTES):
                        raise ValueError("invalid_locked_member")
                    with archive.open(member["name"]) as part:
                        if not matches(part.read(member["bytes"] + 1), member):
                            raise ValueError("member_identity_mismatch")
            status = "verified"
        except FileNotFoundError:
            status = "missing"
        except (OSError, ValueError, KeyError, zipfile.BadZipFile, RuntimeError):
            pass
        results.append({"id": identity, "status": status})
    for row in lock.get("license_references", []):
        relative = row.get("retained_notice")
        if relative is None:
            continue
        if not safe_relative(relative):
            raise ValueError("invalid_notice_path")
        path = root / relative
        try:
            valid = not path.is_symlink() and matches(path.read_bytes(), row)
        except OSError:
            valid = False
        notices.append({"id": row["id"], "verified": valid})
    invalid = any(row["status"] == "invalid" for row in results) or any(not row["verified"] for row in notices)
    missing = any(row["status"] == "missing" for row in results)
    code = 1 if invalid else 2 if missing else 0
    return {"schema": 1, "artifacts": results, "retained_notices": notices,
            "all_locked_bytes_verified": code == 0,
            "closure_complete": False, "publication_approved": False,
            "runtime_executed": False, "installed": False,
            "closure_holds": lock.get("closure_holds", []), "exit_code": code}, code


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True,
                        help="Directory containing the explicitly downloaded lock-named archives")
    args = parser.parse_args()
    try:
        result, code = verify(json.loads(LOCK.read_text()), args.cache)
    except (OSError, ValueError, KeyError, TypeError):
        result, code = {"status": "invalid_lock", "exit_code": 1}, 1
    print(json.dumps(result, indent=2))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
