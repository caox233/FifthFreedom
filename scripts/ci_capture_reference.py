"""Build the pinned historical x86 helper; run only no-attach and pure fixtures.

This independent reference build never installs a helper or changes the retained
asset. Successful checks do not establish binary reproducibility, debugger
lifecycle parity, privacy of actual dumps, or complete-package readiness.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

import capture_helper_receipt as identity

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path("docs/development-reference/capture-helper/capture-helper.cpp")
FIXTURE = Path("tests/native/capture-reference/fixture.cpp")
CASES = (
    "access violation with two fields", "in-page error with two fields",
    "short in-page record rejected", "short access record rejected",
    "empty access record rejected", "access record permits extra field",
    "thread-name record has no access fields", "breakpoint has no access fields",
    "plain module basename", "module paths removed", "JSON delimiter rejected",
    "non-ASCII module name rejected", "empty module name rejected",
    "oversize module name rejected", "bounded module RVA", "module end excluded",
    "address before module excluded",
)
ERRORS = frozenset((
    "CAPTURE_REFERENCE_HOST_REQUIRED", "CAPTURE_REFERENCE_TEMP_REQUIRED",
    "CAPTURE_REFERENCE_INPUT_INVALID", "CAPTURE_REFERENCE_COMPILER_REQUIRED",
    "CAPTURE_REFERENCE_COMPILE_FAILED", "CAPTURE_REFERENCE_PE_INVALID",
    "CAPTURE_REFERENCE_CLI_INVALID", "CAPTURE_REFERENCE_FIXTURE_INVALID",
    "CAPTURE_REFERENCE_INPUT_CHANGED", "CAPTURE_REFERENCE_IMAGE_CHANGED",
))


def require(condition, code):
    if not condition:
        raise ValueError(code)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify_inputs(root=ROOT):
    receipt, code = identity.inspect(root=root, source=root / SOURCE)
    require(code == 0, "CAPTURE_REFERENCE_INPUT_INVALID")
    return receipt


def image(path):
    data = identity.read_regular(path, 16 * 1024 * 1024)
    require(identity.is_x86_executable(data), "CAPTURE_REFERENCE_PE_INVALID")
    return {"bytes": len(data), "sha256": sha(data), "x86_pe32_executable": True}


def fixture_result(output):
    lines = output.splitlines()
    expected = ["PASS " + case for case in CASES]
    require(len(lines) == len(expected) + 2 and lines[:len(expected)] == expected
            and lines[-1] == "RESULT 17 passed; 0 skipped",
            "CAPTURE_REFERENCE_FIXTURE_INVALID")
    match = re.fullmatch(r"MSVC_FULL_VER ([0-9]{8,10})", lines[-2])
    require(match is not None, "CAPTURE_REFERENCE_FIXTURE_INVALID")
    return int(match.group(1))


def compile_one(compiler, source, destination, work, report, label):
    # Match the recorded C++17/static CRT/debug recipe, with new independent
    # output paths. The runner's actual MSVC version is recorded by the fixture.
    command = [compiler, "/nologo", "/EHsc", "/std:c++17", "/MT", "/Od", "/Oy-", "/Zi",
               "/I" + str(work), str(source), "/Fe:" + str(destination),
               "/Fo:" + str(work / (label + ".obj")),
               "/Fd:" + str(work / (label + ".compile.pdb")), "/link", "/machine:x86",
               "/INCREMENTAL:NO", "/DEBUG", "/PDB:" + str(work / (label + ".pdb"))]
    result = subprocess.run(command, cwd=work, capture_output=True, timeout=120)
    raw = result.stdout + result.stderr
    (work / (label + ".compile.log")).write_bytes(raw)
    report.setdefault("compilations", {})[label] = {
        "exit_code": result.returncode, "log_sha256": sha(raw),
        "diagnostic_codes": sorted(set(re.findall(r"\b(?:C[0-9]{4}|LNK[0-9]{4})\b",
                                                   raw.decode("utf-8", errors="replace")))),
    }
    require(result.returncode == 0, "CAPTURE_REFERENCE_COMPILE_FAILED")


def run(report, output):
    require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted",
            "CAPTURE_REFERENCE_HOST_REQUIRED")
    require(bool(os.environ.get("RUNNER_TEMP")), "CAPTURE_REFERENCE_TEMP_REQUIRED")
    report["input_receipt"] = verify_inputs()
    revision = subprocess.run(["git", "rev-parse", "--verify", "HEAD"], cwd=ROOT,
                              capture_output=True, text=True, timeout=10)
    require(revision.returncode == 0 and re.fullmatch(r"[0-9a-f]{40}", revision.stdout.strip()) is not None,
            "CAPTURE_REFERENCE_INPUT_INVALID")
    report["checkout_commit"] = revision.stdout.strip()
    fixture_before = sha(identity.read_regular(ROOT / FIXTURE, 1024 * 1024))
    report["fixture_source_sha256"] = fixture_before
    compiler = shutil.which("cl.exe")
    require(bool(compiler), "CAPTURE_REFERENCE_COMPILER_REQUIRED")
    work = Path(os.environ["RUNNER_TEMP"]) / "ff-capture-reference"
    work.mkdir(exist_ok=False)
    source = work / "capture-helper.cpp"
    source.write_bytes(identity.read_regular(ROOT / SOURCE, identity.MAX_SOURCE_BYTES))
    require(sha(source.read_bytes()) == identity.REPORTED_SOURCE_SHA256,
            "CAPTURE_REFERENCE_INPUT_CHANGED")
    helper, fixture = work / "CaptureReference.exe", work / "CaptureReferenceFixtures.exe"
    compile_one(compiler, source, helper, work, report, "reference")
    compile_one(compiler, ROOT / FIXTURE, fixture, work, report, "fixture")
    report["rebuilt_reference"] = image(helper)
    report["fixture_image"] = image(fixture)
    report["rebuild_matches_historical_binary_bytes"] = (
        report["rebuilt_reference"]["sha256"] == identity.BINARY_SHA256)
    # Both command lines return before OpenProcess, opening a pipe, attaching a
    # debugger, or writing state/dump files. No valid capture request is supplied.
    for name, arguments in (("missing_arguments", []),
                            ("invalid_pipe_prefix", ["INVALID-PIPE", "0", "0"])):
        result = subprocess.run([str(helper), *arguments], cwd=work,
                                capture_output=True, timeout=10)
        require(result.returncode == 2 and not result.stdout and not result.stderr,
                "CAPTURE_REFERENCE_CLI_INVALID")
        report.setdefault("no_attach_cli", {})[name] = {"exit_code": result.returncode}
    result = subprocess.run([str(fixture)], cwd=work, capture_output=True, timeout=10)
    require(result.returncode == 0 and not result.stderr, "CAPTURE_REFERENCE_FIXTURE_INVALID")
    report["msvc_full_version"] = fixture_result(result.stdout.decode("ascii", errors="strict"))
    report["historical_msvc_full_version_matched"] = report["msvc_full_version"] == 194435207
    # Matching this version alone would not prove matching SDK or reproducibility.
    report["pure_fixture_checks"] = len(CASES)
    report["retained_inputs_verified_after"] = verify_inputs()["status"] == "reported_source_identity_verified"
    require(sha(source.read_bytes()) == identity.REPORTED_SOURCE_SHA256,
            "CAPTURE_REFERENCE_INPUT_CHANGED")
    require(sha(identity.read_regular(ROOT / FIXTURE, 1024 * 1024)) == fixture_before,
            "CAPTURE_REFERENCE_INPUT_CHANGED")
    require(image(helper) == report["rebuilt_reference"] and image(fixture) == report["fixture_image"],
            "CAPTURE_REFERENCE_IMAGE_CHANGED")
    target = output / "CaptureReference.exe"
    with target.open("xb") as stream:
        stream.write(identity.read_regular(helper, 16 * 1024 * 1024))
    require(image(target) == report["rebuilt_reference"], "CAPTURE_REFERENCE_IMAGE_CHANGED")
    report.update(passed=True, passed_checks=len(CASES) + 2, skipped_checks=0,
                  reference_source_unchanged=True, retained_binary_unchanged=True)


def main():
    output = ROOT / "artifacts/ci/capture-reference"
    try:
        output.mkdir(parents=True, exist_ok=False)
    except OSError:
        print("CAPTURE_REFERENCE_OUTPUT_NOT_FRESH", file=sys.stderr)
        return 1
    report = dict(schema=1, suite="CaptureReferenceBuild", passed=False,
                  required_checks=19, passed_checks=None, skipped_checks=None,
                  source_sha256=identity.REPORTED_SOURCE_SHA256,
                  retained_binary_sha256=identity.BINARY_SHA256,
                  production_helper_replaced=False, capture_executed=False,
                  debugger_attached=False, game_executed=False,
                  source_binary_equivalence_verified=False,
                  complete_package_ready=False, raw_logs_published=False)
    try:
        run(report, output)
    except Exception as error:
        report["error"] = (str(error) if type(error) is ValueError and str(error) in ERRORS
                           else "CAPTURE_REFERENCE_TIMEOUT" if isinstance(error, subprocess.TimeoutExpired)
                           else "CAPTURE_REFERENCE_CHECK_FAILED")
        print(report["error"], file=sys.stderr)
    report_path = output / "receipt.json"
    with report_path.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    names = ["receipt.json"] + (["CaptureReference.exe"] if report["passed"] else [])
    with (output / "SHA256SUMS.txt").open("x", encoding="ascii", newline="\n") as stream:
        for name in names:
            stream.write(sha((output / name).read_bytes()) + "  " + name + "\n")
    print(json.dumps({"suite": report["suite"], "passed": report["passed"],
                      "checks": report["passed_checks"]}))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
