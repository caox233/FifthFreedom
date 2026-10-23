"""Run finite Windows owned-read contracts with exact same-invocation helpers."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

import ci_diagnostics

ROOT = Path(__file__).resolve().parents[1]
TARGET = "x86_64-pc-windows-msvc"
BINARY = "ff-windows-owned-read-contract"
NORMAL_SUMMARY = "RESULT 19 passed; 0 skipped"
INTEROP_SUMMARY = "OWNED_CORE_READ_INTEROP_OK 4"
NATIVE_CASES = (
    "explicit_descriptor_identity_and_bounds", "duplicate_fields_are_not_last_wins",
    "source_requires_canonical_unicast_ipv4", "request_binds_attempt_instance_fresh_nonce_and_sequence",
    "hello_and_observation_exact_wire", "real_duplex_frame_roundtrip",
    "oversized_truncated_unknown_and_invalid_utf8_frames_reject",
    "cancellation_drops_real_pending_read_without_detached_worker",
)
_RUST_SUMMARY = re.compile(r"test result: ok\. 8 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in [0-9.]+s")
_FAILURE_CODES = frozenset(("OWNED_READ_CARGO_ARTIFACT_INVALID", "OWNED_READ_IMAGE_INVALID",
    "OWNED_READ_PROCESS_FAILED", "OWNED_READ_NATIVE_RESULT_INVALID", "OWNED_READ_MANAGED_RESULT_INVALID",
    "OWNED_READ_IMAGE_CHANGED"))


def managed_output(text: str, *, interop=False) -> bool:
    lines = text.splitlines()
    summary, count = (INTEROP_SUMMARY, 4) if interop else (NORMAL_SUMMARY, 19)
    return (sum(line.startswith("PASS ") for line in lines) == count
            and [line for line in lines if line.startswith(("RESULT ", "OWNED_CORE_READ_INTEROP_OK"))] == [summary]
            and not any(line.startswith(("FAIL", "SKIP", "CI_MANAGED_FAILURE", "CI_OWNED_CORE_FAILURE")) for line in lines))


def native_output(text: str) -> bool:
    lines = text.splitlines()
    summaries = [line for line in lines if line.startswith("test result:")]
    return (len(summaries) == 1 and _RUST_SUMMARY.fullmatch(summaries[0]) is not None
            and sorted(line for line in lines if line.startswith("test tests::"))
            == sorted("test tests::" + name + " ... ok" for name in NATIVE_CASES))


def native_failed_cases(text: str) -> list[str]:
    lines = set(text.splitlines())
    return sorted(name for name in NATIVE_CASES if "test tests::" + name + " ... FAILED" in lines)


def native_executable(text: str, target: Path, manifest: Path) -> Path:
    """Bind to Cargo JSON from the successful build, never a glob/newest file."""
    def unique(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError()
            result[key] = value
        return result
    try:
        if len(text.encode("utf-8")) > 2 * 1024 * 1024:
            raise ValueError()
        rows = [json.loads(line, object_pairs_hook=unique) for line in text.splitlines() if line.strip()]
        if any(type(row) is not dict for row in rows):
            raise ValueError()
        finished = [row for row in rows if row.get("reason") == "build-finished"]
        selected = [row for row in rows if row.get("reason") == "compiler-artifact"
                    and row.get("target", {}).get("name") == BINARY
                    and row.get("target", {}).get("kind") == ["bin"]
                    and row.get("profile", {}).get("test") is False
                    and row.get("executable") is not None]
        if len(finished) != 1 or finished[0].get("success") is not True or len(selected) != 1:
            raise ValueError()
        row = selected[0]
        if Path(row["manifest_path"]).resolve() != manifest.resolve():
            raise ValueError()
        path = Path(row["executable"])
        expected = target.resolve() / TARGET / "debug" / (BINARY + ".exe")
        if not path.is_absolute() or path.is_symlink() or not path.is_file() or path.resolve() != expected:
            raise ValueError()
        return path.resolve()
    except (OSError, ValueError, TypeError, KeyError, AttributeError):
        raise ValueError("OWNED_READ_CARGO_ARTIFACT_INVALID") from None


def image_identity(path: Path) -> dict:
    if path.is_symlink() or not path.is_file():
        raise OSError("OWNED_READ_IMAGE_INVALID")
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            size += len(chunk)
            if size > 128 * 1024 * 1024:
                raise OSError("OWNED_READ_IMAGE_INVALID")
            digest.update(chunk)
    if size == 0:
        raise OSError("OWNED_READ_IMAGE_INVALID")
    return {"bytes": size, "sha256": digest.hexdigest()}


def run_contracts(root: Path, temporary: Path, result: dict) -> None:
    image = temporary / "ff-owned-read-tests/OwnedCoreRead.exe"
    target = temporary / "ff-owned-read-native"
    manifest = root / "tests/native/windows-owned-read/Cargo.toml"
    result["stage"] = "INPUT"
    original = image_identity(image)
    result["apphost_identity"] = original
    target.mkdir()  # Existing build evidence is never silently reused/overwritten.
    cargo = ["cargo", "+1.95.0"]
    common = ["--locked", "--manifest-path", str(manifest), "--target", TARGET, "--target-dir", str(target)]

    def invoke(command, stage, timeout):
        result["stage"] = stage
        result["diagnostics"] = {}
        process = subprocess.run(command, cwd=root, capture_output=True, text=True,
                                 encoding="utf-8", errors="replace", timeout=timeout)
        result["diagnostics"] = ci_diagnostics.diagnostic_details((process.stdout + "\n" + process.stderr)[-ci_diagnostics.MAX_LOG_BYTES:])
        if stage == "NATIVE_TEST":
            result["native_failed_cases"] = native_failed_cases(process.stdout)
        if process.returncode:
            raise ValueError("OWNED_READ_PROCESS_FAILED")
        return process

    output = invoke(cargo + ["test", *common, "--lib", "--", "--test-threads=1"], "NATIVE_TEST", 600)
    if not native_output(output.stdout + "\n" + output.stderr):
        raise ValueError("OWNED_READ_NATIVE_RESULT_INVALID")
    result["native_passed_checks"] = 8
    output = invoke(cargo + ["build", *common, "--bin", BINARY, "--message-format=json"], "NATIVE_BUILD", 600)
    helper = native_executable(output.stdout, target, manifest)
    helper_original = image_identity(helper)
    result["helper_identity"] = helper_original
    result["same_invocation_cargo_helper"] = True
    for stage, command, interop in (("MANAGED", [str(image)], False),
                                    ("INTEROP", [str(image), "--native-helper", str(helper)], True)):
        output = invoke(command, stage, 240)
        if not managed_output(output.stdout + "\n" + output.stderr, interop=interop):
            raise ValueError("OWNED_READ_MANAGED_RESULT_INVALID")
        # Check both actual images after each execution, including missing files.
        if image_identity(image) != original or image_identity(helper) != helper_original:
            raise ValueError("OWNED_READ_IMAGE_CHANGED")
        result["interop_passed_checks" if interop else "managed_passed_checks"] = 4 if interop else 19
    result.update(stage="COMPLETE", passed=True, skipped_checks=0, images_unchanged=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != "win32" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        ci_diagnostics.emit_error("OWNED_READ_WINDOWS_HOSTED_REQUIRED")
        return 1
    result = {"schema": 1, "suite": "OwnedCoreRead", "passed": False, "stage": "INPUT",
              "required_managed_checks": 19, "required_native_checks": 8, "required_interop_checks": 4,
              "managed_passed_checks": 0, "native_passed_checks": 0, "interop_passed_checks": 0,
              "required_managed_runtime": "win-x86", "required_native_target": TARGET,
              "self_contained_test_apphost": True, "skipped_checks": None,
              "images_unchanged": False, "same_invocation_cargo_helper": False,
              "easytier_executed": False, "driver_exercised": False, "game_executed": False,
              "production_admission": False, "runtime_binary_uploaded": False, "raw_output_retained": False}
    try:
        temporary = os.environ.get("RUNNER_TEMP")
        if not temporary:
            raise OSError()
        run_contracts(ROOT, Path(temporary), result)
    except subprocess.TimeoutExpired:
        result["error"] = "OWNED_READ_STAGE_TIMEOUT"
    except (OSError, ValueError) as error:
        result["error"] = str(error) if str(error) in _FAILURE_CODES else "OWNED_READ_STAGE_FAILED"
    if not result["passed"]:
        fields = [key + "=" + " | ".join(values) for key, values in result.get("diagnostics", {}).items() if values]
        if result.get("native_failed_cases"):
            fields.append("native_cases=" + " | ".join(result["native_failed_cases"]))
        ci_diagnostics.emit_error("suite=OwnedCoreRead; stage=" + result["stage"] + "; code="
                                  + result.get("error", "OWNED_READ_RESULT_INVALID")
                                  + ("; " + "; ".join(fields) if fields else ""))
    try:
        with args.report.open("x", encoding="utf-8") as output:
            json.dump(result, output, indent=2)
            output.write("\n")
    except OSError:
        ci_diagnostics.emit_error("OWNED_READ_REPORT_FAILED")
        return 1
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
