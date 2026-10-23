"""Build the exact paired Windows EasyTier sources, never execute or publish them."""
from __future__ import annotations

import copy
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tarfile
import tomllib

import ci_easytier_build as linux_build
import ci_prepare_easytier_source as source

ROOT = source.ROOT
LOCK = ROOT / "scripts/easytier-windows-source.lock.json"
FEATURE = "ff-windows-session-observer"
OVERRIDE = b'\n[patch."https://github.com/EasyTier/rust-tun"]\ntun-easytier = { path = "../tun-source" }\n'
TUN_SOURCE = "git+https://github.com/EasyTier/rust-tun#12378839e7985283df0e4fb536b7137230356db5"


class BuildError(ValueError):
    pass


def require(condition, code):
    if not condition:
        raise BuildError(code)


def load_inputs(lock, repository=ROOT):
    result = []
    for field in ("linux_recipe", "session_recipe"):
        row = lock[field]
        require(source.safe_name(row["path"]), "WINDOWS_RECIPE_PATH")
        data = (repository / row["path"]).read_bytes()
        require(source.sha(data) == row["sha256"], "WINDOWS_RECIPE_DIGEST")
        result.append(json.loads(data))
    return tuple(result)


def inventory(directory, allowed_prebuilt=None):
    rows = {}
    for path in sorted(directory.rglob("*")):
        info = path.lstat()
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400,
                "WINDOWS_SOURCE_LINK")
        if path.is_dir():
            continue
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_size <= source.MAX_CONTENT,
                "WINDOWS_SOURCE_FILE")
        relative = path.relative_to(directory).as_posix()
        data = path.read_bytes()
        row = {"bytes": len(data), "sha256": source.sha(data)}
        if source.prebuilt(data):
            require(allowed_prebuilt is not None and relative == allowed_prebuilt["path"]
                    and all(row[key] == allowed_prebuilt[key] for key in row), "WINDOWS_UNEXPECTED_BINARY_INPUT")
        # Windows reports executable bits differently; byte identities are portable.
        rows[relative] = row
    return rows


def write_tree(directory, files):
    directory.mkdir(exist_ok=False)
    for name, item in files.items():
        require(source.safe_name(name), "WINDOWS_SOURCE_PATH")
        path = directory / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(item["data"])


def apply_patch(directory, row, repository=ROOT):
    require(source.safe_name(row["path"]), "WINDOWS_PATCH_PATH")
    patch = source.checked_bytes(repository / row["path"], row, 1024 * 1024)
    env = dict(os.environ, GIT_CEILING_DIRECTORIES=str(directory.parent))
    for check in (True, False):
        result = subprocess.run(["git", "-c", "core.autocrlf=false", "-c", "core.eol=lf", "apply",
                                 *(["--check"] if check else []), "--whitespace=error-all", "-"],
                                cwd=directory, env=env, input=patch, capture_output=True, timeout=30)
        require(result.returncode == 0, "WINDOWS_PATCH_REJECTED")


def transform_lock(data):
    parsed = tomllib.loads(data.decode())
    packages = [item for item in parsed["package"] if item["name"] == "tun-easytier"]
    require(len(packages) == 1 and packages[0]["version"] == "1.1.1"
            and packages[0].get("source") == TUN_SOURCE, "WINDOWS_TUN_LOCK_INPUT")
    line = ('source = "' + TUN_SOURCE + '"\n').encode()
    require(data.count(line) == 1, "WINDOWS_TUN_LOCK_AMBIGUOUS")
    changed = data.replace(line, b"", 1)
    expected = copy.deepcopy(parsed)
    next(item for item in expected["package"] if item["name"] == "tun-easytier").pop("source")
    require(tomllib.loads(changed.decode()) == expected, "WINDOWS_UNRELATED_LOCK_CHANGE")
    return changed


