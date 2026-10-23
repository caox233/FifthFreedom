"""Build reviewed distinct Linux EasyTier sources; export receipts, never binaries."""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import subprocess
import sys

import ci_prepare_easytier_source as source

ROOT = source.ROOT
CHECKS = (
    ("fifth_freedom_secure_profile_contract", 4, False),
    ("fifth_freedom_ingress_contract", 10, False),
    ("peers::secure_datagram::tests", 9, False),
    ("peers::peer_session::tests", 2, False),
    ("peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip", 1, True),
)


class BuildError(ValueError):
    pass


def require_count(text, expected):
    rows = re.findall(r"^test result: ok\. (\d+) passed; (\d+) failed; (\d+) ignored;", text, re.MULTILINE)
    if rows != [(str(expected), "0", "0")]:
        raise BuildError("EASYTIER_REQUIRED_CONTRACT_COUNT_MISMATCH")


def require_source(directory, lock):
    receipt = json.loads((directory / "FIFTH-FREEDOM-SOURCE-RECEIPT.json").read_bytes())
    actual = source.scan(directory)
    actual.pop("FIFTH-FREEDOM-SOURCE-RECEIPT.json")
    if (actual != receipt["source_files"] or source.sha(source.encoded(actual)) != receipt["source_tree_sha256"]
            or receipt["patches"] != lock["patches"] or receipt["upstream"] != lock["upstream"]
            or receipt["original_archive"] != lock["archive"]
            or receipt["patch_manifest_sha256"] != source.sha(source.encoded(lock["patches"]))
            or receipt["cargo_lock_sha256"] != lock["cargo_lock_sha256"]):
        raise BuildError("EASYTIER_PREPARED_SOURCE_CHANGED")
    return receipt


def git_archive(work, lock):
    checkout = work / "original-checkout"
    checkout.mkdir()
    commands = (["git", "init", "--quiet"],
                ["git", "remote", "add", "origin", lock["upstream"]["repository"]],
                ["git", "fetch", "--depth=1", "origin", lock["upstream"]["commit"]])
    for args in commands:
        if subprocess.run(args, cwd=checkout, capture_output=True, timeout=600).returncode:
            raise BuildError("EASYTIER_FIXED_SOURCE_FETCH_FAILED")
    # Archive Git objects directly: checking out the held Windows inputs would
    # needlessly invoke any installed Git LFS smudge filter on the runner.
    for expression, expected in (("FETCH_HEAD", lock["upstream"]["commit"]), ("FETCH_HEAD^{tree}", lock["upstream"]["tree"])):
        actual = subprocess.check_output(["git", "rev-parse", expression], cwd=checkout, text=True).strip()
        if actual != expected:
            raise BuildError("EASYTIER_UPSTREAM_IDENTITY_MISMATCH")
    archive = work / "original.tar"
    subprocess.run(["git", "archive", "--format=tar", "--output", str(archive), "FETCH_HEAD"], cwd=checkout, check=True, capture_output=True)
    source.checked_bytes(archive, lock["archive"])
    return archive


