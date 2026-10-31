"""Fail-closed admission for the independent historical Capture build."""
from pathlib import Path
import contextlib
import io
import json
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_capture_reference as build


def valid_output():
    return "\n".join([*("PASS " + name for name in build.CASES),
                       "MSVC_FULL_VER 194435207", "RESULT 17 passed; 0 skipped"]) + "\n"


class CaptureReferenceBuildTests(unittest.TestCase):
    def test_received_source_and_retained_asset_are_both_verified(self):
        result = build.verify_inputs()
        self.assertEqual(result["status"], "reported_source_identity_verified")
        self.assertFalse(result["source_binary_equivalence_verified"])

    def test_actual_reference_source_and_asset_mutations_are_rejected(self):
        paths = (build.SOURCE, Path(build.identity.BINARY_PATH), Path("scripts/local-assets.json"))
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in paths:
                (root / name).parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(build.ROOT / name, root / name)
            self.assertEqual(build.verify_inputs(root)["status"], "reported_source_identity_verified")
            for name in paths[:2]:
                with self.subTest(path=str(name)):
                    target = root / name
                    original = target.read_bytes()
                    target.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
                    with self.assertRaisesRegex(ValueError, "CAPTURE_REFERENCE_INPUT_INVALID"):
                        build.verify_inputs(root)
                    target.write_bytes(original)

    def test_no_hosted_windows_run_can_be_claimed_on_other_hosts(self):
        with patch.object(build.sys, "platform", "linux"):
            with self.assertRaisesRegex(ValueError, "CAPTURE_REFERENCE_HOST_REQUIRED"):
                build.run({}, Path("unused"))

    def test_exact_finite_result_records_the_observed_compiler(self):
        self.assertEqual(build.fixture_result(valid_output()), 194435207)
        self.assertEqual(build.fixture_result(valid_output().replace("194435207", "194435212")), 194435212)

    def test_missing_repeated_reordered_or_failed_case_is_rejected(self):
        good = valid_output().splitlines()
        bad = [good[1:], good[:1] + good, [good[1], good[0], *good[2:]],
               [good[0].replace("PASS", "FAIL"), *good[1:]]]
        for lines in bad:
            with self.subTest(lines=lines[:2]):
                with self.assertRaisesRegex(ValueError, "CAPTURE_REFERENCE_FIXTURE_INVALID"):
                    build.fixture_result("\n".join(lines))

    def test_skips_truncated_summary_or_extra_output_are_rejected(self):
        good = valid_output()
        for output in (good.replace("17 passed; 0 skipped", "16 passed; 1 skipped"),
                       good.replace("RESULT 17 passed; 0 skipped", "RESULT 17 passed"),
                       good + "unexpected output\n"):
            with self.assertRaisesRegex(ValueError, "CAPTURE_REFERENCE_FIXTURE_INVALID"):
                build.fixture_result(output)

    def test_compiler_identity_cannot_contain_arbitrary_log_text(self):
        for value in ("private-path", "194435207 extra", "1944", ""):
            with self.assertRaisesRegex(ValueError, "CAPTURE_REFERENCE_FIXTURE_INVALID"):
                build.fixture_result(valid_output().replace("194435207", value))

    def test_altered_reference_digest_is_not_silently_replaced_by_manifest(self):
        with patch.object(build.identity, "REPORTED_SOURCE_SHA256", "0" * 64):
            with self.assertRaisesRegex(ValueError, "CAPTURE_REFERENCE_INPUT_INVALID"):
                build.verify_inputs()

    def test_main_postcopy_failure_removes_own_images_and_preserves_failure_receipt(self):
        def fail_after_copy(report, output):
            (output / "CaptureReference.exe").write_bytes(b"unverified candidate")
            (output / ".CaptureReference.exe.pending").write_bytes(b"unfinished copy")
            (output / "unrelated-evidence.txt").write_text("preserve")
            report["compilations"] = {"reference": {"exit_code": 0, "log_sha256": "a" * 64}}
            raise ValueError("CAPTURE_REFERENCE_IMAGE_CHANGED")

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(build, "ROOT", root), patch.object(build, "run", fail_after_copy), \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(build.main(), 1)
            output = root / "artifacts/ci/capture-reference"
            self.assertFalse((output / "CaptureReference.exe").exists())
            self.assertFalse((output / ".CaptureReference.exe.pending").exists())
            self.assertEqual((output / "unrelated-evidence.txt").read_text(), "preserve")
            report = json.loads((output / "receipt.json").read_text())
            self.assertFalse(report["passed"])
            self.assertEqual(report["error"], "CAPTURE_REFERENCE_IMAGE_CHANGED")
            self.assertTrue(report["failed_output_cleanup_complete"])
            self.assertEqual(report["compilations"]["reference"]["log_sha256"], "a" * 64)
            sums = (output / "SHA256SUMS.txt").read_text().splitlines()
            self.assertEqual(sums, [build.sha((output / "receipt.json").read_bytes()) + "  receipt.json"])


if __name__ == "__main__":
    unittest.main()
