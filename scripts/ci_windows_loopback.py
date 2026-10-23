"""Run only the real final-login test and preserve a sanitized same-run receipt."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

import ci_diagnostics

ROOT = Path(__file__).resolve().parents[1]


def verified_output(text):
    lines = text.splitlines()
    return (len([line for line in lines if line.startswith("PASS ")]) == 9
            and not any(line.startswith(("SKIP ", "FAIL ", "CI_MANAGED_FAILURE ")) for line in lines)
            and lines.count("RESULT 9 final-login preparation checks passed; owned loopback HTTP2 only; no game, Public runtime or live-route admission.") == 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != "win32" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        ci_diagnostics.emit_error("WINDOWS_HOSTED_LOOPBACK_REQUIRED")
        return 1
    result = {"schema": 1, "suite": "FinalLaunchPreparation", "passed": False,
              "game_executed": False, "services_changed": False, "raw_output_retained": False}
    try:
        child = subprocess.run(["dotnet", "run", "--project",
            str(ROOT / "tests/FinalLaunchPreparation/FinalLaunchPreparation.csproj"),
            "-c", "Release", "--no-launch-profile"], cwd=ROOT, capture_output=True,
            text=True, encoding="utf-8", errors="replace", timeout=240)
        text = (child.stdout + "\n" + child.stderr)[-ci_diagnostics.MAX_LOG_BYTES:]
        result["passed"] = child.returncode == 0 and verified_output(text)
        result["process_succeeded"] = child.returncode == 0
        result["diagnostics"] = ci_diagnostics.diagnostic_details(text)
        if not result["passed"]:
            fields = [f"{key}=" + " | ".join(values) for key, values in result["diagnostics"].items() if values]
            ci_diagnostics.emit_error("suite=FinalLaunchPreparation; " + "; ".join(fields or ["WINDOWS_FINAL_SUITE_RESULT_INVALID"]))
    except subprocess.TimeoutExpired:
        result["error"] = "WINDOWS_FINAL_SUITE_TIMEOUT"
        ci_diagnostics.emit_error(result["error"])
    except OSError:
        result["error"] = "WINDOWS_FINAL_SUITE_PROCESS_FAILED"
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
