"""Finite adapter boundary tests; inert files never execute a Windows helper."""
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_owned_read as probe
import ci_diagnostics


def managed(interop=False):
    count = 4 if interop else 22
    return "\n".join([f"PASS case {i}" for i in range(count)] + [probe.INTEROP_SUMMARY if interop else probe.NORMAL_SUMMARY])


def native():
    return "\n".join(["test tests::" + case + " ... ok" for case in probe.NATIVE_CASES]
                     + ["test result: ok. 8 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s"])


def cargo(helper, manifest):
    return [{"reason": "compiler-artifact", "target": {"name": probe.BINARY, "kind": ["bin"]},
             "profile": {"test": False}, "executable": str(helper), "manifest_path": str(manifest)},
            {"reason": "build-finished", "success": True}]


class OwnedReadAdapterTests(unittest.TestCase):
    def test_suite_counts_summaries_failures_and_skips_are_separate(self):
        for interop in (False, True):
            text = managed(interop)
            self.assertTrue(probe.managed_output(text, interop=interop))
            for bad in (text.replace("PASS case 0", "SKIP case 0"), text + "\nFAIL hidden",
                        text + "\n" + (probe.INTEROP_SUMMARY if interop else probe.NORMAL_SUMMARY),
                        text + "\nCI_MANAGED_FAILURE suite=OwnedCoreRead case=22 exception=IOException",
                        text.replace("PASS case 0\n", ""), text.replace("0 skipped", "1 skipped"),
                        managed(not interop), text.replace("22 passed", "19 passed")):
                if bad == text:
                    continue
                self.assertFalse(probe.managed_output(bad, interop=interop))

    def test_native_requires_eight_exact_unique_contracts_and_no_ignored(self):
        valid = native()
        self.assertTrue(probe.native_output(valid))
        for bad in (valid.replace("0 ignored", "1 ignored"), valid + "\n" + valid,
                    valid.replace(probe.NATIVE_CASES[0], probe.NATIVE_CASES[1]),
                    valid.replace(" ... ok", " ... FAILED", 1), valid.replace("8 passed", "0 passed")):
            self.assertFalse(probe.native_output(bad))

    def test_native_failure_diagnostics_only_name_source_defined_tests(self):
        known = probe.NATIVE_CASES[0]
        text = ("test tests::" + known + " ... FAILED\n"
                + "test tests::PRIVATE_NAME ... FAILED\n"
                + "test tests::" + probe.NATIVE_CASES[1] + " ... FAILED PRIVATE_VALUE\n"
                + "panic: PRIVATE_PATH PRIVATE_TOKEN")
        self.assertEqual(probe.native_failed_cases(text), [known])

    def test_cargo_binds_one_successful_same_manifest_non_test_binary(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "alias-parent").mkdir()
            # An actual resolvable parent alias exercises canonical identity on
            # every OS, including Windows TEMP directories with short names.
            target = root / "alias-parent" / ".." / "target"
            helper = target / probe.TARGET / "debug" / (probe.BINARY + ".exe")
            helper.parent.mkdir(parents=True)
            helper.write_bytes(b"INERT UNIT FIXTURE; NOT A WINDOWS EXECUTABLE")
            manifest = target / "Cargo.toml"
            rows = cargo(helper, manifest)
            encode = lambda value: "\n".join(json.dumps(row) for row in value)
            self.assertEqual(probe.native_executable(encode(rows), target, manifest), helper.resolve())
            variants = [rows + [rows[0]], rows[:-1], rows + [rows[-1]],
                        [rows[0], {"reason": "build-finished", "success": False}],
                        [{**rows[0], "manifest_path": str(target / "other.toml")}, rows[1]],
                        [{**rows[0], "profile": {"test": True}}, rows[1]],
                        [{**rows[0], "executable": str(target / "outside.exe")}, rows[1]]]
            for value in variants:
                with self.assertRaisesRegex(ValueError, "OWNED_READ_CARGO_ARTIFACT_INVALID"):
                    probe.native_executable(encode(value), target, manifest)
            duplicate = encode(rows).replace('"executable":', '"executable":"other","executable":', 1)
            with self.assertRaises(ValueError):
                probe.native_executable(duplicate, target, manifest)
            helper.unlink()
            with self.assertRaises(ValueError):
                probe.native_executable(encode(rows), target, manifest)

    def exercise(self, root, result, mutate=None, stderr="", fail_at=None):
        image = root / "ff-owned-read-tests/OwnedCoreRead.exe"
        image.parent.mkdir()
        image.write_bytes(b"INERT MANAGED UNIT FIXTURE")
        target = root / "ff-owned-read-native"
        helper = target / probe.TARGET / "debug" / (probe.BINARY + ".exe")
        manifest = root / "tests/native/windows-owned-read/Cargo.toml"
        calls = []
        def invoke(command, **kwargs):
            calls.append(command)
            stage = len(calls)
            if stage == 1:
                text = native()
            elif stage == 2:
                helper.parent.mkdir(parents=True)
                helper.write_bytes(b"INERT NATIVE UNIT FIXTURE")
                text = "\n".join(json.dumps(row) for row in cargo(helper, manifest))
            else:
                text = managed(stage == 4)
            if mutate and stage == 3:
                mutate(image, helper)
            return subprocess.CompletedProcess(command, 1 if stage == fail_at else 0, text,
                                               stderr if stage == 3 else "")
        with patch.object(probe.subprocess, "run", side_effect=invoke):
            probe.run_contracts(root, root, result)
        return calls, image, helper

    def test_actual_commands_use_fixed_target_exact_cargo_helper_and_two_digests(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "alias-parent").mkdir()
            root = root / "alias-parent" / ".."
            result = {"passed": False}
            calls, image, helper = self.exercise(root, result)
            self.assertEqual(calls[0][:3], ["cargo", "+1.95.0", "test"])
            self.assertIn("--locked", calls[1])
            self.assertEqual(calls[2], [str(image)])
            self.assertEqual(calls[3], [str(image), "--native-helper", str(helper.resolve())])
            self.assertTrue(result["passed"] and result["images_unchanged"])
            self.assertEqual((result["native_passed_checks"], result["managed_passed_checks"], result["interop_passed_checks"]), (8, 22, 4))

    def test_missing_or_changed_actual_images_fail_before_interop(self):
        for change in (lambda image, helper: image.unlink(), lambda image, helper: helper.unlink(),
                       lambda image, helper: image.write_bytes(b"CHANGED"),
                       lambda image, helper: helper.write_bytes(b"CHANGED")):
            with tempfile.TemporaryDirectory() as temporary:
                result = {"passed": False}
                with self.assertRaises((OSError, ValueError)):
                    self.exercise(Path(temporary), result, mutate=change)
                self.assertFalse(result["passed"])
                self.assertNotIn("interop_passed_checks", result)

    def test_zero_exit_cannot_hide_stderr_failure_marker(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = {"passed": False}
            with self.assertRaisesRegex(ValueError, "OWNED_READ_MANAGED_RESULT_INVALID"):
                self.exercise(Path(temporary), result,
                              stderr="CI_MANAGED_FAILURE suite=OwnedCoreRead case=22 exception=IOException")
            self.assertFalse(result["passed"])
            self.assertIn("OwnedCoreRead:22:IOException", result["diagnostics"]["managed_cases"])

    def test_nonzero_exit_cannot_be_overridden_by_complete_success_text(self):
        for stage in range(1, 5):
            with tempfile.TemporaryDirectory() as temporary:
                result = {"passed": False}
                with self.assertRaisesRegex(ValueError, "OWNED_READ_PROCESS_FAILED"):
                    self.exercise(Path(temporary), result, fail_at=stage)
                self.assertFalse(result["passed"])

    def test_existing_target_is_not_reused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "ff-owned-read-native").mkdir()
            with self.assertRaises(FileExistsError):
                self.exercise(root, {"passed": False})

    def test_main_non_hosted_never_executes_and_failure_receipt_has_no_raw_data(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "receipt.json"
            with patch.object(sys, "argv", ["probe", "--report", str(report)]), \
                    patch.object(sys, "platform", "win32"), \
                    patch.dict(os.environ, {"RUNNER_ENVIRONMENT": "self-hosted"}), \
                    patch.object(probe, "run_contracts") as run, redirect_stdout(io.StringIO()):
                self.assertEqual(probe.main(), 1)
                run.assert_not_called()
                self.assertFalse(report.exists())
            with patch.object(sys, "argv", ["probe", "--report", str(report)]), \
                    patch.object(sys, "platform", "win32"), \
                    patch.dict(os.environ, {"RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_TEMP": temporary}), \
                    patch.object(probe, "run_contracts", side_effect=OSError("PRIVATE_PATH PRIVATE_TOKEN")), \
                    redirect_stdout(io.StringIO()) as captured:
                self.assertEqual(probe.main(), 1)
            text = report.read_text()
            self.assertNotIn("PRIVATE", text + captured.getvalue())
            self.assertFalse(json.loads(text)["passed"])

    def test_managed_diagnostics_enforce_each_new_suite_range_and_no_setup(self):
        valid = ["CI_MANAGED_FAILURE suite=OwnedCoreRead case=22 exception=IOException",
                 "CI_MANAGED_FAILURE suite=OwnedCoreReadInterop case=04 exception=TimeoutException"]
        invalid = [valid[0].replace("case=22", "case=23"), valid[1].replace("case=04", "case=05"),
                   valid[0] + " PRIVATE_VALUE", valid[0].replace("case=22", "case=SETUP_PACKAGE"),
                   valid[1].replace("TimeoutException", "PRIVATE_EXCEPTION")]
        details = ci_diagnostics.diagnostic_details("\n".join(valid + invalid))
        self.assertEqual(details["managed_cases"], ["OwnedCoreRead:22:IOException", "OwnedCoreReadInterop:04:TimeoutException"])
        self.assertNotIn("PRIVATE", str(details))


if __name__ == "__main__":
    unittest.main()
