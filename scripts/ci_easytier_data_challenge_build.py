"""Build the independent current-session Data challenge candidate; never run core."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys

import ci_easytier_authenticated_receive_build as receive
import ci_easytier_build as build
import ci_prepare_easytier_source as source

LOCK = source.ROOT / "scripts/easytier-data-challenge-source.lock.json"
PROFILE = "easytier-data-challenge-build-only"
EVIDENCE = "docs/dependency-patches/easytier-2.6.4-data-challenge/receipt.json"
ADDED_MODULE = "easytier/src/peers/peer_conn_data_challenge.rs"
CHECKS = (("fifth_freedom_data_challenge_", 15, False), *receive.CHECKS)


def expected_recipe(repository=source.ROOT):
    baseline_path = repository / receive.LOCK.relative_to(source.ROOT)
    baseline = receive.load_recipe(baseline_path, repository)
    baseline_bytes = baseline_path.read_bytes()
    evidence_bytes = (repository / EVIDENCE).read_bytes()
    evidence = json.loads(evidence_bytes)
    if (source.sha(baseline_bytes) != evidence["baseline"]["recipe_sha256"]
            or baseline["patches"] != evidence["baseline"]["ordered_patches"]
            or baseline["expected_source_tree_sha256"] != evidence["baseline"]["source_tree_sha256"]
            or baseline["cargo_lock_sha256"] != evidence["baseline"]["cargo_lock_sha256"]):
        raise build.BuildError("EASYTIER_CHALLENGE_BASELINE_MISMATCH")
    expected = copy.deepcopy(baseline)
    expected["kind"] = "fifth-freedom-data-challenge-linux-source-inputs-v1"
    expected["baseline_recipe"] = {"path": baseline_path.relative_to(repository).as_posix(),
                                   "sha256": source.sha(baseline_bytes)}
    expected["data_challenge_evidence"] = {"path": EVIDENCE, "sha256": source.sha(evidence_bytes)}
    expected["patches"].extend({**row, "path": (Path(EVIDENCE).parent / row["path"]).as_posix()}
                               for row in evidence["patches"])
    changed = {row["path"]: row for row in evidence["source_files"]}
    for row in expected["modified_files"]:
        if row["path"] in changed:
            update = changed.pop(row["path"])
            if row["patched_sha256"] != update["baseline_sha256"]:
                raise build.BuildError("EASYTIER_CHALLENGE_BASELINE_MISMATCH")
            row["patched_sha256"] = update["tested_sha256"]
    if set(changed) != {ADDED_MODULE} or changed[ADDED_MODULE]["baseline_sha256"] is not None:
        raise build.BuildError("EASYTIER_CHALLENGE_ADDED_SOURCE_MISMATCH")
    # These are the reviewed new module's actual LF byte length and file mode;
    # its content hash and complete 700-file tree are independently pinned too.
    expected["added_files"] = [{"path": ADDED_MODULE, "bytes": 29810, "mode": 0o644,
                                "sha256": changed[ADDED_MODULE]["tested_sha256"]}]
    expected["contract_checks"] = [{"filter": name, "passed": count, "exact": exact} for name, count, exact in CHECKS]
    expected["expected_source_tree_sha256"] = evidence["patch_integrity"]["tested_source_tree_sha256"]
    return expected


def load_recipe(path=LOCK, repository=source.ROOT):
    lock = json.loads(path.read_bytes())
    if lock != expected_recipe(repository):
        raise build.BuildError("EASYTIER_CHALLENGE_RECIPE_MISMATCH")
    return lock


def main():
    load_recipe()
    build.hosted_build(LOCK, PROFILE, checks=CHECKS)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print("EASYTIER_DATA_CHALLENGE_BUILD_ONLY_FAILED", file=sys.stderr)
        raise SystemExit(1)
