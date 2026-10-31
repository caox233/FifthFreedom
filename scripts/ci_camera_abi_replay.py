"""Run the real camera detour against owned Win32 x86 virtual dispatch fixtures.

The public e4 source is the negative control. Compile failures, unobserved
crashes and skipped tests never count as ABI evidence. A strictly observed
owned-call exception is recorded as a partial negative run, separately from
the complete matrix required of the correction. No retail instruction body,
game process or private input is used.
"""
from __future__ import annotations

import argparse
from itertools import product
import json
import os
from pathlib import Path
import re
import tempfile

import ci_native_production_replay as common

ROOT = common.ROOT
BASELINE = "0e691519b878a8fbbb0695392eb2a2ba9616e841"
SOURCE = "src/native/client/hooks/gameplay/src/sticky_camera_runtime.rs"
FIXTURE = "src/native/client/hooks/gameplay/src/sticky_camera_abi_tests.rs"
MODULE = '\n#[cfg(all(test, feature = "camera-abi-fixture"))]\n#[path = "sticky_camera_abi_tests.rs"]\nmod abi_replay;\n'
TEST = "sticky_camera_runtime::abi_replay::camera_disable_native_stack_contract"


def marker_objects(stdout: str, marker: str) -> list[dict]:
    records = []
    for suffix in re.findall(r"\b" + re.escape(marker) + r"\b([^\r\n]*)", stdout):
        try:
            value = json.loads(suffix)
            if type(value) is not dict:
                raise ValueError()
            records.append(value)
        except (ValueError, TypeError):
            raise ValueError("CAMERA_ABI_MARKER_INVALID") from None
    return records


def instrument(raw: bytes) -> tuple[bytes, dict]:
    """Only append the same fixture module to a baseline that predates it."""
    suffix = MODULE.encode("utf-8")
    appended = suffix not in raw
    if b"mod abi_replay;" in raw and (appended or raw.count(suffix) != 1):
        raise ValueError("CAMERA_ABI_MODULE_MISMATCH")
    compiled = raw + suffix if appended else raw
    restored = compiled[:-len(suffix)] if appended else compiled
    if restored != raw:
        raise ValueError("CAMERA_ABI_INSTRUMENTATION_REVERSE_FAILED")
    return compiled, {"canonical_sha256": common.sha(raw), "compiled_sha256": common.sha(compiled),
                      "test_module_appended": appended, "exact_reverse_to_canonical": True}


