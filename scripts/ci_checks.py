"""Separate public source checks from the strict, currently blocked package-input gate."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
PACKAGE_TESTS = (
    "test_build.BundledInputTests.test_all_bundled_assets_match_manifest",
    "test_build.BundledInputTests.test_save_template_files_match_manifest",
)


def checked_path(root: Path, name: str) -> Path:
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
        raise ValueError(f"Unsafe manifest path: {name}")
    result = root.joinpath(*path.parts)
    if not result.resolve().is_relative_to(root.resolve()):
        raise ValueError(f"Manifest path escapes checkout: {name}")
    return result


def assess_inputs(root: Path = ROOT) -> dict:
    assets = json.loads((root / "scripts/local-assets.json").read_text(encoding="utf-8-sig"))["files"]
    save_rows = json.loads((root / "src/launcher/src/Assets/DefaultSaves/manifest.json").read_text(encoding="utf-8-sig"))["Files"]
    publication = json.loads((root / "docs/ASSET-PUBLICATION.json").read_text(encoding="utf-8-sig"))
    holds = {row["path"]: row for row in publication["conditional_privacy_holds"]}
    inputs = dict(assets)
    for name, row in save_rows.items():
        full_name = "src/launcher/src/Assets/DefaultSaves/" + name
        if full_name in inputs and inputs[full_name] != row:
            raise ValueError(f"Conflicting asset manifests: {full_name}")
        inputs[full_name] = row
    missing_held, errors, verified = [], [], []
    for name, row in sorted(inputs.items()):
        path = checked_path(root, name)
        hold = holds.get(name)
        if hold and any(hold[key] != row[key] for key in ("sha256", "bytes")):
            errors.append({"path": name, "reason": "privacy_hold_manifest_mismatch"})
            continue
        if not path.is_file():
            if hold and hold.get("status") == "local_preserved_not_published":
                missing_held.append(name)
            else:
                errors.append({"path": name, "reason": "unclassified_missing_input"})
        elif path.stat().st_size != row["bytes"] or hashlib.sha256(path.read_bytes()).hexdigest() != row["sha256"]:
            errors.append({"path": name, "reason": "size_or_sha256_mismatch"})
        else:
            verified.append(name)
    # A matching digest is not privacy approval, including donor-only held files.
    for name in holds:
        if checked_path(root, name).exists():
            errors.append({"path": name, "reason": "privacy_held_input_present_in_public_checkout"})
    return {
        "status": "invalid" if errors else "blocked_privacy_inputs" if missing_held else "ready",
        "required_input_count": len(inputs),
        "verified_input_count": len(verified),
        "held_input_count_all_sources": len(holds),
        "missing_held_package_inputs": missing_held,
        "errors": errors,
        "complete_package_ready": not errors and not missing_held,
    }


def flatten(suite):
    for entry in suite:
        if isinstance(entry, unittest.TestSuite):
            yield from flatten(entry)
        else:
            yield entry


def source_suites(root: Path = ROOT):
    build_tests = list(flatten(unittest.TestLoader().discover(str(root / "tests/build"))))
    found = {test.id() for test in build_tests}
    if not set(PACKAGE_TESTS).issubset(found):
        raise RuntimeError("Package-input test identities changed; review the explicit CI split")
    yield "build-source-logic", unittest.TestSuite(test for test in build_tests if test.id() not in PACKAGE_TESTS)
    for directory in ("ui", "server", "ci"):
        path = root / "tests" / directory
        if path.is_dir():
            suite = unittest.TestLoader().discover(str(path))
            if not suite.countTestCases():
                raise RuntimeError(f"Zero-test suite: tests/{directory}")
            yield directory, suite


def result_row(name: str, result: unittest.TestResult) -> dict:
    # unittest records every failed subTest separately, while testsRun counts its
    # parent method once. Do not report negative passed counts at the asset gate.
    failed_cases = {
        getattr(test, "test_case", test).id()
        for test, _ in [*result.failures, *result.errors]
    }
    return {
        "suite": name, "executed": result.testsRun,
        "passed": result.testsRun - len(failed_cases) - len(result.skipped) - len(result.expectedFailures) - len(result.unexpectedSuccesses),
        "failed_test_cases": len(failed_cases),
        "failures": len(result.failures), "errors": len(result.errors),
        "skipped": [{"test": test.id(), "reason": reason} for test, reason in result.skipped],
        "expected_failures": len(result.expectedFailures),
        "unexpected_successes": len(result.unexpectedSuccesses),
        "successful": result.wasSuccessful(),
    }


def run(mode: str, root: Path = ROOT) -> tuple[dict, int]:
    admission = assess_inputs(root)
    report = {"schema": 1, "mode": mode, "inputs": admission, "suites": [], "game_executed": False, "published": False}
    if mode == "package-inputs":
        sys.path.insert(0, str(root / "tests/build"))
        suite = unittest.TestLoader().loadTestsFromNames(PACKAGE_TESTS)
        result = unittest.TextTestRunner(verbosity=2).run(suite)
        report["suites"].append(result_row("strict-package-inputs", result))
        code = 0 if result.wasSuccessful() and admission["complete_package_ready"] else 1
    else:
        report["deferred_to_strict_package_gate"] = list(PACKAGE_TESTS)
        for test in PACKAGE_TESTS:
            print("DEFERRED_TO_STRICT_PACKAGE_GATE", test, flush=True)
        code = 1 if admission["errors"] else 0
        for name, suite in source_suites(root):
            result = unittest.TextTestRunner(verbosity=2).run(suite)
            report["suites"].append(result_row(name, result))
            if not result.wasSuccessful():
                code = 1
        report["unavailable_optional_suites"] = [str(Path("tests") / name) for name in ("ui", "server") if not (root / "tests" / name).is_dir()]
    report["exit_code"] = code
    return report, code


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("source", "package-inputs"))
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()
    report, code = run(args.mode)
    receipt = args.receipt or ROOT / "artifacts/ci" / (args.mode + "-receipt.json")
    receipt.parent.mkdir(parents=True, exist_ok=True)
    receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(f"### {args.mode}\n\nExit code: {code}. Complete package ready: {report['inputs']['complete_package_ready']}.\n\n")
            output.write("Source/logic checks do not include the two unchanged complete-resource tests; the independent strict package gate runs them.\n\n")
            for name in report["inputs"]["missing_held_package_inputs"]:
                output.write(f"- Privacy-held, missing: `{name}`\n")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
