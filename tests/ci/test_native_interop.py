"""Synthetic Cargo JSON is not a compiled or executed Windows IPC probe."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_native_interop as interop


class CargoInteropIdentityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-cargo-interop-metadata-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.target = self.root / "target"
        self.target.mkdir()
        self.exe = self.target / "hooks-0123ab.exe"
        self.exe.write_bytes(b"SYNTHETIC INERT METADATA FIXTURE; NEVER EXECUTED")

    def rows(self, **changes):
        artifact = {"reason": "compiler-artifact", "target": {"name": "hooks"},
                    "profile": {"test": True}, "executable": str(self.exe)}
        artifact.update(changes)
        return [artifact, {"reason": "build-finished", "success": True}]

    def parse(self, rows):
        return interop.test_executable("\n".join(json.dumps(row) for row in rows), self.target)

    def test_only_current_cargo_artifact_is_selected_despite_other_existing_executables(self):
        (self.target / "hooks-ff.exe").write_bytes(b"newer unrelated inert fixture")
        self.assertEqual(self.parse(self.rows()), self.exe.resolve())

    def test_missing_failed_and_duplicate_build_or_artifact_records_fail(self):
        valid = self.rows()
        for rows in ([], valid[:1], valid[1:], valid + valid[:1], valid + valid[1:],
                     [valid[0], {"reason": "build-finished", "success": False}],
                     [valid[0], {"reason": "build-finished", "success": 1}]):
            with self.subTest(rows=rows), self.assertRaisesRegex(ValueError, "ARTIFACT_INVALID"):
                self.parse(rows)

    def test_wrong_target_non_test_or_dynamic_path_cannot_select_an_executable(self):
        outside = self.root / "hooks-ab.exe"
        outside.write_bytes(b"outside inert fixture")
        for changes in ({"target": {"name": "other"}}, {"profile": {"test": False}},
                        {"profile": {"test": 1}}, {"executable": str(outside)},
                        {"executable": "hooks-0123ab.exe"}, {"executable": str(self.target / "missing.exe")},
                        {"executable": None}, {"target": []}):
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, "ARTIFACT_INVALID"):
                self.parse(self.rows(**changes))

    def test_malformed_duplicate_json_keys_and_non_objects_are_rejected_without_raw_text(self):
        for text in ('PRIVATE_CARGO_TEXT', '[]', '{"reason":"PRIVATE","reason":"build-finished"}'):
            with self.subTest(text=text), self.assertRaisesRegex(ValueError, "^NATIVE_INTEROP_CARGO_ARTIFACT_INVALID$"):
                interop.test_executable(text, self.target)

    def test_result_requires_three_passes_one_exact_summary_and_no_skips(self):
        valid = "PASS first\nPASS second\nPASS third\n" + interop.RESULT + "\n"
        self.assertEqual(interop.require_summary(valid), 3)
        for text in ("", interop.RESULT, valid + interop.RESULT, valid + "FAIL synthetic\n",
                     valid + "SKIP synthetic\n", valid.replace("PASS first\n", ""),
                     valid.replace("RESULT 3", "RESULT 2")):
            with self.subTest(text=text), self.assertRaisesRegex(ValueError, "RESULT_INVALID"):
                interop.require_summary(text)


if __name__ == "__main__":
    unittest.main()
