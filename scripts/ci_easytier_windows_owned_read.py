"""Compile actual Instance owner-read hook on the independently pinned no-Packet tree."""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import re
import subprocess
import sys

import ci_easytier_windows_no_packet as base

paired, source, ROOT = base.paired, base.source, base.ROOT
LOCK = ROOT / "scripts/easytier-windows-owned-read.lock.json"
FILES = frozenset({"easytier/src/lib.rs", "easytier/src/launcher.rs", "easytier/src/owned_read.rs", "easytier/src/owned_read_windows.rs"})
CODES = frozenset("OWNED_READ_RECIPE OWNED_READ_BASE OWNED_READ_CONTRACT OWNED_READ_PATCH_SCOPE OWNED_READ_INPUT_CHANGED OWNED_READ_COMMAND OWNED_READ_CHECKOUT OWNED_READ_HOSTED_REQUIRED".split())


def require(condition, code):
    if not condition:
        raise paired.BuildError(code)


def failure(error):
    if type(error) is paired.BuildError and str(error) in CODES:
        return {"error_code": str(error), "error_type": "BuildError"}
    return base.fixed_error(error)


def load_inputs(lock, repository=ROOT):
    require(type(lock["schema"]) is int and lock["schema"] == 1
            and lock["kind"] == "independent-windows-owned-read-build-only", "OWNED_READ_RECIPE")
    rows = []
    for key in ("base_recipe", "read_recipe"):
        pin = lock[key]
        require(source.safe_name(pin["path"]), "OWNED_READ_RECIPE")
        rows.append(json.loads(source.checked_bytes(repository / pin["path"], pin, 1024 * 1024)))
    no_packet, read = rows
    base.load_base(no_packet, repository)
    require(lock["base_prepared"] == no_packet["expected_prepared"] and lock["build"] == no_packet["build"], "OWNED_READ_BASE")
    require(lock["admission"] == {"public_startup_allowed": False, "core_runtime_verified": False,
                                  "dependency_license_closure_verified": False}, "OWNED_READ_RECIPE")
    require(read["base_paired_source_tree_sha256"] == no_packet["base_prepared"]["source_tree_sha256"]
            and all(read["base_recipe"][key] == no_packet["base_recipe"][key] for key in ("path", "sha256"))
            and read["feature"] == paired.FEATURE and read["actual_core_executed"] is False
            and read["driver_executed"] is False and read["public_admission"] is False, "OWNED_READ_BASE")
    require(set(lock["expected_prepared"]) == {"source_tree_sha256", "patch_manifest_sha256", "cargo_lock_patched_sha256"}
            and all(type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value) for value in lock["expected_prepared"].values()), "OWNED_READ_RECIPE")
    require(len(read["files"]) == 4 and {row["path"] for row in read["files"]} == FILES, "OWNED_READ_PATCH_SCOPE")
    return no_packet, read


def verify_read(read, repository=ROOT):
    require(type(read["contract_expected_passed"]) is int and read["contract_expected_passed"] == 8
            and len(read["contract_inputs"]) == 4, "OWNED_READ_CONTRACT")
    expected = {"tests/native/windows-owned-read/" + name for name in ("Cargo.toml", "Cargo.lock", "src/lib.rs", "src/main.rs")}
    require({row["path"] for row in read["contract_inputs"]} == expected, "OWNED_READ_CONTRACT")
    for row in read["contract_inputs"]:
        source.checked_bytes(repository / row["path"], row, 1024 * 1024)
    folder = Path(read["patch"]["path"]).parent
    require(source.safe_name(read["patch"]["path"]), "OWNED_READ_RECIPE")
    source.checked_bytes(repository / read["patch"]["path"], read["patch"], 1024 * 1024)
    for row in read["files"]:
        if row["original_sha256"] is None:
            module = repository / folder / "source" / Path(row["path"]).name
            data = module.read_bytes()
            require(len(data) == row["patched_bytes"] and source.sha(data) == row["patched_sha256"], "OWNED_READ_CONTRACT")


