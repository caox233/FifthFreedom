"""Independent authenticated-receive GNU candidate; compile/test, never execute core."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys

import ci_easytier_build as build
import ci_prepare_easytier_source as source

LOCK = source.ROOT / "scripts/easytier-authenticated-receive-source.lock.json"
PROFILE = "easytier-authenticated-receive-build-only"
EVIDENCE = "docs/dependency-patches/easytier-2.6.4-authenticated-receive/receipt.json"
CHECKS = (
    ("fifth_freedom_receive_", 15, False),
    ("fifth_freedom_secure_profile_contract", 4, False),
    ("fifth_freedom_ingress_contract", 10, False),
    ("peers::secure_datagram::tests", 9, False),
    ("peers::peer_session::tests", 2, False),
    ("ff_explicit_pin_contract", 3, False),
    ("peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip", 1, True),
)


def load_recipe(path=LOCK, repository=source.ROOT):
    """Require the reviewed extension; never silently select the old candidate."""
    lock = json.loads(path.read_bytes())
    baseline_bytes = (repository / "scripts/easytier-source.lock.json").read_bytes()
    evidence_bytes = (repository / EVIDENCE).read_bytes()
    baseline, evidence = json.loads(baseline_bytes), json.loads(evidence_bytes)
    if (source.sha(baseline_bytes) != evidence["baseline"]["recipe_sha256"]
            or baseline["patches"] != evidence["baseline"]["ordered_patches"]):
        raise build.BuildError("EASYTIER_RECEIVE_BASELINE_MISMATCH")
    expected = copy.deepcopy(baseline)
    expected["kind"] = "fifth-freedom-authenticated-receive-linux-source-inputs-v1"
    expected["baseline_recipe"] = {"path": "scripts/easytier-source.lock.json", "sha256": source.sha(baseline_bytes)}
    expected["authenticated_receive_evidence"] = {"path": EVIDENCE, "sha256": source.sha(evidence_bytes)}
    expected["patches"].extend({**row, "path": str(Path(EVIDENCE).parent / row["path"]).replace("\\", "/")}
                               for row in evidence["patches"])
    changed = {row["path"]: row["tested_sha256"] for row in evidence["source_files"]}
    for row in expected["modified_files"]:
        if row["path"] in changed:
            row["patched_sha256"] = changed.pop(row["path"])
    expected["contract_checks"] = [{"filter": name, "passed": count, "exact": exact} for name, count, exact in CHECKS]
    # The independently prepared tree is pinned by this reviewed recipe. The
    # common builder checks the actual whole-tree inventory against it, before
    # executing any compiler and again after compilation.
    digest = lock.get("expected_source_tree_sha256", "")
    if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
        raise build.BuildError("EASYTIER_RECEIVE_TREE_IDENTITY_INVALID")
    expected["expected_source_tree_sha256"] = digest
    if changed or lock != expected:
        raise build.BuildError("EASYTIER_RECEIVE_RECIPE_MISMATCH")
    return lock


def main():
    load_recipe()
    build.hosted_build(LOCK, PROFILE, checks=CHECKS)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print("EASYTIER_AUTHENTICATED_RECEIVE_BUILD_ONLY_FAILED", file=sys.stderr)
        raise SystemExit(1)
