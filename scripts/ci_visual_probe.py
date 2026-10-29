"""Actual Windows WPF visual evidence, with preserved baseline and rollback phases."""
from __future__ import annotations

import argparse
import difflib
import hashlib
import itertools
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
BASELINE = "12d394086a22c04e7e1defc7678ceccfe02ee004"
FIXTURE_ID = "fifth-freedom-visual-fixture-v1"
STATES = ("main", "settings", "settings-public", "settings-gameplay", "discovery", "join", "create", "cancel")
LAYOUT_CATEGORIES = {"geometry", "overlap", "hit-area", "visual-hit-test", "input-hit-test", "text-clipping", "scroll-accessibility"}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def scenarios() -> list[dict]:
    return [dict(State=state, Language=language, Dpi=dpi, Width=width, Height=height)
            for state, language, dpi, (width, height) in itertools.product(
                STATES, ("zh-CN", "en-US"), (96, 120, 144, 192), ((1120, 680), (900, 580)))]


def validate_frames(report: dict, directory: Path, *, require_hook: bool) -> dict:
    if report.get("Schema") != 1 or report.get("FixtureId") != FIXTURE_ID:
        raise ValueError("Wrong visual fixture identity")
    if any(report.get(name) is not False for name in ("NetworkStarted", "GameStarted", "PersistedSettingsRead")):
        raise ValueError("Visual isolation not established")
    expected = {(row["State"], row["Language"], row["Dpi"], row["Width"], row["Height"]) for row in scenarios()}
    seen = set()
    failures = []
    screenshots = {}
    for frame in report.get("Frames", []):
        identity = tuple(frame.get(name) for name in ("State", "Language", "Dpi", "Width", "Height"))
        if identity not in expected or identity in seen:
            raise ValueError("Unexpected or duplicate visual scenario")
        seen.add(identity)
        if frame.get("ErrorType") or abs(frame.get("ObservedDpi", 0) - frame["Dpi"]) > .01:
            raise ValueError("WPF did not capture a complete scenario at the requested root DPI")
        if require_hook and not frame.get("ProductionStateHook"):
            raise ValueError("Current product state hook was not exercised")
        images = {frame.get("Screenshot", ""): frame.get("Sha256"), **(frame.get("AdditionalScreenshots") or {})}
        for name, digest in images.items():
            if not name or Path(name).name != name or not name.endswith(".png"):
                raise ValueError("Unsafe screenshot identity")
            image = directory / name
            data = image.read_bytes()
            if not data.startswith(b"\x89PNG\r\n\x1a\n") or sha(image) != digest:
                raise ValueError("Screenshot bytes do not match the WPF receipt")
            if len(data) < 1000:
                raise ValueError("Screenshot has no meaningful rendered content")
            width, height = struct.unpack(">II", data[16:24])
            if (width, height) != (round(frame["Width"] * frame["Dpi"] / 96), round(frame["Height"] * frame["Dpi"] / 96)):
                raise ValueError("PNG pixel dimensions do not match the WPF size and DPI")
            screenshots[name] = digest
        if not any(check.get("Category") in LAYOUT_CATEGORIES for check in frame.get("Checks", [])):
            raise ValueError("Scenario lacks layout assertions")
        if require_hook and frame["State"] not in ("main", "cancel") and not any(check.get("Category") == "keyboard" for check in frame["Checks"]):
            raise ValueError("Current overlay lacks real-window keyboard checks")
        if require_hook and not any(check.get("Category") == "input-hit-test" for check in frame["Checks"]):
            raise ValueError("Current state lacks shown-window input hit tests")
        failures.extend({"frame": frame["Id"], **check} for check in frame["Checks"] if not check["Passed"])
    if seen != expected:
        raise ValueError("Visual matrix is incomplete")
    return {"frames": len(seen), "failures": failures, "screenshots": screenshots,
            "layout_failure_count": sum(check["Category"] in LAYOUT_CATEGORIES for check in failures)}


def run_logged(command, log: Path, cwd=ROOT, env=None) -> int:
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open("w", encoding="utf-8") as stream:
        result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
    return result.returncode


def publish(source: Path, output: Path, logs: Path, label: str) -> None:
    code = run_logged(["dotnet", "publish", source / "src/launcher/src/SplinterCellCNLauncher.csproj",
                       "-c", "Release", "-r", "win-x86", "--self-contained", "true", "--nologo", "-o", output],
                      logs / (label + "-publish.txt"), cwd=source)
    if code:
        raise RuntimeError(label + " WPF publish failed; see preserved build log")