def inspect_result(phase: str, exit_code: int, stdout: str, stderr: str) -> dict:
    old = phase in ("baseline", "rollback")
    if phase not in ("baseline", "modified", "rollback", "reapplied"):
        raise ValueError("CAMERA_ABI_PHASE_INVALID")
    summaries = common.SUMMARY.findall(stdout)
    aborts = marker_objects(stdout, "CAMERA_ABI_ABORT")
    if aborts:
        return inspect_observed_fault(phase, exit_code, stdout, stderr, summaries, aborts)
    expected = ("FAILED", "0", "1", "0", "0") if old else ("ok", "1", "0", "0", "0")
    if len(summaries) != 2 or any(row[:5] != expected for row in summaries) or exit_code != (101 if old else 0):
        raise ValueError("CAMERA_ABI_EXECUTED_TEST_RESULT_INVALID")
    if old and "CAMERA_ABI_CONTRACT_FAILED" not in stdout + stderr:
        raise ValueError("CAMERA_ABI_EXPECTED_ASSERTION_MISSING")
    failed_names = re.findall(r"^    (\S+)\s*$", stdout, re.M)
    if failed_names != ([TEST, TEST] if old else []):
        raise ValueError("CAMERA_ABI_UNEXPECTED_FAILED_TEST")
    if re.findall(r"\bCAMERA_ABI_CHILD_EXIT\b([^\r\n]*)", stdout) != [" code=" + ("101" if old else "0")]:
        raise ValueError("CAMERA_ABI_CHILD_EXIT_INVALID")
    markers = marker_objects(stdout, "CAMERA_ABI_RESULT")
    if len(markers) != 1:
        raise ValueError("CAMERA_ABI_RESULT_MISSING_OR_DUPLICATED")
    result = markers[0]
    # The fixture contract is deliberately explicit. Never accept a shorter
    # positive run or unrelated assertion failure as a baseline observation.
    if (any(type(result.get(k)) is not int for k in ("schema", "cases", "hooked_calls", "unhooked_calls", "cleanup_calls", "negative_controls", "checks"))
            or result.get("schema") != 1 or result.get("cases") != 64
            or result.get("hooked_calls") != 32 or result.get("unhooked_calls") != 32
            or result.get("cleanup_calls") != 64 or result.get("negative_controls") != 1
            or result.get("checks") != 515 or result.get("status") != ("failed" if old else "passed")
            or result.get("game_executed") is not False or result.get("retail_body") is not False
            or result.get("native_return_type_confirmed") is not False):
        raise ValueError("CAMERA_ABI_COVERAGE_INVALID")
    failures = result.get("failures")
    expected_keys = {"stack_cleanup", "argument_forwarding", "receiver", "nonvolatile", "native_count", "cleanup",
                     "caller_stack", "branch", "restoration"}
    if (type(failures) is not dict or set(failures) != expected_keys
            or any(type(v) is not int or v < 0 for v in failures.values())):
        raise ValueError("CAMERA_ABI_FAILURE_COUNTS_INVALID")
    if (failures["stack_cleanup"] != (32 if old else 0)
            or not 0 <= failures["argument_forwarding"] <= (32 if old else 0)
            or any(failures[k] for k in expected_keys - {"stack_cleanup", "argument_forwarding"})):
        raise ValueError("CAMERA_ABI_UNEXPECTED_REGRESSION")
    values = (0, 0xFFFFFFFF, 0x13579BDF, 0x2468ACE0)
    required = set(product(range(4), range(2), range(2), values))
    rows = marker_objects(stdout, "CAMERA_ABI_CASE")
    seen = set()
    argument_failures = 0
    try:
        for row in rows:
            key = tuple(row[k] for k in ("stage", "repeat", "receiver", "argument"))
            hooked = row["stage"] in (1, 3)
            if (any(type(v) is not int for v in key) or key not in required or key in seen
                    or row["hooked"] is not hooked or type(row["received_argument"]) is not int
                    or not 0 <= row["received_argument"] <= 0xFFFFFFFF
                    or type(row["stack_delta"]) is not int or row["stack_delta"] != (-4 if old and hooked else 0)
                    or type(row["branch"]) is not int
                    or row["branch"] != (0 if row["received_argument"] == 0 else 2 if row["received_argument"] == 0xFFFFFFFF else 1)):
                raise ValueError()
            wrong = row["received_argument"] != row["argument"]
            if wrong and not (old and hooked):
                raise ValueError()
            argument_failures += int(wrong)
            seen.add(key)
        if seen != required or argument_failures != failures["argument_forwarding"]:
            raise ValueError()
    except (ValueError, TypeError, KeyError):
        raise ValueError("CAMERA_ABI_CASE_MATRIX_INVALID") from None
    return result