def prepare(easytier_archive, tun_archive, work, lock, repository=ROOT):
    work = work.resolve()
    linux, session = load_inputs(lock, repository)
    archive = source.checked_bytes(easytier_archive, linux["archive"])
    files, materialized = source.archive_files(archive, linux)
    tun_bytes = source.checked_bytes(tun_archive, lock["tun"]["archive"])
    tun_files, _ = source.archive_files(tun_bytes, lock["tun"])
    require(source.sha(files["Cargo.lock"]["data"]) == linux["cargo_lock_sha256"], "WINDOWS_BASE_LOCK_DIGEST")
    for notice in linux["required_notices"]:
        require(source.sha(files[notice["path"]]["data"]) == notice["sha256"], "WINDOWS_BASE_NOTICE_DIGEST")
    easytier, tun = work / "easytier-source", work / "tun-source"
    write_tree(easytier, files)
    write_tree(tun, tun_files)
    expected = {"easytier": inventory(easytier), "tun": inventory(tun)}
    for row in linux["modified_files"]:
        require(expected["easytier"][row["path"]]["sha256"] == row["original_sha256"], "WINDOWS_GUARD_BASE")
    for patch in linux["patches"]:
        apply_patch(easytier, patch, repository)
    for row in linux["modified_files"]:
        data = (easytier / row["path"]).read_bytes()
        require(source.sha(data) == row["patched_sha256"], "WINDOWS_GUARD_DIGEST")
        expected["easytier"][row["path"]] = {"bytes": len(data), "sha256": source.sha(data)}
    patch_rows = list(linux["patches"])
    for group in session["groups"]:
        directory = tun if group["name"] == "tun" else easytier
        for row in group["files"]:
            current = expected[group["name"]].get(row["path"])
            require((current["sha256"] if current else None) == row["original_sha256"], "WINDOWS_SESSION_BASE")
        relative = str(Path(lock["session_recipe"]["path"]).parent / group["patch"]).replace("\\", "/")
        patch = {"path": relative, "sha256": group["patch_sha256"],
                 "bytes": (repository / relative).stat().st_size, "role": "windows-session"}
        apply_patch(directory, patch, repository)
        patch_rows.append(patch)
        for row in group["files"]:
            data = (directory / row["path"]).read_bytes()
            require(source.sha(data) == row["patched_sha256"], "WINDOWS_SESSION_DIGEST")
            expected[group["name"]][row["path"]] = {"bytes": len(data), "sha256": source.sha(data)}
    patch = lock["modern_build_patch"]
    require(expected["easytier"][patch["source_path"]]["sha256"] == patch["original_sha256"], "WINDOWS_BUILD_BASE")
    apply_patch(easytier, patch, repository)
    data = (easytier / patch["source_path"]).read_bytes()
    require(source.sha(data) == patch["patched_sha256"], "WINDOWS_BUILD_PATCH_DIGEST")
    expected["easytier"][patch["source_path"]] = {"bytes": len(data), "sha256": source.sha(data)}
    patch_rows.append({key: patch[key] for key in ("path", "bytes", "sha256")})
    require(inventory(easytier) == expected["easytier"] and inventory(tun) == expected["tun"], "WINDOWS_PATCH_SCOPE")

    # This exact held import library is a link input, not a published runtime.
    link = lock["link_input"]
    require(link in linux["omitted_files"], "WINDOWS_LINK_INPUT_NOT_PINNED")
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as bundle:
        data = bundle.extractfile(link["path"]).read()
    require(len(data) == link["bytes"] and source.sha(data) == link["sha256"], "WINDOWS_LINK_INPUT_DIGEST")
    destination = easytier / link["path"]
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    manifest = easytier / "Cargo.toml"
    require(b'[patch."https://github.com/EasyTier/rust-tun"]' not in manifest.read_bytes(), "WINDOWS_DUPLICATE_OVERRIDE")
    manifest.write_bytes(manifest.read_bytes() + OVERRIDE)
    cargo_lock = easytier / "Cargo.lock"
    cargo_lock.write_bytes(transform_lock(cargo_lock.read_bytes()))
    trees = {"easytier": inventory(easytier, link), "tun": inventory(tun)}
    receipt = {"schema": 1, "kind": "paired-windows-source-not-admitted", "upstream": linux["upstream"],
            "tun_upstream": lock["tun"]["upstream"], "archives": [linux["archive"], lock["tun"]["archive"]],
            "patches": patch_rows, "patch_manifest_sha256": source.sha(source.encoded(patch_rows)),
            "source_files": trees, "source_tree_sha256": source.sha(source.encoded(trees)),
            "cargo_lock_original_sha256": linux["cargo_lock_sha256"],
            "cargo_lock_patched_sha256": source.sha(cargo_lock.read_bytes()),
            "cargo_override_sha256": source.sha(OVERRIDE), "materialized_links": materialized,
            "link_only_held_input": link, "runtime_executed": False, "public_startup_allowed": False}
    for field, expected_digest in lock.get("expected_prepared", {}).items():
        require(receipt[field] == expected_digest, "WINDOWS_PREPARED_IDENTITY_MISMATCH")
    return receipt


