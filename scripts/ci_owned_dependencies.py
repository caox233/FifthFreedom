"""Run x86 file-lifetime contracts using pinned official DLL bytes, never load them."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import urllib.request
import zipfile

import ci_diagnostics

ROOT = Path(__file__).resolve().parents[1]
URL = "https://www.wintun.net/builds/wintun-0.14.1.zip"
ARCHIVE_BYTES = 750540
ARCHIVE_SHA = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"
MEMBER = "wintun/bin/amd64/wintun.dll"
MEMBER_BYTES = 427552
MEMBER_SHA = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce"
SUMMARY = "RESULT 13 passed; 0 skipped"
CASES = (
    "production catalog matches preserved official origin",
    "dependency origin accepts no caller digest and has no independent disposal",
    "relative and traversal runtime directories reject",
    "dependency must be exactly adjacent to executable",
    "absent dependency preserves ordinary input declaration",
    "real official bytes stay locked from suspended creation through drain",
    "failed DLL validation creates no child and releases prior image config pins",
    "same-sized changed official bytes reject without allowing launch",
    "missing dependency fails before child creation",
    "reparse DLL input rejects even with correct target bytes",
    "parent image exit cannot release dependency while owned descendant remains",
    "cancelled resume drains suspended child before dependency release",
    "explicit no-dependency branch does not silently pin ambient DLL",
)
CODES = frozenset(("DEPENDENCY_ARCHIVE_INVALID", "DEPENDENCY_MEMBER_INVALID",
    "DEPENDENCY_ASSET_CHANGED", "DEPENDENCY_IMAGE_INVALID", "DEPENDENCY_IMAGE_CHANGED",
    "DEPENDENCY_TEST_INPUT_EXISTS", "DEPENDENCY_TEST_INPUT_RETAINED",
    "DEPENDENCY_RESULT_INVALID", "DEPENDENCY_HOST_REQUIRED", "DEPENDENCY_TEMP_REQUIRED"))


def require(value, code):
    if not value:
        raise ValueError(code)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def official_member(data):
    require(len(data) == ARCHIVE_BYTES and sha(data) == ARCHIVE_SHA, "DEPENDENCY_ARCHIVE_INVALID")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        require(archive.namelist().count(MEMBER) == 1, "DEPENDENCY_MEMBER_INVALID")
        row = archive.getinfo(MEMBER)
        require(row.file_size == MEMBER_BYTES and not row.is_dir(), "DEPENDENCY_MEMBER_INVALID")
        member = archive.read(row)
    require(len(member) == MEMBER_BYTES and sha(member) == MEMBER_SHA, "DEPENDENCY_MEMBER_INVALID")
    return member


def image_identity(path):
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and not path.is_symlink(), "DEPENDENCY_IMAGE_INVALID")
    data = path.read_bytes()
    after = path.lstat()
    fields = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
    require(all(getattr(before, key) == getattr(after, key) for key in fields)
            and len(data) == after.st_size and len(data) > 0, "DEPENDENCY_IMAGE_INVALID")
    return {"bytes": len(data), "sha256": sha(data)}


def verified_output(text):
    # All output is source-defined for this executable; unexpected lines fail.
    return text.splitlines() == [*("PASS " + case for case in CASES), SUMMARY]


def run(report):
    require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted", "DEPENDENCY_HOST_REQUIRED")
    require(bool(os.environ.get("RUNNER_TEMP")), "DEPENDENCY_TEMP_REQUIRED")
    root = Path(os.environ["RUNNER_TEMP"])
    image = root / "ff-owned-dependency-tests/OwnedCoreDependencies.exe"
    adjacent = image.parent / "wintun.dll"
    before = image_identity(image)
    require(not os.path.lexists(adjacent), "DEPENDENCY_TEST_INPUT_EXISTS")
    assets = root / "ff-owned-dependency-assets"
    assets.mkdir(exist_ok=False)
    with urllib.request.urlopen(URL, timeout=60) as response:
        archive = response.read(ARCHIVE_BYTES + 1)
    member = official_member(archive)
    archive_path, member_path = assets / "wintun-0.14.1.zip", assets / "wintun.dll"
    with archive_path.open("xb") as output:
        output.write(archive)
    with member_path.open("xb") as output:
        output.write(member)
    report.update(official_archive={"bytes": ARCHIVE_BYTES, "sha256": ARCHIVE_SHA},
        official_member={"member": MEMBER, "bytes": MEMBER_BYTES, "sha256": MEMBER_SHA},
        test_apphost_before=before)
    child = subprocess.run([str(image), "--wintun-input", str(member_path)], cwd=ROOT,
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=240)
    output = child.stdout + child.stderr
    report["diagnostics"] = ci_diagnostics.diagnostic_details(output[-ci_diagnostics.MAX_LOG_BYTES:])
    report["process_succeeded"] = child.returncode == 0
    after = image_identity(image)
    report["test_apphost_after"] = after
    report["test_apphost_unchanged"] = before == after
    report["official_inputs_unchanged"] = (sha(archive_path.read_bytes()) == ARCHIVE_SHA
        and sha(member_path.read_bytes()) == MEMBER_SHA)
    report["test_adjacent_input_removed"] = not os.path.lexists(adjacent)
    require(report["test_apphost_unchanged"], "DEPENDENCY_IMAGE_CHANGED")
    require(report["official_inputs_unchanged"], "DEPENDENCY_ASSET_CHANGED")
    require(report["test_adjacent_input_removed"], "DEPENDENCY_TEST_INPUT_RETAINED")
    require(child.returncode == 0 and verified_output(output), "DEPENDENCY_RESULT_INVALID")
    report.update(passed=True, passed_checks=13, skipped_checks=0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = dict(schema=1, suite="OwnedCoreDependencies", passed=False,
        required_checks=13, passed_checks=None, skipped_checks=None,
        required_runtime="win-x86", self_contained_test_apphost=True,
        dll_loaded=False, driver_exercised=False, easytier_executed=False,
        game_executed=False, production_admission=False, raw_output_retained=False)
    try:
        run(report)
    except Exception as error:
        code = str(error) if type(error) is ValueError and str(error) in CODES else (
            "DEPENDENCY_PROCESS_TIMEOUT" if isinstance(error, subprocess.TimeoutExpired)
            else "DEPENDENCY_CHECK_FAILED")
        report["error"] = code
        details = report.get("diagnostics", {})
        fields = [key + "=" + " | ".join(value) for key, value in details.items() if value]
        ci_diagnostics.emit_error("suite=OwnedCoreDependencies; code=" + code
            + ("; " + "; ".join(fields) if fields else ""))
    try:
        with args.report.open("x", encoding="utf-8") as output:
            json.dump(report, output, indent=2)
            output.write("\n")
    except OSError:
        ci_diagnostics.emit_error("OWNED_DEPENDENCY_REPORT_FAILED")
        return 1
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