def derive(work, prepared, lock, repository=ROOT):
    no_packet, read = load_inputs(lock, repository)
    verify_read(read, repository)
    base.require_prepared(work, prepared)
    require(all(prepared[key] == value for key, value in lock["base_prepared"].items()), "OWNED_READ_BASE")
    expected = copy.deepcopy(prepared["source_files"])
    for row in read["files"]:
        previous = expected["easytier"].get(row["path"])
        require((previous is None if row["original_sha256"] is None else previous is not None and previous["sha256"] == row["original_sha256"]), "OWNED_READ_BASE")
        expected["easytier"][row["path"]] = {"bytes": row["patched_bytes"], "sha256": row["patched_sha256"]}
    paired.apply_patch(work / "easytier-source", read["patch"], repository)
    actual = {"easytier": paired.inventory(work / "easytier-source"), "tun": paired.inventory(work / "tun-source")}
    require(actual == expected, "OWNED_READ_PATCH_SCOPE")
    require(actual["easytier"]["Cargo.lock"]["sha256"] == prepared["cargo_lock_patched_sha256"], "OWNED_READ_BASE")
    patches = [*prepared["patches"], read["patch"]]
    result = {"schema": 1, "kind": "independent-windows-owned-read-source-not-admitted",
              "base_recipe": lock["base_recipe"], "base_prepared": lock["base_prepared"], "read_recipe": lock["read_recipe"],
              "upstream": prepared["upstream"], "tun_upstream": prepared["tun_upstream"], "archives": prepared["archives"],
              "source_files": actual, "source_tree_sha256": source.sha(source.encoded(actual)),
              "patches": patches, "patch_manifest_sha256": source.sha(source.encoded(patches)),
              "cargo_lock_patched_sha256": prepared["cargo_lock_patched_sha256"],
              "omitted_link_input": prepared["omitted_link_input"], "runtime_executed": False,
              "original_archives_preserved": True, "public_startup_allowed": False}
    require(all(result[key] == value for key, value in lock["expected_prepared"].items()), "OWNED_READ_BASE")
    return result


def check_prepared(work, prepared, lock):
    base.require_prepared(work, prepared)
    require(all(prepared[key] == value for key, value in lock["expected_prepared"].items())
            and source.sha(source.encoded(prepared["patches"])) == prepared["patch_manifest_sha256"]
            and prepared["source_files"]["easytier"]["Cargo.lock"]["sha256"] == prepared["cargo_lock_patched_sha256"],
            "OWNED_READ_INPUT_CHANGED")


