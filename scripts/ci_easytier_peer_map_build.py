"""Independent exact-map challenge GNU candidate; tests/build, never run core."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys

import ci_easytier_data_challenge_build as challenge
import ci_easytier_build as build
import ci_prepare_easytier_source as source

LOCK = source.ROOT / "scripts/easytier-peer-map-source.lock.json"
PROFILE = "easytier-peer-map-build-only"
EVIDENCE = "docs/dependency-patches/easytier-2.6.4-peer-map-challenge/receipt.json"
MODIFIED_FILES = (
    "easytier/src/instance/instance.rs",
    "easytier/src/peers/peer.rs",
    "easytier/src/peers/peer_map.rs",
)
ADDED_MODULE = "easytier/src/peers/peer_map_data_challenge.rs"
CHECKS = (("fifth_freedom_peer_map_challenge_contract", 12, False), *challenge.CHECKS)


def expected_recipe(repository=source.ROOT):
    baseline_path = repository / challenge.LOCK.relative_to(source.ROOT)
    baseline = challenge.load_recipe(baseline_path, repository)
    baseline_bytes = baseline_path.read_bytes()
    evidence_bytes = (repository / EVIDENCE).read_bytes()
    evidence = json.loads(evidence_bytes)
    if (source.sha(baseline_bytes) != evidence["baseline"]["recipe_sha256"]
            or baseline["patches"] != evidence["baseline"]["ordered_patches"]
            or baseline["expected_source_tree_sha256"] != evidence["baseline"]["source_tree_sha256"]
            or baseline["cargo_lock_sha256"] != evidence["baseline"]["cargo_lock_sha256"]):
        raise build.BuildError("EASYTIER_PEER_MAP_BASELINE_MISMATCH")
    changed = {row["path"]: row for row in evidence["source_files"]}
    if (len(changed) != len(evidence["source_files"])
            or set(changed) != {*MODIFIED_FILES, ADDED_MODULE}
            or changed[ADDED_MODULE]["baseline_sha256"] is not None
            or any(changed[name]["baseline_sha256"] is None for name in MODIFIED_FILES)):
        raise build.BuildError("EASYTIER_PEER_MAP_SOURCE_SCOPE_MISMATCH")
    expected = copy.deepcopy(baseline)
    expected["kind"] = "fifth-freedom-peer-map-linux-source-inputs-v1"
    expected["baseline_recipe"] = {"path": baseline_path.relative_to(repository).as_posix(),
                                   "sha256": source.sha(baseline_bytes)}
    expected["peer_map_evidence"] = {"path": EVIDENCE, "sha256": source.sha(evidence_bytes)}
    expected["patches"].extend({**row, "path": (Path(EVIDENCE).parent / row["path"]).as_posix()}
                               for row in evidence["patches"])
    # These three files were unchanged by the prior ten patches. Preserve their
    # exact upstream hashes, in addition to the final whole-tree identity.
    existing = {row["path"] for row in [*expected["modified_files"], *expected["added_files"]]}
    if existing.intersection(changed):
        raise build.BuildError("EASYTIER_PEER_MAP_SOURCE_SCOPE_MISMATCH")
    expected["modified_files"].extend({"path": name,
        "original_sha256": changed[name]["baseline_sha256"],
        "patched_sha256": changed[name]["tested_sha256"]} for name in MODIFIED_FILES)
    # Explicit reviewed LF length/mode: no implicit file admission from a patch.
    expected["added_files"].append({"path": ADDED_MODULE, "bytes": 19984, "mode": 0o644,
                                     "sha256": changed[ADDED_MODULE]["tested_sha256"]})
    expected["contract_checks"] = [{"filter": name, "passed": count, "exact": exact}
                                   for name, count, exact in CHECKS]
    expected["expected_source_tree_sha256"] = evidence["patch_integrity"]["tested_source_tree_sha256"]
    return expected


def load_recipe(path=LOCK, repository=source.ROOT):
    lock = json.loads(path.read_bytes())
    if lock != expected_recipe(repository):
        raise build.BuildError("EASYTIER_PEER_MAP_RECIPE_MISMATCH")
    return lock


def main():
    load_recipe()
    build.hosted_build(LOCK, PROFILE, checks=CHECKS)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print("EASYTIER_PEER_MAP_BUILD_ONLY_FAILED", file=sys.stderr)
        raise SystemExit(1)
