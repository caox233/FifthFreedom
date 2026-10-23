"""Assemble same-run components, then verify both identities with the native reader.

This is not a complete Windows client, installable game package or enabled public
server. Inputs are reviewed GitHub-hosted artifacts from this exact run/attempt
and revision. Hashes establish local integrity, not publisher authentication.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys

from package_catalog import build_release_catalog, json_bytes

ROOT = Path(__file__).resolve().parents[1]
WINDOWS = {
    "FifthFreedom.component-only.exe": ("FifthFreedom.exe", 0x14C, False),
    "uplay_r1_loader.dll": ("runtime/uplay_r1_loader.dll", 0x14C, True),
    "scbl_gameplay.dll": ("runtime/scbl_gameplay.dll", 0x14C, True),
    "FifthFreedom.Server.exe": ("runtime/FifthFreedom.Server.exe", 0x8664, False),
}
LINUX = {"dedicated_server", "verify_public_package"}
LIMIT = 512 * 1024 * 1024
SCOPE = {"complete_game_package": False, "complete_windows_client": False,
         "native_public_profile_enabled": False, "game_executed": False,
         "deployed": False, "published": False}


class CandidateError(ValueError):
    """Fixed diagnostic codes only; do not echo untrusted input content."""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise CandidateError("INPUT_JSON_DUPLICATE")
        value[key] = item
    return value


def regular(path, maximum=LIMIT):
    path = Path(os.path.abspath(path))
    for component in (*reversed(path.parents), path):
        info = component.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise CandidateError("INPUT_LINK_REJECTED")
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or not 0 < info.st_size <= maximum:
        raise CandidateError("INPUT_FILE_INVALID")
    return info


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_nlink, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns, getattr(info, "st_birthtime_ns", None))


def opened_identity(info):
    if sys.platform != "win32":
        return identity(info)
    # CPython's Windows path stat adds extension-based execute bits and maps
    # ctime to creation time, whereas fstat reports ChangeTime. Bind the path
    # and opened handle through common fields; still compare each API's full
    # metadata before/after reading, including its own mode and ctime.
    return (info.st_dev, info.st_ino, info.st_mode & ~0o111, info.st_nlink,
            info.st_size, info.st_mtime_ns, getattr(info, "st_birthtime_ns", None))


def sha_file(path):
    before = regular(path)
    sha = hashlib.sha256()
    size = 0
    with Path(path).open("rb") as handle:
        opened = os.fstat(handle.fileno())
        if opened_identity(before) != opened_identity(opened):
            raise CandidateError("INPUT_CHANGED")
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            size += len(block)
            if size > LIMIT:
                raise CandidateError("INPUT_FILE_INVALID")
            sha.update(block)
        if identity(opened) != identity(os.fstat(handle.fileno())):
            raise CandidateError("INPUT_CHANGED")
    if size != before.st_size or identity(before) != identity(regular(path)):
        raise CandidateError("INPUT_CHANGED")
    return {"bytes": size, "sha256": sha.hexdigest()}


def read_small(path, maximum=1024 * 1024):
    before = regular(path, maximum)
    with Path(path).open("rb") as handle:
        opened = os.fstat(handle.fileno())
        if opened_identity(before) != opened_identity(opened):
            raise CandidateError("INPUT_CHANGED")
        data = handle.read(maximum + 1)
        if identity(opened) != identity(os.fstat(handle.fileno())):
            raise CandidateError("INPUT_CHANGED")
    if len(data) != before.st_size or len(data) > maximum or identity(before) != identity(regular(path, maximum)):
        raise CandidateError("INPUT_FILE_INVALID")
    return data


def read_json(path):
    try:
        return json.loads(read_small(path), object_pairs_hook=unique)
    except (ValueError, TypeError) as error:
        if isinstance(error, CandidateError):
            raise
        raise CandidateError("INPUT_JSON_INVALID") from None


def lane_inputs(artifact, lane, names, commit, run_id, attempt):
    folder = Path(artifact) / "ci" / lane
    receipt_path = folder / "receipt.json"
    receipt = read_json(receipt_path)
    if (not isinstance(receipt, dict) or type(receipt.get("schema")) is not int or receipt.get("schema") != 1
            or receipt.get("lane") != lane or receipt.get("commit") != commit
            or receipt.get("run_id") != run_id or receipt.get("run_attempt") != attempt
            or receipt.get("github_actions") is not True
            or receipt.get("tracked_source_dirty") is not False
            or receipt.get("status") != "components_built_and_selected_tests_passed"
            or type(receipt.get("exit_code")) is not int or receipt["exit_code"] != 0
            or any(receipt.get(name) is not False for name in
                   ("complete_package", "game_executed", "live_acceptance", "deployed", "published"))):
        raise CandidateError("INPUT_RUN_OR_SCOPE_MISMATCH")
    if lane == "linux-lan-baseline" and receipt.get("workspace") != "src/native":
        raise CandidateError("INPUT_WORKSPACE_MISMATCH")
    rows = receipt.get("components")
    if not isinstance(rows, dict) or set(rows) != set(names):
        raise CandidateError("INPUT_COMPONENT_SET_MISMATCH")
    for row in rows.values():
        if (not isinstance(row, dict) or set(row) != {"bytes", "sha256"}
                or type(row["bytes"]) is not int or not 0 < row["bytes"] <= LIMIT
                or not isinstance(row["sha256"], str)
                or re.fullmatch(r"[a-f0-9]{64}", row["sha256"]) is None):
            raise CandidateError("INPUT_COMPONENT_METADATA_INVALID")
    sums = read_small(folder / "components/SHA256SUMS.txt", 8192).decode("ascii").splitlines()
    if sums != [f"{row['sha256']}  {name}" for name, row in sorted(rows.items())]:
        raise CandidateError("INPUT_CHECKSUM_LIST_MISMATCH")
    paths = {name: folder / "components" / name for name in rows}
    for name, path in paths.items():
        if sha_file(path) != rows[name]:
            raise CandidateError("INPUT_COMPONENT_HASH_MISMATCH")
    return paths, rows, sha_file(receipt_path)


def pe_header(path, machine, dll):
    size = regular(path).st_size
    with Path(path).open("rb") as source:
        header = source.read(64)
        if len(header) < 64 or header[:2] != b"MZ":
            raise CandidateError("CLIENT_PE_INVALID")
        offset = struct.unpack_from("<I", header, 60)[0]
        if offset < 64 or offset > min(size, 65536) - 26:
            raise CandidateError("CLIENT_PE_INVALID")
        source.seek(offset)
        pe = source.read(26)
    if (len(pe) != 26 or pe[:4] != b"PE\0\0" or struct.unpack_from("<H", pe, 4)[0] != machine
            or struct.unpack_from("<H", pe, 20)[0] < 2
            or offset + 24 + struct.unpack_from("<H", pe, 20)[0] > min(size, 65536)
            or bool(struct.unpack_from("<H", pe, 22)[0] & 0x2000) != dll
            or struct.unpack_from("<H", pe, 24)[0] != (0x10B if machine == 0x14C else 0x20B)):
        raise CandidateError("CLIENT_PE_INVALID")


def elf_header(path):
    regular(path)
    with Path(path).open("rb") as source:
        header = source.read(64)
    if (len(header) != 64 or header[:7] != b"\x7fELF\x02\x01\x01" or header[7] not in (0, 3)
            or struct.unpack_from("<H", header, 16)[0] not in (2, 3)
            or struct.unpack_from("<H", header, 18)[0] != 62
            or struct.unpack_from("<I", header, 20)[0] != 1
            or struct.unpack_from("<H", header, 52)[0] != 64):
        raise CandidateError("LINUX_ELF_INVALID")


def checked_copy(source, destination, expected):
    destination.parent.mkdir(parents=True, exist_ok=True)
    with Path(source).open("rb") as reader, destination.open("xb") as writer:
        size = 0
        for block in iter(lambda: reader.read(1024 * 1024), b""):
            size += len(block)
            if size > expected["bytes"]:
                raise CandidateError("COPY_HASH_MISMATCH")
            writer.write(block)
    if sha_file(destination) != expected:
        raise CandidateError("COPY_HASH_MISMATCH")


def assemble(windows_artifact, linux_artifact, output, checkout, commit, run_id, attempt):
    if (re.fullmatch(r"[a-f0-9]{40}", commit) is None or re.fullmatch(r"[1-9][0-9]{0,19}", run_id) is None
            or re.fullmatch(r"[1-9][0-9]{0,7}", attempt) is None):
        raise CandidateError("EXPECTED_RUN_INVALID")
    output, checkout = Path(os.path.abspath(output)), Path(os.path.abspath(checkout))
    if output.exists() or output.is_symlink():
        raise CandidateError("OUTPUT_ALREADY_EXISTS")
    for parent in output.parents:
        if parent.exists() or parent.is_symlink():
            info = parent.lstat()
            if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise CandidateError("OUTPUT_PARENT_INVALID")
    windows, win_rows, win_receipt = lane_inputs(windows_artifact, "windows-components", WINDOWS, commit, run_id, attempt)
    linux, linux_rows, linux_receipt = lane_inputs(linux_artifact, "linux-lan-baseline", LINUX, commit, run_id, attempt)
    for name, (_, machine, dll) in WINDOWS.items():
        pe_header(windows[name], machine, dll)
    for path in linux.values():
        elf_header(path)
    release = read_json(checkout / "release-identity.json")
    if (not isinstance(release, dict) or set(release) != {"release_id", "compat_id"}
            or any(not isinstance(value, str) or re.fullmatch(r"[A-Za-z0-9._-]{1,64}", value) is None
                   for value in release.values())):
        raise CandidateError("PREBUILD_IDENTITY_INVALID")
    program = {row[0]: windows[name] for name, row in WINDOWS.items()}
    try:
        client_catalog = build_release_catalog(program, checkout / "release-identity.json")
    except (ValueError, KeyError):
        raise CandidateError("CLIENT_CATALOG_CONSTRUCTION_FAILED") from None
    service = linux_rows["dedicated_server"]
    public_catalog = json_bytes({"schema": 1, "platform": "linux-x86_64",
        "service": {"role": "server", "path": "bin/FifthFreedom.Server", **release, **service},
        "admitted_client": {"root": "client", "catalog": "package/RELEASE-CATALOG.json", **release,
                            "manifest_sha256": digest(client_catalog)}})
    notices = [checkout / "LICENSE", checkout / "THIRD_PARTY_NOTICES.txt",
               *sorted((checkout / "src/launcher/src/ThirdPartyNotices").glob("*.txt"))]
    notice_rows = {path: sha_file(path) for path in notices}
    output.mkdir(parents=True, exist_ok=False)
    for name, (relative, _, _) in WINDOWS.items():
        checked_copy(windows[name], output / "client" / relative, win_rows[name])
    checked_copy(linux["dedicated_server"], output / "bin/FifthFreedom.Server", service)
    checked_copy(linux["verify_public_package"], output / "tools/verify_public_package", linux_rows["verify_public_package"])
    (output / "bin/FifthFreedom.Server").chmod(0o755)
    (output / "tools/verify_public_package").chmod(0o755)
    for name, data in (("client/package/RELEASE-CATALOG.json", client_catalog),
                       ("package/PUBLIC-SERVER-CATALOG.json", public_catalog)):
        path = output / name
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("xb") as handle:
            handle.write(data)
    for path, row in notice_rows.items():
        name = path.name if path.parent == checkout else "ThirdPartyNotices/" + path.name
        checked_copy(path, output / name, row)
    from fusionfix_provenance import copy_notices
    copy_notices(output / "licenses/FusionFix", checkout)
    readme = ("Fifth Freedom component-only public-server integrity candidate.\n"
              "NOT a complete Windows client or installable game package.\n"
              "client/ contains the four unchanged Windows role bytes only as admission evidence.\n"
              "Privacy-held saves remain excluded and the complete-package gate remains separate.\n"
              "The ordinary native Public command remains closed; controlled owner-lease development probes are separate.\n"
              "No deployment or game execution occurred.\n"
              "Artifacts may reset executable permissions; offline verification uses tools/verify_public_package ROOT.\n")
    (output / "README.component-only.txt").write_text(readme, encoding="utf-8")
    files = {path.relative_to(output).as_posix(): sha_file(path) for path in sorted(output.rglob("*")) if path.is_file()}
    receipt = {"schema": 1, "kind": "public-server-component-candidate", "source_commit": commit,
               "run_id": run_id, "run_attempt": attempt, "scope": dict(SCOPE),
               "status": "assembled_pending_native_verification", "files": files,
               "input_receipts": {"windows-components": win_receipt, "linux-lan-baseline": linux_receipt},
               "expected_native_result": {"schema": 1, "result": "verified", "platform": "linux-x86_64",
                   "service": {**release, "manifest_sha256": digest(public_catalog), "executable_sha256": service["sha256"]},
                   "admitted_client": {"storage_key": f"1|{release['release_id']}|{release['compat_id']}|{digest(client_catalog)}"}}}
    (output / "CANDIDATE-RECEIPT.json").write_bytes(json_bytes(receipt))
    return receipt


def verify_native(output, receipt):
    output = Path(output).absolute()
    for name, row in receipt["files"].items():
        if sha_file(output / name) != row:
            raise CandidateError("CANDIDATE_CHANGED_BEFORE_VERIFICATION")
    result = subprocess.run([str(output / "tools/verify_public_package"), str(output)],
                            capture_output=True, timeout=120, cwd=output)
    if result.returncode or len(result.stdout) > 16384:
        raise CandidateError("NATIVE_VERIFICATION_FAILED")
    try:
        actual = json.loads(result.stdout, object_pairs_hook=unique)
    except (TypeError, ValueError):
        raise CandidateError("NATIVE_VERIFICATION_RECEIPT_INVALID") from None
    if actual != receipt["expected_native_result"] or type(actual.get("schema")) is not int:
        raise CandidateError("NATIVE_VERIFICATION_IDENTITY_MISMATCH")
    for name, row in receipt["files"].items():
        if sha_file(output / name) != row:
            raise CandidateError("CANDIDATE_CHANGED_DURING_VERIFICATION")
    receipt["status"] = "native_dual_identity_verified"
    receipt["native_verification"] = actual
    (output / "CANDIDATE-RECEIPT.json").write_bytes(json_bytes(receipt))
    sums = {path.relative_to(output).as_posix(): sha_file(path) for path in sorted(output.rglob("*")) if path.is_file()}
    (output / "SHA256SUMS.txt").write_text(
        "".join(f"{row['sha256']}  {name}\n" for name, row in sorted(sums.items())), encoding="ascii")
    return receipt


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--windows-artifact", type=Path, required=True)
    parser.add_argument("--linux-artifact", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--expected-run-id", required=True)
    parser.add_argument("--expected-run-attempt", required=True)
    args = parser.parse_args(argv)
    try:
        if sys.platform != "linux":
            raise CandidateError("LINUX_VERIFIER_REQUIRED")
        if subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip() != args.expected_commit:
            raise CandidateError("CHECKOUT_REVISION_MISMATCH")
        receipt = assemble(args.windows_artifact, args.linux_artifact, args.output, ROOT,
                           args.expected_commit, args.expected_run_id, args.expected_run_attempt)
        result = verify_native(args.output, receipt)
        print(json.dumps({"result": result["status"], "source_commit": result["source_commit"],
                          "complete_game_package": False, "native_public_profile_enabled": False}))
        return 0
    except (CandidateError, OSError, ValueError, subprocess.SubprocessError) as error:
        print(str(error) if isinstance(error, CandidateError) else "PUBLIC_SERVER_CANDIDATE_FAILED", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
