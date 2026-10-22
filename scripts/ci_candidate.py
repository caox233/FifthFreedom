"""Build isolated CI components; these receipts never certify a complete game package."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from contextlib import contextmanager

import build
import ci_diagnostics

ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "artifacts"
LANES = ("windows-components", "windows-package-smoke", "linux-lan-baseline", "linux-public-donor-reference")


def run_build_step(label, args, cwd=None, env=None):
    """Summarize only logs created by this invocation, never stale cached output."""
    folder = ART / "logs"
    before = set(folder.glob("*")) if folder.is_dir() else set()
    def fresh_logs():
        return sorted(path for suffix in ("stdout", "stderr")
                      for path in folder.glob(f"*-{label}.{suffix}.txt") if path not in before)
    with ci_diagnostics.diagnostic_step(label, fresh_logs):
        return build.run(label, args, cwd, env)


def parse_probe_report(text: str, flag: str) -> int:
    if "FAIL" in text:
        raise RuntimeError("Launcher probe reported a failure")
    passes = len(re.findall(r"^PASS .+$", text, re.MULTILINE))
    pattern = (r"^RESULT (\d+) isolated UI/protocol checks passed\.$" if flag == "--self-test"
               else r"^RESULT (\d+) passed; 0 failed; isolated UI and save fixtures only$")
    summaries = re.findall(pattern, text, re.MULTILINE)
    if len(summaries) != 1 or passes == 0 or int(summaries[0]) != passes:
        raise RuntimeError("Launcher probe has no complete, matching nonzero RESULT summary")
    return passes


def run_launcher_probe(program: Path, flag: str, destination: Path) -> dict:
    label = "launcher-ui-smoke" if flag == "--self-test" else "launcher-bundled-save-smoke"
    known_labels = ci_diagnostics.known_ui_labels([
        ROOT / "src/launcher/src/MainWindow.Diagnostics.cs",
        ROOT / "src/launcher/src/MainWindow.LanToolsProbe.cs",
    ])
    with ci_diagnostics.diagnostic_step(label, lambda: [destination / "report.txt", destination / "error.txt"], known_labels):
        return _run_launcher_probe(program, flag, destination)


def _run_launcher_probe(program: Path, flag: str, destination: Path) -> dict:
    if flag not in ("--self-test", "--lan-tools-self-test"):
        raise ValueError("Only reviewed no-game launcher probes are allowed")
    destination.mkdir(parents=True, exist_ok=False)
    # LogService owns executable/data/<machine>/<user>; redirecting APPDATA alone
    # would not isolate this program. Each probe gets a fresh entire program copy.
    with tempfile.TemporaryDirectory(prefix="fifth-ci-launcher-probe-") as temporary:
        root = Path(temporary)
        isolated = root / "program"
        shutil.copytree(program, isolated)
        report = root / "probe.txt"
        env = os.environ.copy()
        for name in ("LOCALAPPDATA", "APPDATA", "TEMP", "TMP"):
            path = root / name.lower()
            path.mkdir()
            env[name] = str(path)
        env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(root / "dotnet-bundle")
        try:
            result = subprocess.run([str(isolated / "FifthFreedom.exe"), flag, str(report)],
                                    cwd=isolated, env=env, capture_output=True, timeout=180)
        except subprocess.TimeoutExpired as error:
            (destination / "stdout.txt").write_bytes(error.stdout or b"")
            (destination / "stderr.txt").write_bytes(error.stderr or b"")
            build.write(destination / "process.json", {"flag": flag, "exit_code": None, "timeout_seconds": 180})
            raise RuntimeError(f"Launcher probe {flag} exceeded 180 seconds") from error
        (destination / "stdout.txt").write_bytes(result.stdout)
        (destination / "stderr.txt").write_bytes(result.stderr)
        text = report.read_text(encoding="utf-8-sig") if report.is_file() else ""
        (destination / "report.txt").write_text(text, encoding="utf-8")
        error = report.with_suffix(".txt.error.txt")
        if error.is_file():
            shutil.copyfile(error, destination / "error.txt")
        build.write(destination / "process.json", {"flag": flag, "exit_code": result.returncode, "report_present": report.is_file()})
        if result.returncode:
            raise RuntimeError(f"Launcher probe {flag} exited {result.returncode}")
        passed = parse_probe_report(text.replace("\r\n", "\n"), flag)
        print(f"NO_GAME_PROBE_PASS {flag} checks={passed}", flush=True)
        return {"flag": flag, "passed": passed, "failed": 0, "exit_code": 0, "game_executed": False}


@contextmanager
def owned_server_test_env(base: dict):
    """Keep auth/logger fixtures and SQLite rows inside a fresh test-owned root."""
    with tempfile.TemporaryDirectory(prefix="fifth-ci-server-fixtures-") as temporary:
        root = Path(temporary)
        identity = root / "identity"
        database = root / "database"
        identity.mkdir()
        database.mkdir()
        env = dict(base)
        # Never use any inherited path that could name a real account database.
        env["SCBL_IDENTITY_TEST_DIR"] = str(identity)
        env["SCBL_RELEASE_TEST_DIR"] = str(database)
        yield env


def context(lane: str) -> dict:
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT, text=True).strip())
    return {
        "schema": 1, "lane": lane, "commit": commit, "tracked_source_dirty": dirty,
        "github_actions": os.environ.get("GITHUB_ACTIONS") == "true",
        "run_id": os.environ.get("GITHUB_RUN_ID"),
        "complete_package": False, "game_executed": False,
        "live_acceptance": False, "deployed": False, "published": False,
    }


def collect(lane: str, files: dict[str, Path], receipt: dict) -> None:
    destination = ART / "ci" / lane / "components"
    destination.mkdir(parents=True, exist_ok=False)
    rows = {}
    for name, source in sorted(files.items()):
        target = destination / name
        shutil.copyfile(source, target)
        rows[name] = {"sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "bytes": target.stat().st_size}
    for name in ("LICENSE", "THIRD_PARTY_NOTICES.txt"):
        shutil.copyfile(ROOT / name, destination / name)
    notices = ROOT / "src/launcher/src/ThirdPartyNotices"
    if lane == "windows-components" and notices.is_dir():
        shutil.copytree(notices, destination / "ThirdPartyNotices")
    (destination / "README.txt").write_text(
        "Fifth Freedom CI component compilation evidence.\n"
        "NOT a complete or runtime-validated game package. Do not install these files into a game.\n"
        "The launcher may lack privacy-held default saves. Package admission remains a separate strict gate.\n"
        "Linux public donor is reference evidence, not an integrated/public-deployable server.\n"
        "No game, production service, or capture was started.\n", encoding="utf-8")
    sums = "".join(f"{row['sha256']}  {name}\n" for name, row in sorted(rows.items()))
    (destination / "SHA256SUMS.txt").write_text(sums, encoding="ascii")
    receipt["components"] = rows


def windows_components(receipt: dict) -> None:
    if os.name != "nt":
        raise RuntimeError("Windows component compilation requires a Windows runner")
    # No asset insertion or MSBuild resource removal. The project wildcards omit
    # privacy-held SAV/META; this remains incomplete-package component evidence.
    launcher = ART / "ci/windows-launcher-compile"
    run_build_step("ci-wpf-compile", [build.dotnet(), "publish", ROOT / "src/launcher/src/SplinterCellCNLauncher.csproj",
              "-c", "Release", "-r", "win-x86", "--self-contained", "true", "--nologo", "-o", launcher])
    receipt["launcher_probes"] = [run_launcher_probe(launcher, "--self-test", ART / "ci/windows-components/ui-smoke")]
    for name in ("PortPlan", "BackgroundTaskRegression", "ObserverSession", "NetworkEnvironment"):
        run_build_step("ci-test-" + name, [build.dotnet(), "run", "--project", ROOT / f"tests/{name}/{name}.csproj",
                  "-c", "Release", "--no-launch-profile"])
    cargo, env = build.native_env()
    env["CARGO_BUILD_JOBS"] = "2"
    env["CARGO_NET_GIT_FETCH_WITH_CLI"] = "true"
    native = ROOT / "src/native"
    run_build_step("ci-native-core-tests", [cargo, "test", "--locked", "-p", "hooks-plugin-api", "-p", "hooks-config", "-p", "api", "--lib"], native, env)
    for label, args in (
        ("hooks", ["-p", "hooks", "--lib", "--features", "patch-free,lan-goal-origin"]),
        ("gameplay", ["-p", "scbl-gameplay", "--lib", "--features", "goggle-feedback"]),
        ("lan-server", ["-p", "dedicated_server", "--bin", "dedicated_server"]),
    ):
        run_build_step("ci-build-" + label, [cargo, "build", "--release", "--locked", *args], native, env)
    target = Path(env["CARGO_TARGET_DIR"])
    collect("windows-components", {
        "FifthFreedom.component-only.exe": launcher / "FifthFreedom.exe",
        "uplay_r1_loader.dll": target / "i686-pc-windows-msvc/release/hooks.dll",
        "scbl_gameplay.dll": target / "i686-pc-windows-msvc/release/scbl_gameplay.dll",
        "FifthFreedom.Server.exe": target / "release/dedicated_server.exe",
    }, receipt)


def windows_package_smoke(receipt: dict) -> None:
    if os.name != "nt":
        raise RuntimeError("Packaged WPF probes require a Windows runner")
    result = build.read(ART / "BUILD-RESULT.json")
    if result.get("status") != "built-and-package-verified":
        raise RuntimeError("No verified full package from this build")
    program = Path(result["program"]).resolve()
    if not program.is_relative_to((ART / "program").resolve()):
        raise RuntimeError("Package path is outside the build-owned program directory")
    build.verify_latest(program)
    receipt["launcher_probes"] = [
        run_launcher_probe(program, flag, ART / "ci/windows-package-smoke" / name)
        for flag, name in (("--self-test", "ui"), ("--lan-tools-self-test", "lan-tools"))
    ]
    receipt["complete_package"] = True
    receipt["archive_sha256"] = result["archive_sha256"]


def linux_server(lane: str, receipt: dict) -> None:
    if not sys.platform.startswith("linux"):
        raise RuntimeError("Linux server compilation requires a Linux runner")
    donor = lane == "linux-public-donor-reference"
    workspace = ROOT / ("integration/scbl3" if donor else "src/native")
    env = os.environ.copy()
    env.update(CARGO_TARGET_DIR=str(ART / "ci/cargo" / lane), CARGO_BUILD_JOBS="2", CARGO_INCREMENTAL="0", CARGO_NET_GIT_FETCH_WITH_CLI="true")
    receipt["workspace"] = workspace.relative_to(ROOT).as_posix()
    packages = ["dedicated_server", "dedicated_server_config", "quazal", "sc_bl_protocols", "api"]
    args = ["cargo", "test", "--locked"]
    for package in packages:
        args.extend(["-p", package])
    with owned_server_test_env(env) as test_env:
        run_build_step("ci-" + lane + "-tests", args, workspace, test_env)
    run_build_step("ci-" + lane + "-build", ["cargo", "build", "--release", "--locked", "-p", "dedicated_server", "--bin", "dedicated_server"], workspace, env)
    if donor:
        manager_env = env.copy()
        manager_env["PYTHONPATH"] = str(workspace / "server/manager")
        run_build_step("ci-donor-manager-tests", [sys.executable, "-B", "-m", "unittest", "discover", "-s", "server/manager/tests", "-v"], workspace, manager_env)
    collect(lane, {"dedicated_server": Path(env["CARGO_TARGET_DIR"]) / "release/dedicated_server"}, receipt)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("lane", choices=LANES)
    args = parser.parse_args()
    receipt = context(args.lane)
    code = 1
    try:
        with ci_diagnostics.diagnostic_step(args.lane):
            if args.lane == "windows-components":
                windows_components(receipt)
            elif args.lane == "windows-package-smoke":
                windows_package_smoke(receipt)
            else:
                linux_server(args.lane, receipt)
        receipt["status"] = "package_no_game_checks_passed" if args.lane == "windows-package-smoke" else "components_built_and_selected_tests_passed"
        code = 0
    except Exception as error:
        receipt.update(status="failed", error=str(error))
        print(str(error), file=sys.stderr)
    finally:
        receipt["exit_code"] = code
        build.write(ART / "ci" / args.lane / "receipt.json", receipt)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