def require_prepared(work, receipt, lock):
    actual = {"easytier": inventory(work / "easytier-source", lock["link_input"]),
              "tun": inventory(work / "tun-source")}
    require(actual == receipt["source_files"] and source.sha(source.encoded(actual)) == receipt["source_tree_sha256"],
            "WINDOWS_PREPARED_SOURCE_CHANGED")


def require_resolution(metadata, work):
    packages = metadata["packages"]
    candidates = [package for package in packages if package["name"] == "tun-easytier"]
    require(len(candidates) == 1, "WINDOWS_TUN_RESOLUTION_AMBIGUOUS")
    tun = candidates[0]
    require(tun["version"] == "1.1.1" and tun["source"] is None
            and Path(tun["manifest_path"]).resolve() == (work / "tun-source/Cargo.toml").resolve(),
            "WINDOWS_TUN_RESOLUTION_NOT_PAIRED")
    nodes = metadata["resolve"]["nodes"]
    easytier = next(package for package in packages if package["name"] == "easytier")
    require(Path(easytier["manifest_path"]).resolve() == (work / "easytier-source/easytier/Cargo.toml").resolve(),
            "WINDOWS_CALLER_RESOLUTION")
    node = next(node for node in nodes if node["id"] == easytier["id"])
    require(FEATURE in node["features"] and any(dep["name"] == "tun" and dep["pkg"] == tun["id"] for dep in node["deps"]),
            "WINDOWS_CALLER_FEATURE_OR_DEPENDENCY_MISSING")
    tun_node = next(node for node in nodes if node["id"] == tun["id"])
    require("async" in tun_node["features"], "WINDOWS_TUN_ASYNC_MISSING")
    return {"tun_version": tun["version"], "tun_source": "verified-local-paired-tree",
            "caller_features": sorted(node["features"]), "tun_features": sorted(tun_node["features"])}


def pe_identity(data):
    require(len(data) >= 256 and data[:2] == b"MZ", "WINDOWS_OUTPUT_NOT_PE")
    offset = int.from_bytes(data[60:64], "little")
    require(64 <= offset <= len(data) - 26 and data[offset:offset + 4] == b"PE\0\0"
            and int.from_bytes(data[offset + 4:offset + 6], "little") == 0x8664
            and int.from_bytes(data[offset + 24:offset + 26], "little") == 0x20B,
            "WINDOWS_OUTPUT_WRONG_MACHINE")
    return {"bytes": len(data), "sha256": source.sha(data), "machine": "AMD64", "format": "PE32+"}


def safe_diagnostics(data, known_files=None):
    text = data.decode("utf-8", "replace")
    locations = []
    for raw, line, column in re.findall(r"(?m)^\s*-->\s+(.+):(\d+):(\d+)\s*$", text):
        normalized = raw.replace("\\", "/")
        matches = [(tree, path) for tree, files in (known_files or {}).items() for path in files
                   if normalized == path or normalized.endswith("/" + path)]
        if len(matches) == 1 and len(locations) < 24:
            tree, path = matches[0]
            row = {"tree": tree, "source": path, "line": int(line), "column": int(column)}
            if row not in locations:
                locations.append(row)
    return {"rust_codes": sorted(set(re.findall(r"\bE[0-9]{4}\b", text)))[:24],
            "link_codes": sorted(set(re.findall(r"\bLNK[0-9]{4}\b", text)))[:24],
            "known_source_locations": locations}


