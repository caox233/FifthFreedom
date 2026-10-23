"""Independent Windows core/CLI candidate: no Packet import, no runtime execution."""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import re
import subprocess
import sys

import ci_easytier_windows_build as paired

source = paired.source
ROOT = paired.ROOT
LOCK = ROOT / "scripts/easytier-windows-no-packet.lock.json"
FEATURES = frozenset("wireguard websocket smoltcp tun socks5 kcp quic magic-dns zstd ff-windows-session-observer".split())
FORBIDDEN = frozenset({"pnet_datalink", "pnet_sys", "pnet_transport"})
CODES = frozenset("NO_PACKET_RECIPE NO_PACKET_BASE NO_PACKET_PATCH_SCOPE NO_PACKET_MODULE NO_PACKET_SOURCE_CHANGED NO_PACKET_GRAPH NO_PACKET_FEATURES NO_PACKET_CAPTURE_DEPENDENCY NO_PACKET_IMPORT NO_PACKET_COMMAND NO_PACKET_CHECKOUT NO_PACKET_HOSTED_REQUIRED".split())


def require(condition, code):
    if not condition:
        raise paired.BuildError(code)


def fixed_error(error):
    if type(error) is paired.BuildError and str(error) in CODES:
        return {"error_code": str(error), "error_type": "BuildError"}
    return paired.failure_fields(error)


def load_base(lock, repository=ROOT):
    require(lock["schema"] == 1 and lock["kind"] == "independent-windows-no-packet-build-only"
            and lock["build"]["target"] == "x86_64-pc-windows-msvc"
            and lock["build"]["no_default_features"] is True
            and set(lock["build"]["features"]) == FEATURES
            and len(lock["build"]["features"]) == len(FEATURES)
            and lock["build"]["execute_binaries"] is False
            and lock["build"]["upload_binaries"] is False, "NO_PACKET_RECIPE")
    require(set(lock["expected_prepared"]) == {"source_tree_sha256", "patch_manifest_sha256", "cargo_lock_patched_sha256"}
            and all(type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value) for value in lock["expected_prepared"].values()),
            "NO_PACKET_RECIPE")
    row = lock["base_recipe"]
    require(source.safe_name(row["path"]), "NO_PACKET_RECIPE")
    base = json.loads(source.checked_bytes(repository / row["path"], row, 1024 * 1024))
    require(lock["base_prepared"] == base["expected_prepared"]
            and lock["omitted_link_input"] == base["link_input"], "NO_PACKET_BASE")
    return base


def verify_contracts(lock, repository=ROOT):
    tests = lock["contract_tests"]
    require(type(tests["expected_passed"]) is int and tests["expected_passed"] == 8
            and tests["manifest"] in [item["path"] for item in tests["files"]], "NO_PACKET_RECIPE")
    for item in [lock["linked_module"], *tests["files"]]:
        require(source.safe_name(item["path"]), "NO_PACKET_RECIPE")
        source.checked_bytes(repository / item["path"], item, 1024 * 1024)
    return tests


def derive(work, prepared, lock, repository=ROOT):
    base = load_base(lock, repository)
    verify_contracts(lock, repository)
    paired.require_prepared(work, prepared, base)
    require(all(prepared[key] == value for key, value in lock["base_prepared"].items()), "NO_PACKET_BASE")
    expected = copy.deepcopy(prepared["source_files"])
    require(1 <= len(lock["files"]) <= 8 and len({row["path"] for row in lock["files"]}) == len(lock["files"]),
            "NO_PACKET_PATCH_SCOPE")
    for row in lock["files"]:
        require(source.safe_name(row["path"]) and expected["easytier"].get(row["path"]) == row["before"],
                "NO_PACKET_BASE")
        expected["easytier"][row["path"]] = row["after"]
    omitted = lock["omitted_link_input"]
    require(expected["easytier"].pop(omitted["path"]) == {key: omitted[key] for key in ("bytes", "sha256")},
            "NO_PACKET_BASE")
    paired.apply_patch(work / "easytier-source", lock["patch"], repository)
    # Delete only the verified derived copy. The canonical archive and old paired tree remain untouched.
    (work / "easytier-source" / omitted["path"]).unlink()
    actual = {"easytier": paired.inventory(work / "easytier-source"), "tun": paired.inventory(work / "tun-source")}
    require(actual == expected, "NO_PACKET_PATCH_SCOPE")
    module = lock["linked_module"]
    require(actual["easytier"][module["production_path"]] == {key: module[key] for key in ("bytes", "sha256")},
            "NO_PACKET_MODULE")
    patches = [*prepared["patches"], lock["patch"]]
    receipt = {"schema": 1, "kind": "independent-windows-no-packet-source-not-admitted",
            "base_recipe": lock["base_recipe"], "base_prepared": lock["base_prepared"],
            "upstream": prepared["upstream"], "tun_upstream": prepared["tun_upstream"], "archives": prepared["archives"],
            "patches": patches, "patch_manifest_sha256": source.sha(source.encoded(patches)),
            "source_files": actual, "source_tree_sha256": source.sha(source.encoded(actual)),
            "cargo_lock_patched_sha256": actual["easytier"]["Cargo.lock"]["sha256"],
            "omitted_link_input": omitted, "original_archives_preserved": True,
            "runtime_executed": False, "public_startup_allowed": False}
    require(all(receipt[key] == value for key, value in lock["expected_prepared"].items()), "NO_PACKET_BASE")
    return receipt