def inspect_observed_fault(phase: str, exit_code: int, stdout: str, stderr: str,
                           summaries: list, aborts: list[dict]) -> dict:
    """A bounded observed bad-call exception is not a completed 64-case run.

    The fixture never resumes the exception context. Its parent must fail a
    real assertion after the child emits the precise owned-call observation.
    Every other crash or missing prerequisite still fails the runner itself.
    """
    if (phase not in ("baseline", "rollback") or exit_code != 101 or len(aborts) != 1
            or len(summaries) != 1 or summaries[0][:5] != ("FAILED", "0", "1", "0", "0")
            or "CAMERA_ABI_CONTRACT_FAILED" not in stderr + stdout
            or re.findall(r"^    (\S+)\s*$", stdout, re.M) != [TEST]
            or re.findall(r"\bCAMERA_ABI_CHILD_EXIT\b([^\r\n]*)", stdout) != [" code=86"]
            or marker_objects(stdout, "CAMERA_ABI_RESULT")):
        raise ValueError("CAMERA_ABI_OBSERVED_FAULT_ENVELOPE_INVALID")
    event = aborts[0]
    required_numbers = {"schema": 1, "completed_cases": 16, "stage": 1, "repeat": 0, "receiver": 0,
                        "argument": 0x13579BDF, "native_count_delta": 1, "prior_failures": 0,
                        "cleanup_calls": 17, "exception_code": 0xC0000005, "access": 8, "child_exit_code": 86}
    required_flags = {"receiver_correct": True, "cleanup_correct": True, "previous_cases_validated": True,
                      "context_resumed": False, "game_executed": False}
    if (any(type(event.get(k)) is not int or event[k] != v for k, v in required_numbers.items())
            or any(event.get(k) is not v for k, v in required_flags.items())
            or any(type(event.get(k)) is not int or not 0 <= event[k] <= 0xFFFFFFFF
                   for k in ("received_argument", "eip", "exception_address", "access_target"))
            or event["received_argument"] == event["argument"]
            or not event["eip"] == event["exception_address"] == event["access_target"]):
        raise ValueError("CAMERA_ABI_OBSERVED_FAULT_CONTEXT_INVALID")
    expected = list(product((0,), range(2), range(2), (0x13579BDF, 0, 0xFFFFFFFF, 0x2468ACE0)))
    cases = marker_objects(stdout, "CAMERA_ABI_CASE")
    try:
        if len(cases) != len(expected):
            raise ValueError()
        for row, key in zip(cases, expected):
            actual = tuple(row[k] for k in ("stage", "repeat", "receiver", "argument"))
            argument = key[3]
            if (any(type(v) is not int for v in actual) or actual != key or row["hooked"] is not False
                    or type(row["received_argument"]) is not int or row["received_argument"] != argument
                    or type(row["stack_delta"]) is not int or row["stack_delta"] != 0
                    or type(row["branch"]) is not int or row["branch"] != (0 if argument == 0 else 2 if argument == 0xFFFFFFFF else 1)):
                raise ValueError()
    except (ValueError, TypeError, KeyError):
        raise ValueError("CAMERA_ABI_OBSERVED_FAULT_PRIOR_CASES_INVALID") from None
    return {"status": "expected_missing_argument_fault_observed", "completed_cases": 16,
            "first_hooked_call_returned": False, "remaining_cases_executed": False,
            "complete_matrix_pass_claimed": False, "observation": event}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/ci/camera-abi-replay")
    options = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("CAMERA_ABI_REQUIRES_WINDOWS_X86_BUILD_TOOLS")
    output = options.output.resolve()
    if output.exists():
        raise RuntimeError("CAMERA_ABI_OUTPUT_ALREADY_EXISTS")
    output.mkdir(parents=True)
    head = common.git("rev-parse", "HEAD").decode("ascii").strip()
    receipt = {"schema": 1, "source_commit": head, "baseline_commit": BASELINE, "status": "started",
               "scope": "Production StaticDetour and replacement; synthetic x86 vtable caller and native leaf; not retail instruction replay",
               "game_executed": False, "private_inputs": False, "c71_inputs_replaced": False, "roles": []}
    common.write(output / "receipt.json", receipt)
    try:
        tools = ("scripts/ci_camera_abi_replay.py", "scripts/ci_native_production_replay.py", "scripts/build.py")
        receipt["runner_sources"] = {}
        for name in tools:
            canonical_tool = common.git("show", head + ":" + name)
            working_tool = (ROOT / name).read_bytes()
            if working_tool.replace(b"\r\n", b"\n") != canonical_tool.replace(b"\r\n", b"\n"):
                raise RuntimeError("CAMERA_ABI_RUNNER_SOURCE_CHANGED")
            receipt["runner_sources"][name] = {"canonical_sha256": common.sha(canonical_tool),
                                               "working_sha256": common.sha(working_tool), "only_checkout_crlf_difference_allowed": True}
        canonical = {"baseline": common.git("show", BASELINE + ":" + SOURCE),
                     "modified": common.git("show", head + ":" + SOURCE)}
        if (ROOT / SOURCE).read_bytes().replace(b"\r\n", b"\n") != canonical["modified"].replace(b"\r\n", b"\n"):
            raise RuntimeError("CAMERA_ABI_TRACKED_SOURCE_CHANGED")
        versions = {name: instrument(raw) for name, raw in canonical.items()}
        receipt["production_source"] = {"path": SOURCE, **{name: value[1] for name, value in versions.items()}}
        receipt["fixture"] = {"path": FIXTURE, "sha256": common.sha(common.git("show", head + ":" + FIXTURE))}
        import build
        cargo, env = build.native_env()
        env.pop("FF_CAMERA_ABI_CHILD", None)
        target = Path(env["CARGO_TARGET_DIR"]).resolve()
        with tempfile.TemporaryDirectory(prefix="ff-camera-abi-") as directory:
            checkout = Path(directory)
            common.extract_checkout(common.git("archive", "--format=tar", head), checkout)
            for phase in ("baseline", "modified", "rollback", "reapplied"):
                variant = "baseline" if phase in ("baseline", "rollback") else "modified"
                compiled, identity = versions[variant]
                (checkout / SOURCE).write_bytes(compiled)
                role = {"name": phase, "source": identity}
                receipt["roles"].append(role)
                args = [str(cargo), "test", "--locked", "-p", "scbl-gameplay", "--lib", "--features",
                        "camera-abi-fixture", "--no-run", "--message-format=json"]
                role["compile"] = common.run_command(args, checkout / "src/native", env, output, phase + "-compile", 1800)
                common.write(output / "receipt.json", receipt)
                if role["compile"]["exit_code"] != 0:
                    raise RuntimeError("CAMERA_ABI_COMPILE_FAILURE_NOT_NEGATIVE_CONTROL")
                exe = common.select_executable((output / role["compile"]["stdout"]).read_text(encoding="utf-8"), target)
                role["executable"] = {"file": exe.name, "machine": "i386", "sha256": common.sha(exe.read_bytes()), "bytes": exe.stat().st_size}
                role["execution"] = common.run_command([str(exe), TEST, "--exact", "--nocapture", "--test-threads=1"],
                    checkout / "src/native", env, output, phase + "-execute", 90)
                common.write(output / "receipt.json", receipt)
                result = role["execution"]
                role["checks"] = inspect_result(phase, result["exit_code"],
                    (output / result["stdout"]).read_text(encoding="utf-8"), (output / result["stderr"]).read_text(encoding="utf-8"))
                common.write(output / "receipt.json", receipt)
                print(f"PASS camera-abi {phase} {role['checks']}", flush=True)
        if receipt["roles"][0]["source"] != receipt["roles"][2]["source"] or receipt["roles"][1]["source"] != receipt["roles"][3]["source"]:
            raise RuntimeError("CAMERA_ABI_ROLLBACK_SOURCE_MISMATCH")
        receipt["status"] = "passed"
        common.write(output / "receipt.json", receipt)
        return 0
    except BaseException as error:
        receipt["status"] = "failed"
        receipt["error_type"] = type(error).__name__
        receipt["failure"] = str(error) if re.fullmatch(r"CAMERA_ABI_[A-Z0-9_]+", str(error)) else "CAMERA_ABI_PREREQUISITE_OR_EXECUTION_FAILED"
        common.write(output / "receipt.json", receipt)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