def build(work, output, prepared, lock, environment=None, cargo="cargo"):
    require_prepared(work, prepared, lock)
    output.mkdir(parents=True, exist_ok=False)
    (output / "source-receipt.json").write_bytes(source.encoded(prepared))
    receipt = {"schema": 1, "status": "building", "source_tree_sha256": prepared["source_tree_sha256"],
               "patch_manifest_sha256": prepared["patch_manifest_sha256"], "build_inputs": lock["build"],
               "cargo_lock_sha256": prepared["cargo_lock_patched_sha256"], "runs": [],
               "runtime_executed": False, "runtime_binary_uploaded": False, "public_startup_allowed": False,
               "driver_loaded": False, "win7_compatibility": False, "dependency_license_closure_verified": False}
    env = dict(os.environ if environment is None else environment)
    env.update(CARGO_TARGET_DIR=str(work / "target"), CARGO_BUILD_JOBS="2", CARGO_INCREMENTAL="0",
               CARGO_NET_GIT_FETCH_WITH_CLI="true", GIT_CEILING_DIRECTORIES=str(work),
               CARGO_ENCODED_RUSTFLAGS="-C\x1ftarget-feature=+crt-static")
    logs = work / "logs"
    logs.mkdir(exist_ok=False)
    base = [cargo, "+" + lock["build"]["rust_toolchain"]]
    commands = [("metadata", base + ["metadata", "--locked", "--format-version=1", "--filter-platform", lock["build"]["target"],
                                     "--manifest-path", "easytier/Cargo.toml", "--features", FEATURE]),
                ("core-cli-build", base + ["build", "--locked", "--release", "--target", lock["build"]["target"],
                                            "-p", "easytier", "--features", FEATURE,
                                            "--bin", "easytier-core", "--bin", "easytier-cli"])]
    try:
        for label, command in commands:
            print("WINDOWS_EASYTIER_STAGE " + label, flush=True)
            out, err = logs / (label + ".stdout"), logs / (label + ".stderr")
            with out.open("xb") as stdout, err.open("xb") as stderr:
                result = subprocess.run(command, cwd=work / "easytier-source", env=env,
                                        stdout=stdout, stderr=stderr, timeout=600 if label == "metadata" else 7200)
            data = out.read_bytes() + err.read_bytes()
            receipt["runs"].append({"stage": label, "exit_code": result.returncode,
                                    "log_sha256": source.sha(data), **safe_diagnostics(data, prepared["source_files"])})
            require(result.returncode == 0, "WINDOWS_BUILD_COMMAND_FAILED")
            if label == "metadata":
                receipt["resolution"] = require_resolution(json.loads(out.read_bytes()), work)
                require_prepared(work, prepared, lock)
            (output / "receipt.json").write_bytes(source.encoded(receipt))
        require_prepared(work, prepared, lock)
        receipt["built_executables"] = {name: pe_identity((work / "target" / lock["build"]["target"] / "release" / name).read_bytes())
                                         for name in lock["build"]["bins"]}
        receipt["status"] = "paired_windows_core_cli_built_not_executed"
        return receipt
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        receipt.update(status="failed", error_code=str(error) if isinstance(error, BuildError) else "WINDOWS_BUILD_FAILED")
        raise
    finally:
        (output / "receipt.json").write_bytes(source.encoded(receipt))


def main():
    require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted", "WINDOWS_HOSTED_RUNNER_REQUIRED")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    require(commit == os.environ.get("GITHUB_SHA") and not subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT), "WINDOWS_CHECKOUT_CHANGED")
    protoc = Path(os.environ.get("PROTOC", ""))
    require(protoc.is_file() and subprocess.check_output([str(protoc), "--version"], text=True).strip() == "libprotoc 35.1",
            "WINDOWS_PINNED_PROTOC_REQUIRED")
    lock = json.loads(LOCK.read_bytes())
    linux, _ = load_inputs(lock)
    work = Path(os.environ["RUNNER_TEMP"]) / "fifth-easytier-windows-build"
    work.mkdir(exist_ok=False)
    output = ROOT / "artifacts/ci/easytier-windows-build"
    output.parent.mkdir(parents=True, exist_ok=True)
    run_path = output.parent / "easytier-windows-build-run.json"
    run = {"schema": 1, "status": "running", "checkout_commit": commit, "run_id": os.environ.get("GITHUB_RUN_ID"),
           "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"), "recipe_sha256": source.sha(LOCK.read_bytes()),
           "runtime_executed": False, "runtime_binary_uploaded": False, "public_startup_allowed": False}
    with run_path.open("xb") as handle:
        handle.write(source.encoded(run))
    try:
        for name in ("easytier-fetch", "tun-fetch"):
            (work / name).mkdir()
        easytier_archive = linux_build.git_archive(work / "easytier-fetch", linux)
        tun_archive = linux_build.git_archive(work / "tun-fetch", lock["tun"])
        prepared = prepare(easytier_archive, tun_archive, work, lock)
        result = build(work, output, prepared, lock)
        result.update(checkout_commit=commit, run_id=run["run_id"], run_attempt=run["run_attempt"])
        (output / "receipt.json").write_bytes(source.encoded(result))
        run["status"] = "paired_windows_build_verified_not_executed"
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        run.update(status="failed", error_code=str(error) if isinstance(error, BuildError) else "WINDOWS_BUILD_FAILED")
        raise
    finally:
        run_path.write_bytes(source.encoded(run))
    print("WINDOWS_EASYTIER_BUILD_VERIFIED_NO_RUNTIME_EXECUTION")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError):
        print("WINDOWS_EASYTIER_BUILD_FAILED", file=sys.stderr)
        raise SystemExit(1)