def require_prepared(work, prepared):
    actual = {"easytier": paired.inventory(work / "easytier-source"), "tun": paired.inventory(work / "tun-source")}
    require(actual == prepared["source_files"] and source.sha(source.encoded(actual)) == prepared["source_tree_sha256"],
            "NO_PACKET_SOURCE_CHANGED")


def selected_graph(data):
    """Check Cargo's selected normal/build graph, never metadata's workspace feature union."""
    require(type(data) is bytes and 0 < len(data) <= 8 * 1024 * 1024, "NO_PACKET_GRAPH")
    try:
        lines = data.decode("utf-8").splitlines()
    except UnicodeDecodeError:
        raise paired.BuildError("NO_PACKET_GRAPH") from None
    require(0 < len(lines) <= 30000, "NO_PACKET_GRAPH")
    nodes = {}
    for line in lines:
        match = re.fullmatch(r"([A-Za-z0-9_-]+) v([0-9][A-Za-z0-9.+-]*)(?: \([^|\r\n]*\))?\|([A-Za-z0-9_,+./-]*)(?: \(\*\))?", line)
        require(match is not None, "NO_PACKET_GRAPH")
        name, version, raw_features = match.groups()
        features = sorted(raw_features.split(",")) if raw_features else []
        require(len(features) == len(set(features)), "NO_PACKET_GRAPH")
        row = {"name": name, "version": version, "features": features}
        key = (name, version, tuple(features))
        require(name not in FORBIDDEN, "NO_PACKET_CAPTURE_DEPENDENCY")
        nodes[key] = row
    easytier = [row for row in nodes.values() if row["name"] == "easytier"]
    require(len(easytier) == 1 and easytier[0]["version"] == "2.6.4"
            and set(easytier[0]["features"]) == FEATURES and lines[0].startswith("easytier v2.6.4 "),
            "NO_PACKET_FEATURES")
    for name, features in (("pnet", {"serde"}), ("pnet_base", {"serde", "std"})):
        rows = [row for row in nodes.values() if row["name"] == name]
        require(len(rows) == 1 and rows[0]["version"] == "0.35.0" and set(rows[0]["features"]) == features,
                "NO_PACKET_FEATURES")
    versions = {(row["name"], row["version"]) for row in nodes.values()}
    require(("pnet_packet", "0.35.0") in versions and ("ipnetwork", "0.20.0") in versions
            and ("network-interface", "2.0.5") in versions, "NO_PACKET_GRAPH")
    rows = [nodes[key] for key in sorted(nodes)]
    return {"target": "x86_64-pc-windows-msvc", "edges": ["normal", "build"],
            "package": "easytier", "default_features": False, "features": sorted(FEATURES),
            "selected_nodes": rows, "selected_nodes_sha256": source.sha(source.encoded(rows)),
            "capture_dependencies": [], "metadata_workspace_union_is_not_selected_graph": True}


def inspect_without_packet(data):
    identity = paired.inspect_executable(data)
    require(all("packet.dll" not in identity["imports"][kind] for kind in ("normal", "delay")), "NO_PACKET_IMPORT")
    return identity