def instrument_baseline(source: Path) -> dict:
    # Copy only the reviewed probe and inject its isolated entry into the old
    # product's own App. Do not transplant current product behavior into baseline.
    shutil.copyfile(ROOT / "src/launcher/src/MainWindow.VisualProbe.cs", source / "src/launcher/src/MainWindow.VisualProbe.cs")
    path = source / "src/launcher/src/App.xaml.cs"
    text = path.read_text(encoding="utf-8-sig")
    original = text
    original_hash = sha(path)
    if "--visual-probe" in text:
        raise ValueError("Baseline unexpectedly already contains the visual probe")
    marker = '        bool toolsCheck=e.Args.Length==2 && e.Args[0]=="--lan-tools-self-test";'
    if text.count(marker) != 1 or text.count("        PreviewMode=musicCheck") != 1:
        raise ValueError("Baseline startup changed; review probe injection")
    text = text.replace(marker, marker + '\n        bool visualProbe=e.Args.Length==2 && e.Args[0]=="--visual-probe";')
    text = text.replace("        PreviewMode=musicCheck", "        PreviewMode=visualProbe || musicCheck")
    marker = "            try\n            {\n"
    if text.count(marker) != 1:
        raise ValueError("Baseline isolated startup changed")
    text = text.replace(marker, marker + "            if(visualProbe) {Shutdown(SplinterCellCNLauncher.MainWindow.RunVisualProbe(e.Args[1]));return;}\n")
    path.write_text(text, encoding="utf-8")
    return {"baseline_app_original_sha256": original_hash, "baseline_app_instrumented_sha256": sha(path),
            "probe_sha256": sha(source / "src/launcher/src/MainWindow.VisualProbe.cs"),
            "entry_patch": "".join(difflib.unified_diff(original.splitlines(keepends=True), text.splitlines(keepends=True),
                               fromfile="baseline/App.xaml.cs", tofile="instrumented/App.xaml.cs"))}


def capture_phase(program: Path, phase: str, revision: str, artifacts: Path) -> dict:
    destination = artifacts / phase
    destination.mkdir()
    with tempfile.TemporaryDirectory(prefix="ff-wpf-visual-") as temporary:
        isolated = Path(temporary)
        executable_root = isolated / "program"
        shutil.copytree(program, executable_root)
        environment = os.environ.copy()
        for name in ("LOCALAPPDATA", "APPDATA", "TEMP", "TMP"):
            folder = isolated / name.lower()
            folder.mkdir()
            environment[name] = str(folder)
        environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(isolated / "bundle")
        config = isolated / "visual-config.json"
        config.write_text(json.dumps(dict(FixtureId=FIXTURE_ID, Phase=phase, SourceCommit=revision,
                              OutputDirectory=str(destination), Scenarios=scenarios())), encoding="utf-8")
        with (destination / "process.txt").open("w", encoding="utf-8") as log:
            result = subprocess.run([str(executable_root / "FifthFreedom.exe"), "--visual-probe", str(config)],
                                    cwd=executable_root, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=300)
        if config.with_suffix(".json.error.txt").is_file():
            shutil.copyfile(config.with_suffix(".json.error.txt"), destination / "startup.error.txt")
        report = json.loads((destination / "results.json").read_text(encoding="utf-8-sig"))
        summary = validate_frames(report, destination, require_hook=phase in ("modified", "reapplied"))
        if result.returncode not in (0, 1) or bool(result.returncode) != bool(summary["failures"]):
            raise ValueError("Process status contradicts its layout result")
        return {"phase": phase, "source_commit": revision, "executable_sha256": sha(program / "FifthFreedom.exe"),
                "exit_code": result.returncode, **summary}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/visual-probe")
    args = parser.parse_args()
    if os.name != "nt":
        raise SystemExit("Actual WPF visual capture requires a GitHub-hosted Windows runner")
    artifacts = args.output.resolve()
    artifacts.mkdir(parents=True, exist_ok=False)
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT, text=True).strip():
        raise SystemExit("Visual source must be a clean tracked checkout for exact revision evidence")
    results = []
    error = None
    try:
        with tempfile.TemporaryDirectory(prefix="ff-wpf-baseline-") as temporary:
            root = Path(temporary)
            archive = root / "baseline.tar"
            subprocess.run(["git", "archive", "--format=tar", "-o", str(archive), BASELINE], cwd=ROOT, check=True)
            baseline = root / "source"
            baseline.mkdir()
            with tarfile.open(archive) as bundle:
                bundle.extractall(baseline, filter="data")
            instrumentation = instrument_baseline(baseline)
            (artifacts / "baseline-entry.patch").write_text(instrumentation.pop("entry_patch"), encoding="utf-8")
            (artifacts / "instrumentation.json").write_text(json.dumps(instrumentation, indent=2) + "\n", encoding="utf-8")
            baseline_program = root / "baseline-program"
            current_program = root / "modified-program"
            publish(baseline, baseline_program, artifacts / "logs", "baseline")
            results.append(capture_phase(baseline_program, "baseline", BASELINE, artifacts))
            publish(ROOT, current_program, artifacts / "logs", "modified")
            results.append(capture_phase(current_program, "modified", revision, artifacts))
            results.append(capture_phase(baseline_program, "rollback", BASELINE, artifacts))
            results.append(capture_phase(current_program, "reapplied", revision, artifacts))
    except Exception as failure:
        error = type(failure).__name__ + ": " + str(failure)
    complete = len(results) == 4
    baseline_failed = complete and all(results[index]["layout_failure_count"] > 0 for index in (0, 2))
    current_passed = complete and all(not results[index]["failures"] for index in (1, 3))
    paired = complete and all(results[a]["executable_sha256"] == results[b]["executable_sha256"] for a, b in ((0, 2), (1, 3)))
    receipt = dict(schema=1, fixture_id=FIXTURE_ID, source_commit=revision, baseline_commit=BASELINE,
                   root_dpi_not_os_display_dpi=True, game_started=False, network_started=False,
                   results=results, error=error, baseline_layout_failure_reproduced=baseline_failed,
                   modified_and_reapplied_passed=current_passed, rollback_executable_hashes_match=paired)
    (artifacts / "comparison.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    if error or not (baseline_failed and current_passed and paired):
        print("::error::WPF visual comparison failed; inspect preserved screenshots and layout receipts", flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
