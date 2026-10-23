#!/usr/bin/env python3
"""Verify pinned patch inputs, replay/reverse them, and run six pure Rust tests.

Copies only the receipt's source files to a new directory. Does not build or
execute EasyTier, load a DLL, create an adapter, or alter the input checkouts.
"""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess


def digest(data):
    return hashlib.sha256(data).hexdigest()


def checked_path(root, relative):
    path = pathlib.PurePosixPath(relative)
    if path.is_absolute() or not path.parts or ".." in path.parts:
        raise ValueError("INVALID_RECEIPT_PATH")
    candidate = root.joinpath(*path.parts)
    if candidate.is_symlink() or not candidate.resolve().is_relative_to(root.resolve()):
        raise ValueError("INVALID_RECEIPT_PATH")
    return candidate


def run(command, directory, log):
    result = subprocess.run(command, cwd=directory, capture_output=True, timeout=60)
    output = result.stdout + result.stderr
    log.write_bytes(output)
    if result.returncode:
        raise ValueError("VERIFICATION_STEP_FAILED")
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tun-source", required=True, type=pathlib.Path)
    parser.add_argument("--easytier-source", required=True, type=pathlib.Path)
    parser.add_argument("--work-dir", required=True, type=pathlib.Path,
                        help="New, nonexistent output directory; preserved after verification.")
    parser.add_argument("--rustc", required=True, type=pathlib.Path,
                        help="Explicit existing rustc executable; no toolchain installation.")
    args = parser.parse_args()
    here = pathlib.Path(__file__).resolve().parent
    receipt = json.loads((here / "receipt.json").read_text())
    # Refuse an existing path rather than clean or reuse somebody else's files.
    args.work_dir.mkdir(parents=False, exist_ok=False)
    work = args.work_dir.resolve()
    sources = {"tun": args.tun_source, "easytier": args.easytier_source}
    results = []
    for group in receipt["groups"]:
        patch = checked_path(here, group["patch"])
        if digest(patch.read_bytes()) != group["patch_sha256"]:
            raise ValueError("PATCH_DIGEST_MISMATCH")
        destination = work / group["name"]
        destination.mkdir()
        for item in group["files"]:
            source = checked_path(sources[group["name"]], item["path"])
            target = checked_path(destination, item["path"])
            if item["original_sha256"] is None:
                if source.exists():
                    raise ValueError("EXPECTED_ABSENT_SOURCE")
                continue
            data = source.read_bytes()
            if digest(data) != item["original_sha256"]:
                raise ValueError("SOURCE_DIGEST_MISMATCH")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        run(["git", "apply", "--check", str(patch)], destination,
            work / (group["name"] + "-apply-check.log"))
        run(["git", "apply", str(patch)], destination,
            work / (group["name"] + "-apply.log"))
        for item in group["files"]:
            if digest(checked_path(destination, item["path"]).read_bytes()) != item["patched_sha256"]:
                raise ValueError("PATCHED_SOURCE_DIGEST_MISMATCH")
        if group["name"] == "tun":
            test_file = destination / "src/session_lifetime.rs"
            run([str(args.rustc.resolve()), "--edition", "2021", "--test", str(test_file),
                 "-o", str(work / "lifetime-tests")], work, work / "compile.log")
            output = run([str(work / "lifetime-tests"), "--test-threads=1"], work,
                         work / "lifetime-tests.log").decode("utf-8", "strict")
            if not re.search(r"test result: ok\. 6 passed; 0 failed; 0 ignored;", output):
                raise ValueError("TEST_COUNT_MISMATCH")
        run(["git", "apply", "--reverse", str(patch)], destination,
            work / (group["name"] + "-reverse.log"))
        for item in group["files"]:
            path = checked_path(destination, item["path"])
            if item["original_sha256"] is None:
                if path.exists():
                    raise ValueError("REVERSE_EXPECTED_ABSENCE")
            elif digest(path.read_bytes()) != item["original_sha256"]:
                raise ValueError("REVERSE_DIGEST_MISMATCH")
        results.append(group["name"])
    print(json.dumps({"patches_applied_and_reversed": results,
                      "lifetime_passed": 6, "lifetime_ignored": 0,
                      "windows_runtime": False, "driver_runtime": False}))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
        # Source locations and arbitrary compiler output stay in the explicitly
        # chosen local work directory, not public diagnostics.
        raise SystemExit("UI09_VERIFICATION_FAILED")