def delegate_diagnostics(path, prepared):
    """Project bounded, source-bound fixed diagnostics; never raw compiler text."""
    def unique(items):
        row = {}
        for key, value in items:
            if key in row:
                raise ValueError()
            row[key] = value
        return row
    try:
        with path.open("rb") as handle:
            data = handle.read(2 * 1024 * 1024 + 1)
        if len(data) > 2 * 1024 * 1024:
            raise ValueError()
        value = json.loads(data, object_pairs_hook=unique)
        allowed = {"schema", "status", "kind", "source_tree_sha256", "patch_manifest_sha256", "cargo_lock_sha256",
                   "build_inputs", "runs", "runtime_executed", "runtime_binary_uploaded", "public_startup_allowed",
                   "driver_loaded", "dependency_license_closure_verified", "error_code", "error_type", "errno",
                   "returncode", "paired_tun_resolution_workspace_union", "selected_graph", "built_executables"}
        if (type(value) is not dict or not set(value) <= allowed or type(value["schema"]) is not int
                or value["schema"] != 1 or value["kind"] != "independent-windows-no-packet-build-only"
                or value["source_tree_sha256"] != prepared["source_tree_sha256"]
                or value["patch_manifest_sha256"] != prepared["patch_manifest_sha256"]
                or value["cargo_lock_sha256"] != prepared["cargo_lock_patched_sha256"]):
            raise ValueError()
        stages = ["interface-contract", "typed-session-contract", "metadata", "selected-graph", "core-cli-build"]
        runs = value["runs"]
        if type(runs) is not list or len(runs) > len(stages):
            raise ValueError()
        for index, row in enumerate(runs):
            if (type(row) is not dict or set(row) != {"stage", "exit_code", "log_sha256", "rust_codes", "link_codes", "known_source_locations"}
                    or row["stage"] != stages[index] or type(row["exit_code"]) is not int
                    or not -(2**31) <= row["exit_code"] < 2**32 or type(row["log_sha256"]) is not str
                    or not re.fullmatch(r"[0-9a-f]{64}", row["log_sha256"])):
                raise ValueError()
            for key, pattern in (("rust_codes", r"E[0-9]{4}"), ("link_codes", r"LNK[0-9]{4}")):
                codes = row[key]
                if (type(codes) is not list or len(codes) > 24
                        or any(type(code) is not str or not re.fullmatch(pattern, code) for code in codes)):
                    raise ValueError()
            locations = row["known_source_locations"]
            if type(locations) is not list or len(locations) > 24:
                raise ValueError()
            for location in locations:
                if (type(location) is not dict or set(location) != {"tree", "source", "line", "column"}
                        or type(location["tree"]) is not str or type(location["source"]) is not str
                        or location["source"] not in prepared["source_files"].get(location["tree"], {})
                        or any(type(location[key]) is not int or not 0 <= location[key] < 2**31 for key in ("line", "column"))):
                    raise ValueError()
        return runs
    except (OSError, ValueError, KeyError, TypeError, RecursionError):
        return None


def build(work, output, prepared, lock, repository=ROOT, environment=None, cargo="cargo"):
    no_packet, read = load_inputs(lock, repository)
    verify_read(read, repository)
    check_prepared(work, prepared, lock)
    output.mkdir(parents=True, exist_ok=False)
    receipt = {"schema": 1, "status": "building", "kind": lock["kind"], "source_tree_sha256": prepared["source_tree_sha256"],
               "patch_manifest_sha256": prepared["patch_manifest_sha256"], "cargo_lock_sha256": prepared["cargo_lock_patched_sha256"],
               "base_recipe": lock["base_recipe"], "read_recipe": lock["read_recipe"], "runs": [],
               "runtime_executed": False, "runtime_binary_uploaded": False, "public_startup_allowed": False,
               "driver_loaded": False, "dependency_license_closure_verified": False}
    (output / "source-receipt.json").write_bytes(source.encoded(prepared))
    try:
        env = dict(os.environ if environment is None else environment)
        env.update(CARGO_TARGET_DIR=str(work / "target"), CARGO_BUILD_JOBS="2", CARGO_INCREMENTAL="0",
                   CARGO_NET_GIT_FETCH_WITH_CLI="true", GIT_CEILING_DIRECTORIES=str(work),
                   CARGO_ENCODED_RUSTFLAGS="-C\x1ftarget-feature=+crt-static")
        command = [cargo, "+" + lock["build"]["rust_toolchain"], "test", "--locked", "--target", lock["build"]["target"],
                   "--manifest-path", str(repository / "tests/native/windows-owned-read/Cargo.toml"), "--lib", "--", "--test-threads=1"]
        logs = work / "owned-read-contract-logs"
        logs.mkdir(exist_ok=False)
        print("WINDOWS_OWNED_READ_STAGE owned-read-contract", flush=True)
        with (logs / "stdout").open("xb") as stdout, (logs / "stderr").open("xb") as stderr:
            result = subprocess.run(command, cwd=repository, env=env, stdout=stdout, stderr=stderr, timeout=600)
        data = (logs / "stdout").read_bytes() + (logs / "stderr").read_bytes()
        receipt["runs"].append({"stage": "owned-read-contract", "exit_code": result.returncode,
                                "log_sha256": source.sha(data), **paired.safe_diagnostics(data, prepared["source_files"])})
        require(result.returncode == 0, "OWNED_READ_COMMAND")
        paired.linux_build.require_count(data.decode("utf-8", errors="replace"), 8)
        verify_read(read, repository)
        check_prepared(work, prepared, lock)
        # Reuse the independently reviewed no-Packet graph/build/import gates.
        # Its original recipe remains untouched; this wrapper separately binds
        # and rechecks the composed source identity before and after the build.
        compiled = base.build(work, output / "core-build", prepared, no_packet, repository, environment, cargo)
        verify_read(read, repository)
        check_prepared(work, prepared, lock)
        receipt["runs"].extend(compiled["runs"])
        for key in ("built_executables", "selected_graph", "paired_tun_resolution_workspace_union", "build_inputs"):
            receipt[key] = compiled[key]
        receipt["status"] = "windows_owned_read_built_not_executed"
        return receipt
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        receipt.update(status="failed", **failure(error))
        delegated = delegate_diagnostics(output / "core-build/receipt.json", prepared)
        if delegated is not None:
            receipt["runs"] = receipt["runs"][:1] + delegated
            receipt["delegate_diagnostics"] = "verified_fixed_projection"
        else:
            receipt["delegate_diagnostics"] = "unavailable_or_invalid"
        raise
    finally:
        (output / "receipt.json").write_bytes(source.encoded(receipt))


