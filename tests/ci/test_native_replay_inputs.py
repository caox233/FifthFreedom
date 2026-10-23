"""A missing or merely present unreviewed native fixture never clears admission."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_native_replay_inputs as gate


class NativeReplayInputsTests(unittest.TestCase):
    def test_retained_real_contract_missing_inputs_remains_explicit_failure(self):
        result = gate.assess()
        self.assertEqual(result["status"], "blocked_native_replay_inputs")
        self.assertEqual(result["retained_test_count"], 13)
        self.assertTrue(result["retained_test_source_unchanged"])
        self.assertFalse(result["fixture_source_supplied"])
        self.assertFalse(result["full_native_replay_verified"])
        self.assertEqual(result["executed_native_replay_tests"], 0)

    def test_arbitrary_file_presence_does_not_authenticate_or_export_input(self):
        with tempfile.TemporaryDirectory(prefix="PRIVATE-unreviewed-fixture-") as directory:
            (Path(directory) / "native_fixture.rs").write_text("PRIVATE_SYNTHETIC_INPUT_NOT_EXECUTABLE")
            result = gate.assess(fixture_directory=directory)
        self.assertTrue(result["fixture_source_supplied"])
        self.assertFalse(result["reviewed_fixture_manifest_supplied"])
        self.assertFalse(result["fixture_authenticated"])
        self.assertEqual(result["status"], "blocked_native_replay_inputs")
        self.assertNotIn("PRIVATE", json.dumps(result))

    def test_changed_retained_test_source_is_not_reclassified_as_held(self):
        with tempfile.TemporaryDirectory(prefix="ff-replay-contract-") as directory:
            root = Path(directory)
            path = root / gate.TEST_SOURCE
            path.parent.mkdir(parents=True)
            path.write_bytes((gate.ROOT / gate.TEST_SOURCE).read_bytes() + b"\n// synthetic mutation\n")
            result = gate.assess(root)
        self.assertEqual(result["status"], "invalid_test_contract")
        self.assertFalse(result["retained_test_source_unchanged"])
        self.assertFalse(result["full_native_replay_verified"])