def build_source(directory, target, output, lock, cargo="cargo", environment=None):
    prepared = require_source(directory, lock)
    if target.resolve().is_relative_to(directory.resolve()):
        raise BuildError("EASYTIER_TARGET_INSIDE_SOURCE")
    output.mkdir(parents=True, exist_ok=False)
    (output / "source-receipt.json").write_bytes(source.encoded(prepared))
    receipt = {"schema": 1, "status": "building", "source_tree_sha256": prepared["source_tree_sha256"],
        "patch_manifest_sha256": prepared["patch_manifest_sha256"], "upstream": lock["upstream"],
        "source_archive_sha256": lock["archive"]["sha256"], "cargo_lock_sha256": lock["cargo_lock_sha256"],
        "build_inputs": lock["build"], "admission": lock["admission"], "runs": [],
        "dependency_source_closure_verified": False, "runtime_executed": False,
        "runtime_binary_uploaded": False, "official_binary_reproduced": False, "public_startup_allowed": False}
    env = dict(os.environ if environment is None else environment)
    env.update(CARGO_TARGET_DIR=str(target), CARGO_BUILD_JOBS="2", CARGO_PROFILE_DEV_DEBUG="0",
               CARGO_PROFILE_TEST_DEBUG="0", CARGO_ENCODED_RUSTFLAGS="", CARGO_NET_GIT_FETCH_WITH_CLI="true",
               GIT_CEILING_DIRECTORIES=str(directory.parent), SOURCE_DATE_EPOCH=str(lock["upstream"]["source_date_epoch"]))
    base = [cargo, "+" + lock["build"]["rust_toolchain"]]
    common = ["--locked", "--target", lock["build"]["target"], "-p", "easytier"]
    commands = [(selection, base + ["test", *common, "--lib", selection, "--", *(["--exact"] if exact else [])], count)
                for selection, count, exact in CHECKS]
    commands.append(("release-build", base + ["build", *common, "--release", "--bin", "easytier-core", "--bin", "easytier-cli"], None))
    # Raw compiler logs stay beside the temporary target and are not uploaded.
    logs = target.parent / (target.name + "-logs")
    logs.mkdir(exist_ok=False)
    try:
        for index, (label, command, count) in enumerate(commands):
            print("EASYTIER_BUILD_STAGE " + label, flush=True)
            log = logs / (str(index) + ".log")
            with log.open("xb") as stream:
                result = subprocess.run(command, cwd=directory, env=env, stdout=stream, stderr=subprocess.STDOUT, timeout=5400)
            data = log.read_bytes()
            row = {"stage": label, "exit_code": result.returncode, "log_sha256": source.sha(data)}
            receipt["runs"].append(row)
            if result.returncode:
                raise BuildError("EASYTIER_BUILD_COMMAND_FAILED")
            if count is not None:
                require_count(data.decode("utf-8", errors="replace"), count)
                row.update(passed=count, failed=0, ignored=0)
            (output / "receipt.json").write_bytes(source.encoded(receipt))
        require_source(directory, lock)
        binaries = {}
        for name in lock["build"]["bins"]:
            path = target / lock["build"]["target"] / "release" / name
            data = path.read_bytes()
            if data[:6] != b"\x7fELF\x02\x01" or int.from_bytes(data[18:20], "little") != 62:
                raise BuildError("EASYTIER_COMPILED_PLATFORM_MISMATCH")
            binaries[name] = {"bytes": len(data), "sha256": source.sha(data)}
        receipt.update(status="built_and_contracts_verified_not_admitted", built_executables=binaries)
        return receipt
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        receipt.update(status="failed", error_code=str(error) if isinstance(error, BuildError) else "EASYTIER_BUILD_FAILED")
        raise
    finally:
        (output / "receipt.json").write_bytes(source.encoded(receipt))


def main():
    if sys.platform != "linux" or os.environ.get("GITHUB_ACTIONS") != "true":
        raise BuildError("EASYTIER_HOSTED_LINUX_REQUIRED")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if commit != os.environ.get("GITHUB_SHA") or subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT):
        raise BuildError("EASYTIER_CHECKOUT_IDENTITY_MISMATCH")
    lock = json.loads(source.LOCK.read_bytes())
    work = Path(os.environ["RUNNER_TEMP"]) / "fifth-easytier-build-only"
    work.mkdir(exist_ok=False)
    output = ROOT / "artifacts/ci/easytier-build-only"
    run_path = output.parent / "easytier-build-only-run.json"
    run_path.parent.mkdir(parents=True, exist_ok=True)
    run = {"schema": 1, "checkout_commit": commit, "run_id": os.environ.get("GITHUB_RUN_ID"),
           "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"), "status": "running",
           "source_lock_sha256": source.sha(source.LOCK.read_bytes()), "runtime_executed": False,
           "runtime_binary_uploaded": False, "public_startup_allowed": False}
    with run_path.open("xb") as handle:
        handle.write(source.encoded(run))
    try:
        archive = git_archive(work, lock)
        directory = work / "source"
        source.prepare(archive, directory, lock)
        result = build_source(directory, work / "target", output, lock)
        result.update(checkout_commit=commit, run_id=run["run_id"], run_attempt=run["run_attempt"])
        (output / "receipt.json").write_bytes(source.encoded(result))
        run["status"] = "build_only_verified"
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        run["status"] = "failed"
        raise
    finally:
        run_path.write_bytes(source.encoded(run))
    print("EASYTIER_BUILD_ONLY_VERIFIED_NO_RUNTIME_PUBLICATION", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print("EASYTIER_BUILD_ONLY_FAILED", file=sys.stderr)
        raise SystemExit(1)
