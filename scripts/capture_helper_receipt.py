"""Inspect the retained capture binary and an explicitly supplied source without executing either.

This receipt establishes input identity only. It never authorizes publication,
updates an asset pin, installs a helper, or claims a successful rebuild.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import stat
import struct

ROOT = Path(__file__).resolve().parents[1]
BINARY_PATH = "src/launcher/src/Assets/SCBL.LAN.Capture.exe"
BINARY_SHA256 = "d34d192bb97a379c4b063c24f7cea6789005b9e5a5584404b701dfd4e7533a11"
BINARY_BYTES = 810496
REPORTED_SOURCE_SHA256 = "bed779d4302b68e4397a8e3fe9afddfa886c9b96c8667a4a9279def85e11a43b"
MAX_SOURCE_BYTES = 16 * 1024 * 1024


def read_regular(path: Path, maximum: int) -> bytes:
    # A supplied path is explicit: do not search private folders or follow links
    # that could silently substitute a different input.
    for component in (path, *path.parents):
        if component.is_symlink():
            raise ValueError("symlink_input")
    if not stat.S_ISREG(path.stat().st_mode):
        raise ValueError("nonregular_input")
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("input_too_large")
    return data


def is_x86_executable(data: bytes) -> bool:
    """Recognize bounded PE32 executable headers; do not load an image."""
    if len(data) < 64 or data[:2] != b"MZ":
        return False
    offset = struct.unpack_from("<I", data, 0x3C)[0]
    if offset < 64 or offset + 26 > len(data) or data[offset:offset + 4] != b"PE\0\0":
        return False
    machine = struct.unpack_from("<H", data, offset + 4)[0]
    optional_size, characteristics = struct.unpack_from("<HH", data, offset + 20)
    magic = struct.unpack_from("<H", data, offset + 24)[0]
    return (machine == 0x14C and magic == 0x10B and optional_size >= 96
            and offset + 24 + optional_size <= len(data)
            and bool(characteristics & 0x2) and not bool(characteristics & 0x2000))


def inspect(root: Path = ROOT, source: Path | None = None) -> tuple[dict, int]:
    errors: list[str] = []
    manifest_verified = binary_verified = source_verified = False
    try:
        manifest = json.loads(read_regular(root / "scripts/local-assets.json", 1024 * 1024))
        row = manifest["files"][BINARY_PATH]
        manifest_verified = (row["sha256"] == BINARY_SHA256
                             and type(row["bytes"]) is int and row["bytes"] == BINARY_BYTES)
    except (OSError, ValueError, KeyError, TypeError, RecursionError):
        pass
    if not manifest_verified:
        errors.append("retained_binary_manifest_mismatch_or_unavailable")
    try:
        binary = read_regular(root / BINARY_PATH, BINARY_BYTES)
        binary_verified = (len(binary) == BINARY_BYTES
                           and hashlib.sha256(binary).hexdigest() == BINARY_SHA256
                           and is_x86_executable(binary))
    except (OSError, ValueError):
        pass
    if not binary_verified:
        errors.append("retained_binary_identity_mismatch_or_unavailable")
    if source is not None:
        try:
            source_verified = hashlib.sha256(read_regular(source, MAX_SOURCE_BYTES)).hexdigest() == REPORTED_SOURCE_SHA256
        except (OSError, ValueError):
            pass
        if not source_verified:
            errors.append("reported_source_digest_mismatch_or_unavailable")

    status = ("input_verification_failed" if errors else
              "reported_source_identity_verified" if source_verified else "blocked_source_not_supplied")
    code = 1 if errors else 0 if source_verified else 2
    return {
        "schema": 1,
        "status": status,
        "exit_code": code,
        "retained_binary": {"path": BINARY_PATH, "sha256": BINARY_SHA256,
                            "bytes": BINARY_BYTES, "manifest_verified": manifest_verified,
                            "sha256_size_and_x86_pe_verified": binary_verified},
        "reported_source": {"sha256": REPORTED_SOURCE_SHA256,
                            "supplied": source is not None, "digest_verified": source_verified},
        "errors": errors,
        "source_contents_included": False,
        "rebuilt_binary_verified": False,
        "source_binary_equivalence_verified": False,
        "publication_approved": False,
        "capture_executed": False,
        "remaining_gates": [
            *([] if source_verified else ["receive_reviewed_source_with_reported_digest"]),
            "review_source_and_build_recipe",
            "compile_with_recorded_windows_msvc_x86_toolchain",
            "compare_rebuild_and_preserve_original_binary",
            "review_source_rights_and_publication",
        ],
    }, code


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path,
                        help="Explicit received source path; only its expected digest is included in output")
    args = parser.parse_args()
    receipt, code = inspect(source=args.source)
    print(json.dumps(receipt, indent=2))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
