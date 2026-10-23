"""Run the selected real Windows prerequisite suite with a sanitized receipt."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

import ci_diagnostics

ROOT = Path(__file__).resolve().parents[1]
STREAM_CASES = (
    "utf8-crosses-real-read-boundary", "json-secret-crosses-read-boundary",
    "crlf-split-and-no-final-newline", "server-dump-after-chunk-boundary",
    "line-limit-includes-newline", "invalid-utf8-straddles-read",
    "truncated-snapshot-does-not-succeed", "snapshot-length-does-not-read-appended-tail",
    "malformed-final-json-remains-fatal", "json-depth-and-node-limits-remain-fatal",
    "cancellation-before-read-and-before-line", "output-failure-stops-before-next-read",
)
SUITES = {
    "FinalLaunchPreparation": (9, "RESULT 9 final-login preparation checks passed; owned loopback HTTP2 only; no game, Public runtime or live-route admission."),
    "PublicNativeReturn": (27, "RESULT 27 passed, 0 skipped"),
    "DiagnosticTextStreaming": (12, "DIAGNOSTIC_STREAM_SUMMARY cases=12 linked_production=true source_unchanged=true full_zip_tested=false network=false game=false"),
}


def verified_output(text, suite="FinalLaunchPreparation"):
    count, summary = SUITES[suite]
    lines = text.splitlines()
    if suite == "DiagnosticTextStreaming":
        expected = ["DIAGNOSTIC_STREAM_PASS " + case for case in STREAM_CASES] + [summary]
        actual = [line for line in lines if line.startswith(("DIAGNOSTIC_STREAM_", "PASS ", "RESULT ", "SKIP ", "FAIL ", "CI_MANAGED_FAILURE "))]
        return actual == expected
    return (len([line for line in lines if line.startswith("PASS ")]) == count
            and not any(line.startswith(("SKIP ", "FAIL ", "CI_MANAGED_FAILURE ")) for line in lines)
            and [line for line in lines if line.startswith("RESULT ")] == [summary])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--suite", choices=tuple(SUITES), default="FinalLaunchPreparation")
    args = parser.parse_args()
    if sys.platform != "win32" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        ci_diagnostics.emit_error("WINDOWS_HOSTED_LOOPBACK_REQUIRED")
        return 1
    result = {"schema": 1, "suite": args.suite, "passed": False,
              "required_checks": SUITES[args.suite][0], "passed_checks": None, "skipped_checks": None,
              "game_executed": False, "native_interop_exercised": False,
              "full_diagnostic_zip_exercised": False,
              "services_changed": False, "raw_output_retained": False}
    try:
        child = subprocess.run(["dotnet", "run", "--project",
            str(ROOT / f"tests/{args.suite}/{args.suite}.csproj"),
            "-c", "Release", "--no-launch-profile"], cwd=ROOT, capture_output=True,
            text=True, encoding="utf-8", errors="replace", timeout=240)
        text = (child.stdout + "\n" + child.stderr)[-ci_diagnostics.MAX_LOG_BYTES:]
        result["passed"] = child.returncode == 0 and verified_output(text, args.suite)
        if result["passed"]:
            result.update(passed_checks=SUITES[args.suite][0], skipped_checks=0)
        result["process_succeeded"] = child.returncode == 0
        result["diagnostics"] = ci_diagnostics.diagnostic_details(text)
        if not result["passed"]:
            fields = [f"{key}=" + " | ".join(values) for key, values in result["diagnostics"].items() if values]
            ci_diagnostics.emit_error("suite=" + args.suite + "; " + "; ".join(fields or ["WINDOWS_MANAGED_SUITE_RESULT_INVALID"]))
    except subprocess.TimeoutExpired:
        result["error"] = "WINDOWS_MANAGED_SUITE_TIMEOUT"
        ci_diagnostics.emit_error(result["error"])
    except OSError:
        result["error"] = "WINDOWS_MANAGED_SUITE_PROCESS_FAILED"
        ci_diagnostics.emit_error(result["error"])
    try:
        with args.report.open("x", encoding="utf-8") as output:
            json.dump(result, output, indent=2)
            output.write("\n")
    except OSError:
        ci_diagnostics.emit_error("WINDOWS_LOOPBACK_REPORT_FAILED")
        return 1
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
