"""Four-role x86 Windows source regressions using owned synthetic fixtures.

This is separate from unavailable C71 retail replay inputs. It never runs a
retail game, imports private fixtures, weakens a pin or substitutes a compile
failure for a failing executed assertion. Both lanes share one temporary source
checkout and one sequential Cargo target cache.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import struct
import subprocess
import sys
import tarfile
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
BASELINE = "d207fc611be3a89351f8517fcbc37aeb18610dd4"
PREFIX = "src/native/client/hooks/gameplay/src/"
INSTRUMENTATION = {
    PREFIX + "sticky_camera_sound.rs": '\n#[cfg(test)]\n#[path="sticky_camera_lifecycle_tests.rs"]\nmod lifecycle_replay;\n',
    PREFIX + "sticky_camera_runtime.rs": '\n#[cfg(test)]\n#[path="sticky_camera_lifecycle_runtime_fixture.rs"]\npub(crate) mod lifecycle_fixture;\n',
    PREFIX + "progression.rs": '\n#[cfg(test)]\n#[path = "progression_region_tests.rs"]\nmod region_tests;\n',
}
LANES = {
    "sticky-camera": {"selector": "sticky_camera", "modified_passed": 10, "baseline_passed": 9, "baseline_failed": 1,
                      "required_failure": "STICKY_CALLBACK_EXPECTATION_FAILED"},
    "progression": {"selector": "progression::region_tests::", "modified_passed": 2, "baseline_passed": 0, "baseline_failed": 2,
                    "required_failure": "PROGRESSION_VALID_SAME_ALLOCATION_SPAN"},
}
CAMERA_EXPECTED_FAILURES = {
    "cancel_destroy_blocks_new_start": 3, "cancel_replacement_blocks_new_start": 3, "cancel_asset_reuse_blocks_new_start": 3,
    "cancel_world_switch_blocks_new_start": 3, "cancel_shutdown_blocks_new_start": 3,
    "end_failure_does_not_stop_destroyed_or_replaced_actor": 3, "nested_accepted_start_consumes_outer_fallback": 1,
    "outer_failure_keeps_nested_pair": 1, "nested_pair_still_has_paired_end": 1, "runtime_nested_start_never_mixes_original_whistle": 1,
    "runtime_nested_start_remains_owned": 1, "old_failed_end_does_not_stop_nested_pair": 1,
    "cancel_destruction_no_native_whistle_fallback": 2, "runtime_end_nested_start_never_mixes_or_republishes": 1,
    "detonate_result_after_native_lifecycle_callback": 4, "lifecycle_destruction_blocks_native_trigger": 4,
}
CALLBACK_CHECKS = 87
CAMERA_TEST = "sticky_camera_sound::lifecycle_replay::sticky_camera_lifecycle_callbacks"
SUMMARY = re.compile(r"^test result: (ok|FAILED)\. (\d+) passed; (\d+) failed; (\d+) ignored; (\d+) measured; (\d+) filtered out;", re.M)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def git(*args: str) -> bytes:
    return subprocess.check_output(["git", *args], cwd=ROOT)


def inspect_result(lane: str, phase: str, exit_code: int, stdout: str, stderr: str) -> dict:
    """Only completed Rust tests with the exact expected failures count as B/R."""
    specification = LANES[lane]
    baseline = phase in {"baseline", "rollback"}
    summaries = SUMMARY.findall(stdout)
    if not summaries:
        raise ValueError("PRODUCTION_REPLAY_TEST_SUMMARY_MISSING")
    state, passed, failed, ignored, measured, filtered = summaries[-1]
    counts = dict(zip(("passed", "failed", "ignored", "measured", "filtered"), map(int, (passed, failed, ignored, measured, filtered))))
    expected = (specification["baseline_passed"], specification["baseline_failed"]) if baseline else (specification["modified_passed"], 0)
    if ((counts["passed"], counts["failed"]) != expected or counts["ignored"] != 0 or counts["measured"] != 0
            or exit_code != (101 if baseline else 0) or state != ("FAILED" if baseline else "ok")):
        raise ValueError("PRODUCTION_REPLAY_TEST_RESULT_UNEXPECTED")
    if baseline and specification["required_failure"] not in stdout + stderr:
        raise ValueError("PRODUCTION_REPLAY_EXPECTED_ASSERTION_MISSING")
    if lane == "sticky-camera":
        expected_child = ("FAILED", "0", "1", "0", "0") if baseline else ("ok", "1", "0", "0", "0")
        if len(summaries) != 2 or summaries[0][:5] != expected_child:
            raise ValueError("PRODUCTION_REPLAY_CHILD_SUMMARIES_INVALID")
        failed_names = re.findall(r"^    (sticky_camera[^\r\n]+)$", stdout, re.M)
        if failed_names != ([CAMERA_TEST, CAMERA_TEST] if baseline else []):
            raise ValueError("PRODUCTION_REPLAY_FAILED_TEST_NAME_INVALID")
        callback = re.findall(r"^STICKY_CALLBACK_RESULT checks=(\d+) failures=(\d+) native_audio=synthetic game=false$", stdout, re.M)
        errors = re.findall(r"^STICKY_CALLBACK_FAIL ([a-z_]+)$", stdout, re.M)
        if len(callback) != 1 or int(callback[0][0]) != CALLBACK_CHECKS or int(callback[0][1]) != len(errors):
            raise ValueError("PRODUCTION_REPLAY_CALLBACK_RESULT_INVALID")
        if baseline:
            if Counter(errors) != Counter(CAMERA_EXPECTED_FAILURES):
                raise ValueError("PRODUCTION_REPLAY_UNEXPECTED_CALLBACK_FAILURE")
        elif errors:
            raise ValueError("PRODUCTION_REPLAY_CALLBACK_FAILURE")
        counts.update(callback_checks=int(callback[0][0]), callback_failures=len(errors), callback_failure_names=sorted(set(errors)))
    elif len(summaries) != 1:
        raise ValueError("PRODUCTION_REPLAY_SUMMARIES_INVALID")
    elif baseline:
        expected_names = (
            "progression::region_tests::progression_region_single_owned_allocation_accepts_all_writable_regions",
            "progression::region_tests::progression_region_invalid_tail_or_unrelated_allocation_preserves_original",
        )
        if any(not re.search(r"^test " + re.escape(name) + r" \.\.\. FAILED$", stdout, re.M) for name in expected_names):
            raise ValueError("PRODUCTION_REPLAY_PROGRESSION_FAILURE_NAMES_INVALID")
        if "PROGRESSION_INVALID_REPLACEMENT variant=5" not in stdout + stderr:
            raise ValueError("PRODUCTION_REPLAY_CAPACITY_NEGATIVE_CONTROL_MISSING")
    return counts


def select_executable(stdout: str, target: Path) -> Path:
    try:
        rows = [json.loads(line) for line in stdout.splitlines() if line.strip()]
        if any(type(row) is not dict for row in rows):
            raise ValueError()
        completed = [r for r in rows if r.get("reason") == "build-finished"]
        artifacts = [r for r in rows if r.get("reason") == "compiler-artifact" and r.get("target", {}).get("name") == "scbl_gameplay"
                     and r.get("profile", {}).get("test") is True and r.get("executable") is not None]
        if len(completed) != 1 or completed[0].get("success") is not True or len(artifacts) != 1:
            raise ValueError()
        path = Path(artifacts[0]["executable"])
        if (not path.is_absolute() or not re.fullmatch(r"scbl_gameplay-[a-f0-9]+\.exe", path.name)
                or not path.resolve().is_relative_to(target.resolve()) or path.is_symlink() or not path.is_file()):
            raise ValueError()
        binary = path.read_bytes()
        offset = struct.unpack_from("<I", binary, 0x3C)[0]
        if binary[:2] != b"MZ" or binary[offset:offset+4] != b"PE\0\0" or struct.unpack_from("<H", binary, offset+4)[0] != 0x14C:
            raise ValueError()
        return path
    except (ValueError, OSError, TypeError, KeyError, AttributeError, struct.error):
        raise ValueError("PRODUCTION_REPLAY_CARGO_ARTIFACT_INVALID") from None


def run_command(command: list[str], cwd: Path, env: dict, output: Path, label: str, timeout: float) -> dict:
    out = output / (label + ".stdout.txt")
    err = output / (label + ".stderr.txt")
    started = time.monotonic()
    with out.open("wb") as stdout, err.open("wb") as stderr:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stdout, stderr=stderr)
        try:
            while True:
                try:
                    process.wait(timeout=15)
                    break
                except subprocess.TimeoutExpired:
                    elapsed = time.monotonic() - started
                    print(f"WORKING native-source-replay {label} {int(elapsed)}s", flush=True)
                    if elapsed >= timeout:
                        raise TimeoutError("PRODUCTION_REPLAY_COMMAND_TIMEOUT")
        except BaseException:
            # On Windows the test has a child solely for owned-memory isolation.
            # Bound the whole task tree if that synthetic child hangs.
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            process.wait(timeout=15)
            raise
    row = {"label": label, "exit_code": process.returncode, "elapsed_seconds": round(time.monotonic() - started, 3),
           "stdout": out.name, "stderr": err.name, "stdout_sha256": sha(out.read_bytes()), "stderr_sha256": sha(err.read_bytes())}
    write(output / (label + ".json"), row)
    return row


def extract_checkout(archive: bytes, destination: Path) -> None:
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as data:
        for member in data.getmembers():
            # Git/tar member names use POSIX separators on every host. On
            # Windows, Path('/absolute').is_absolute() is false because it has
            # no drive; the tar filter may then strip its leading slash. Reject
            # it before extraction rather than accepting that normalization.
            path = PurePosixPath(member.name)
            windows = PureWindowsPath(member.name)
            if (path.is_absolute() or windows.drive or windows.root or "\\" in member.name
                    or ".." in path.parts or any(":" in part for part in path.parts)
                    or member.issym() or member.islnk() or not (member.isfile() or member.isdir())):
                raise ValueError("PRODUCTION_REPLAY_ARCHIVE_MEMBER_INVALID")
        data.extractall(destination, filter="data")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/ci/native-production-replay")
    options = parser.parse_args()
    output = options.output.resolve()
    if os.name != "nt":
        raise RuntimeError("PRODUCTION_REPLAY_REQUIRES_WINDOWS_X86_BUILD_TOOLS")
    if output.exists():
        raise RuntimeError("PRODUCTION_REPLAY_OUTPUT_ALREADY_EXISTS")
    output.mkdir(parents=True)
    head = git("rev-parse", "HEAD").decode("ascii").strip()
    if not re.fullmatch(r"[a-f0-9]{40}", head):
        raise RuntimeError("PRODUCTION_REPLAY_SOURCE_INVALID")
    receipt = {"schema": 1, "source_commit": head, "baseline_commit": BASELINE, "status": "started",
               "scope": "selected real production source, synthetic native leaves and owned Windows memory; not retail instruction/game replay",
               "game_launched": False, "private_inputs": False, "c71_inputs_replaced": False, "roles": []}
    write(output / "receipt.json", receipt)
    try:
        # The official workflow must fetch this public, pinned commit first.
        # Missing source is a prerequisite failure, never a baseline regression.
        baseline = {name: git("show", BASELINE + ":" + name) for name in INSTRUMENTATION}
        modified = {name: git("show", head + ":" + name) for name in INSTRUMENTATION}
        for name in INSTRUMENTATION:
            if (ROOT / name).read_bytes() != modified[name]:
                # Checkout line ending conversion is allowed only when it
                # changes CRLF to LF; no arbitrary production edits are hidden.
                if (ROOT / name).read_bytes().replace(b"\r\n", b"\n") != modified[name].replace(b"\r\n", b"\n"):
                    raise RuntimeError("PRODUCTION_REPLAY_TRACKED_SOURCE_CHANGED")
        receipt["source_files"] = {name: {"baseline_sha256": sha(baseline[name]), "modified_sha256": sha(modified[name]),
            "baseline_test_only_append": append, "instrumented_baseline_sha256": sha(baseline[name] + append.encode("utf-8"))}
            for name, append in INSTRUMENTATION.items()}
        fixture_names = [PREFIX + name for name in ("sticky_camera_lifecycle_tests.rs", "sticky_camera_lifecycle_runtime_fixture.rs", "progression_region_tests.rs")]
        receipt["fixed_test_sources"] = {name: sha(git("show", head + ":" + name)) for name in fixture_names}
        import build
        cargo, env = build.native_env()
        env.pop("FF_STICKY_CAMERA_FIXTURE_CHILD", None)
        target = Path(env["CARGO_TARGET_DIR"]).resolve()
        with tempfile.TemporaryDirectory(prefix="ff-production-source-replay-") as directory:
            checkout = Path(directory)
            extract_checkout(git("archive", "--format=tar", head), checkout)
            receipt["unchanged_dependency_tree"] = git("rev-parse", head + "^{tree}").decode("ascii").strip()
            for phase in ("baseline", "modified", "rollback", "reapplied"):
                old = phase in {"baseline", "rollback"}
                role = {"name": phase, "production_files": {}, "lanes": {}}
                receipt["roles"].append(role)
                for name, append in INSTRUMENTATION.items():
                    data = baseline[name] + append.encode("utf-8") if old else modified[name]
                    (checkout / name).write_bytes(data)
                    role["production_files"][name] = sha(data)
                command = [str(cargo), "test", "--locked", "-p", "scbl-gameplay", "--lib", "--no-run", "--message-format=json"]
                built = run_command(command, checkout / "src/native", env, output, phase + "-compile", 1800)
                role["compile"] = built
                write(output / "receipt.json", receipt)
                if built["exit_code"] != 0:
                    raise RuntimeError("PRODUCTION_REPLAY_COMPILE_FAILED_NOT_BASELINE_EVIDENCE")
                exe = select_executable((output / built["stdout"]).read_text(encoding="utf-8"), target)
                role["executable"] = {"file": exe.name, "sha256": sha(exe.read_bytes()), "machine": "i386", "bytes": exe.stat().st_size}
                for lane, specification in LANES.items():
                    args = [str(exe), specification["selector"], "--nocapture", "--test-threads=1"]
                    result = run_command(args, checkout / "src/native", env, output, phase + "-" + lane, 90)
                    role["lanes"][lane] = result
                    write(output / "receipt.json", receipt)
                    result["checks"] = inspect_result(lane, phase, result["exit_code"],
                        (output / result["stdout"]).read_text(encoding="utf-8"), (output / result["stderr"]).read_text(encoding="utf-8"))
                    write(output / "receipt.json", receipt)
                    print(f"PASS native-source-replay {phase} {lane} {result['checks']}", flush=True)
            if (receipt["roles"][0]["production_files"] != receipt["roles"][2]["production_files"]
                    or receipt["roles"][1]["production_files"] != receipt["roles"][3]["production_files"]):
                raise RuntimeError("PRODUCTION_REPLAY_ROLE_SOURCE_MISMATCH")
        receipt["status"] = "passed"
        write(output / "receipt.json", receipt)
        return 0
    except BaseException as error:
        receipt["status"] = "failed"
        receipt["error_type"] = type(error).__name__
        receipt["failure"] = str(error) if re.fullmatch(r"PRODUCTION_REPLAY_[A-Z0-9_]+", str(error)) else "PRODUCTION_REPLAY_PREREQUISITE_OR_EXECUTION_FAILED"
        write(output / "receipt.json", receipt)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