def build(work, output, prepared, lock, repository=ROOT, environment=None, cargo="cargo"):
    base_lock = load_base(lock, repository)
    tests = verify_contracts(lock, repository)
    typed = paired.verify_session_read_contract_inputs(base_lock, repository)
    require_prepared(work, prepared)
    output.mkdir(parents=True, exist_ok=False)
    (output / "source-receipt.json").write_bytes(source.encoded(prepared))
    receipt = {"schema": 1, "status": "building", "kind": lock["kind"], "source_tree_sha256": prepared["source_tree_sha256"],
               "patch_manifest_sha256": prepared["patch_manifest_sha256"], "cargo_lock_sha256": prepared["cargo_lock_patched_sha256"],
               "build_inputs": lock["build"], "runs": [], "runtime_executed": False, "runtime_binary_uploaded": False,
               "public_startup_allowed": False, "driver_loaded": False, "dependency_license_closure_verified": False}
    env = dict(os.environ if environment is None else environment)
    env.update(CARGO_TARGET_DIR=str(work / "target"), CARGO_BUILD_JOBS="2", CARGO_INCREMENTAL="0",
               CARGO_NET_GIT_FETCH_WITH_CLI="true", GIT_CEILING_DIRECTORIES=str(work),
               CARGO_ENCODED_RUSTFLAGS="-C\x1ftarget-feature=+crt-static")
    logs = work / "logs"
    logs.mkdir(exist_ok=False)
    cargo_base = [cargo, "+" + lock["build"]["rust_toolchain"]]
    selection = ["--no-default-features", "--features", ",".join(lock["build"]["features"])]
    target = lock["build"]["target"]
    commands = [("interface-contract", cargo_base + ["test", "--locked", "--target", target, "--manifest-path",
                 str(repository / tests["manifest"]), "--lib", "--", "--test-threads=1"]),
                ("typed-session-contract", cargo_base + ["test", "--locked", "--target", target, "--manifest-path",
                 str(repository / typed["manifest"]), "--lib", "--", "--test-threads=1"]),
                ("metadata", cargo_base + ["metadata", "--locked", "--format-version=1", "--filter-platform", target,
                 "--manifest-path", "easytier/Cargo.toml", *selection]),
                ("selected-graph", cargo_base + ["tree", "--locked", "-p", "easytier", "--target", target, *selection,
                 "-e", "normal,build", "--prefix", "none", "--format", "{p}|{f}"]),
                ("core-cli-build", cargo_base + ["build", "--locked", "--release", "--target", target, "-p", "easytier",
                 *selection, "--bin", "easytier-core", "--bin", "easytier-cli"])]
    try:
        for label, command in commands:
            print("WINDOWS_NO_PACKET_STAGE " + label, flush=True)
            out, err = logs / (label + ".stdout"), logs / (label + ".stderr")
            with out.open("xb") as stdout, err.open("xb") as stderr:
                result = subprocess.run(command, cwd=work / "easytier-source", env=env, stdout=stdout, stderr=stderr,
                                        timeout=7200 if label == "core-cli-build" else 600)
            data = out.read_bytes() + err.read_bytes()
            receipt["runs"].append({"stage": label, "exit_code": result.returncode, "log_sha256": source.sha(data),
                                    **paired.safe_diagnostics(data, prepared["source_files"])})
            require(result.returncode == 0, "NO_PACKET_COMMAND")
            if label.endswith("-contract"):
                paired.linux_build.require_count(data.decode("utf-8", errors="replace"), 8)
                verify_contracts(lock, repository)
                paired.verify_session_read_contract_inputs(base_lock, repository)
            elif label == "metadata":
                receipt["paired_tun_resolution_workspace_union"] = paired.require_resolution(json.loads(out.read_bytes()), work)
            elif label == "selected-graph":
                receipt["selected_graph"] = selected_graph(out.read_bytes())
            require_prepared(work, prepared)
            (output / "receipt.json").write_bytes(source.encoded(receipt))
        receipt["built_executables"] = {name: inspect_without_packet((work / "target" / target / "release" / name).read_bytes())
                                         for name in lock["build"]["bins"]}
        receipt["status"] = "windows_no_packet_built_not_executed"
        return receipt
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        receipt.update(status="failed", **fixed_error(error))
        raise
    finally:
        (output / "receipt.json").write_bytes(source.encoded(receipt))


def main():
    require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted", "NO_PACKET_HOSTED_REQUIRED")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    require(commit == os.environ.get("GITHUB_SHA") and not subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT), "NO_PACKET_CHECKOUT")
    protoc = Path(os.environ.get("PROTOC", ""))
    paired.require(protoc.is_file() and subprocess.check_output([str(protoc), "--version"], text=True).strip() == "libprotoc 35.1",
                   "WINDOWS_PINNED_PROTOC_REQUIRED")
    lock = json.loads(LOCK.read_bytes())
    base_lock = load_base(lock)
    linux, _ = paired.load_inputs(base_lock)
    work = Path(os.environ["RUNNER_TEMP"]) / "fifth-easytier-windows-no-packet"
    work.mkdir(exist_ok=False)
    output = ROOT / "artifacts/ci/easytier-windows-no-packet"
    output.parent.mkdir(parents=True, exist_ok=True)
    run_path = output.parent / "easytier-windows-no-packet-run.json"
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
        tun_archive = paired.linux_build.git_archive(work / "tun-fetch", base_lock["tun"])
        run["stage"] = "prepare-independent-source"
        baseline = paired.prepare(archive, tun_archive, work, base_lock)
        prepared = derive(work, baseline, lock)
        run["stage"] = "contracts-resolution-build"
        result = build(work, output, prepared, lock)
        result.update(checkout_commit=commit, run_id=run["run_id"], run_attempt=run["run_attempt"])
        (output / "receipt.json").write_bytes(source.encoded(result))
        run["status"] = "windows_no_packet_build_verified_not_executed"
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        run.update(status="failed", **fixed_error(error))
        raise
    finally:
        run_path.write_bytes(source.encoded(run))
    print("WINDOWS_NO_PACKET_BUILD_VERIFIED_NO_RUNTIME_EXECUTION")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError):
        print("WINDOWS_NO_PACKET_BUILD_FAILED", file=sys.stderr)
        raise SystemExit(1)