def main():
    require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted", "OWNED_READ_HOSTED_REQUIRED")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    require(commit == os.environ.get("GITHUB_SHA") and not subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT), "OWNED_READ_CHECKOUT")
    protoc = Path(os.environ.get("PROTOC", ""))
    paired.require(protoc.is_file() and subprocess.check_output([str(protoc), "--version"], text=True).strip() == "libprotoc 35.1",
                   "WINDOWS_PINNED_PROTOC_REQUIRED")
    lock = json.loads(LOCK.read_bytes())
    no_packet, _ = load_inputs(lock)
    original = base.load_base(no_packet)
    linux, _ = paired.load_inputs(original)
    work = Path(os.environ["RUNNER_TEMP"]) / "fifth-easytier-windows-owned-read"
    work.mkdir(exist_ok=False)
    output = ROOT / "artifacts/ci/easytier-windows-owned-read"
    output.parent.mkdir(parents=True, exist_ok=True)
    run_path = output.parent / "easytier-windows-owned-read-run.json"
    run = {"schema": 1, "status": "running", "checkout_commit": commit, "run_id": os.environ.get("GITHUB_RUN_ID"),
           "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"), "recipe_sha256": source.sha(LOCK.read_bytes()),
           "runtime_executed": False, "runtime_binary_uploaded": False, "public_startup_allowed": False}
    with run_path.open("xb") as handle:
        handle.write(source.encoded(run))
    try:
        for name in ("easytier-fetch", "tun-fetch"):
            (work / name).mkdir()
        run["stage"] = "fetch-fixed-archives"
        archive = paired.linux_build.git_archive(work / "easytier-fetch", linux)
        tun_archive = paired.linux_build.git_archive(work / "tun-fetch", original["tun"])
        run["stage"] = "prepare-combined-source"
        baseline = paired.prepare(archive, tun_archive, work, original)
        no_packet_tree = base.derive(work, baseline, no_packet)
        prepared = derive(work, no_packet_tree, lock)
        run["stage"] = "contracts-resolution-build"
        result = build(work, output, prepared, lock)
        result.update(checkout_commit=commit, run_id=run["run_id"], run_attempt=run["run_attempt"])
        (output / "receipt.json").write_bytes(source.encoded(result))
        run["status"] = "windows_owned_read_build_verified_not_executed"
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        run.update(status="failed", **failure(error))
        raise
    finally:
        run_path.write_bytes(source.encoded(run))
    print("WINDOWS_OWNED_READ_BUILD_VERIFIED_NO_RUNTIME_EXECUTION")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError):
        print("WINDOWS_OWNED_READ_BUILD_FAILED", file=sys.stderr)
        raise SystemExit(1)
