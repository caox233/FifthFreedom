"""Keep the retained C71 native replay dependency visibly blocked until supplied and reviewed."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat

ROOT = Path(__file__).resolve().parents[1]
TEST_SOURCE = "src/native/client/hooks/src/input_mouse/merc_mouse_sensitivity_tests.rs"
TEST_SOURCE_SHA256 = "a0942f34386a90b7dfbfa95d634e98b0211e4248fc4c18d87ca98dce8552e679"
FEATURE = "native-replay-fixtures"


def assess(root=ROOT, fixture_directory=None):
    source = (root / TEST_SOURCE).read_bytes()
    source_unchanged = hashlib.sha256(source).hexdigest() == TEST_SOURCE_SHA256
    tests = re.findall(rb"#\[test\]\s+fn (c71_[a-z0-9_]+)\(", source)
    supplied = False
    if fixture_directory is not None:
        try:
            # Presence is not provenance or permission to read/execute an
            # arbitrary local file. Never retain the caller's path or contents.
            info = (Path(fixture_directory) / "native_fixture.rs").lstat()
            supplied = stat.S_ISREG(info.st_mode)
        except (OSError, ValueError):
            pass
    return {
        "schema": 1, "status": "blocked_native_replay_inputs" if source_unchanged and len(tests) == 13 else "invalid_test_contract",
        "required_feature": FEATURE, "retained_test_count": len(tests), "retained_test_source_unchanged": source_unchanged,
        "fixture_source_supplied": supplied,
        # No reviewed C71 fixture/native-byte/generator provenance manifest is
        # supplied in this checkout. An untrusted file cannot clear this gate.
        "reviewed_fixture_manifest_supplied": False, "fixture_authenticated": False,
        "missing_contract": "C71 fixture source, native bytes/generator and reviewed provenance manifest",
        "executed_native_replay_tests": 0, "full_native_replay_verified": False,
        "game_executed": False, "complete_package": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", type=Path, default=ROOT / "artifacts/ci/native-replay-inputs/receipt.json")
    args = parser.parse_args()
    result = assess(fixture_directory=os.environ.get("SCBL_R58_C71_FIXTURE"))
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    with args.receipt.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2); stream.write("\n")
    message = "NATIVE_REPLAY_INPUTS_NOT_ADMITTED: 13 retained checks require reviewed C71 fixture source and provenance; no replay executed"
    print(("::error title=Native replay input gate::" if os.environ.get("GITHUB_ACTIONS") == "true" else "CI_ERROR_SUMMARY ") + message)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
