"""No Windows networking here: verify the bounded hosted suite adapter only."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_windows_loopback as loopback

SUMMARY = "RESULT 9 final-login preparation checks passed; owned loopback HTTP2 only; no game, Public runtime or live-route admission."


class WindowsLoopbackTests(unittest.TestCase):
    def test_exact_nine_checks_and_summary_are_required_without_skips(self):
        valid = "\n".join([f"PASS case {i}" for i in range(9)] + [SUMMARY])
        self.assertTrue(loopback.verified_output(valid))
        for changed in (valid.replace("PASS case 8", "SKIP case 8"), valid + "\n" + SUMMARY,
                        valid.replace("RESULT 9", "RESULT 0"), valid + "\nFAIL private detail",
                        valid + "\nCI_MANAGED_FAILURE suite=FinalLaunchPreparation case=09 exception=Exception"):
            self.assertFalse(loopback.verified_output(changed))

    def test_real_command_failure_receipt_keeps_codes_and_not_child_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            output = io.StringIO()
            marker = "CI_MANAGED_FAILURE suite=FinalLaunchPreparation case=SETUP_LISTENER exception=SocketException"
            child = subprocess.CompletedProcess([], 1, marker + "\nFINAL_PREPARATION_LOOPBACK_PERMISSION_DENIED",
                                                "PRIVATE_PATH PRIVATE_TOKEN PRIVATE_STACK")
            with patch.object(sys, "argv", ["probe", "--report", str(path)]), patch.object(sys, "platform", "win32"), \
                    patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(loopback.subprocess, "run", return_value=child) as invoke, redirect_stdout(output):
                self.assertEqual(loopback.main(), 1)
            self.assertEqual(invoke.call_args.args[0][0], "dotnet")
            self.assertIn("FinalLaunchPreparation.csproj", " ".join(invoke.call_args.args[0]))
            text = path.read_text()
            self.assertNotIn("PRIVATE", text + output.getvalue())
            receipt = json.loads(text)
            self.assertIs(receipt["passed"], False)
            self.assertIs(receipt["services_changed"], False)
            self.assertEqual(receipt["diagnostics"]["final_preparation_codes"], ["FINAL_PREPARATION_LOOPBACK_PERMISSION_DENIED"])

    def test_non_hosted_invocation_cannot_run_or_create_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            with patch.object(sys, "argv", ["probe", "--report", str(path)]), \
                    patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "self-hosted"}), \
                    patch.object(loopback.subprocess, "run") as invoke, redirect_stdout(io.StringIO()):
                self.assertEqual(loopback.main(), 1)
            invoke.assert_not_called()
            self.assertFalse(path.exists())

    def test_return_suite_requires_all_windows_cases_and_zero_skips(self):
        summary = "RESULT 28 passed, 0 skipped"
        valid = "\n".join([f"PASS case {i}" for i in range(28)] + [summary])
        self.assertTrue(loopback.verified_output(valid, "PublicNativeReturn"))
        for changed in (valid.replace("PASS case 24", "SKIP case 24"), valid.replace(summary, "RESULT 14 passed, 11 skipped"),
                        valid + "\n" + SUMMARY, valid.replace(summary, "RESULT 28 passed, 1 skipped"),
                        valid.replace(summary, "RESULT 25 passed, 0 skipped"),
                        valid + "\nCI_MANAGED_FAILURE suite=PublicNativeReturn case=15 exception=UnauthorizedAccessException"):
            self.assertFalse(loopback.verified_output(changed, "PublicNativeReturn"))
        self.assertFalse(loopback.verified_output(valid))

    def test_return_command_and_receipt_are_distinct_from_native_interop(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            stdout = "\n".join([f"PASS case {i}" for i in range(28)] + ["RESULT 28 passed, 0 skipped"])
            child = subprocess.CompletedProcess([], 0, stdout, "PRIVATE_NOT_RETAINED")
            with patch.object(sys, "argv", ["probe", "--suite", "PublicNativeReturn", "--report", str(path)]), \
                    patch.object(sys, "platform", "win32"), patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(loopback.subprocess, "run", return_value=child) as invoke:
                self.assertEqual(loopback.main(), 0)
            self.assertIn("PublicNativeReturn.csproj", " ".join(invoke.call_args.args[0]))
            self.assertNotIn("--native-interop", invoke.call_args.args[0])
            receipt = json.loads(path.read_text())
            self.assertEqual(receipt["passed_checks"], 28)
            self.assertEqual(receipt["skipped_checks"], 0)
            self.assertIs(receipt["native_interop_exercised"], False)
            self.assertNotIn("PRIVATE", path.read_text())

    def test_stream_suite_requires_every_distinct_source_case_and_exact_scope(self):
        lines = ["DIAGNOSTIC_STREAM_PASS " + name for name in loopback.STREAM_CASES]
        summary = loopback.SUITES["DiagnosticTextStreaming"][1]
        valid = "\n".join(lines + [summary])
        self.assertTrue(loopback.verified_output(valid, "DiagnosticTextStreaming"))
        invalid = (
            "\n".join(lines[:-1] + [lines[0], summary]),
            "\n".join(list(reversed(lines)) + [summary]),
            valid.replace(lines[0], "DIAGNOSTIC_STREAM_PASS unknown-case"),
            valid.replace("full_zip_tested=false", "full_zip_tested=true"),
            valid.replace("cases=12", "cases=0"),
            valid + "\n" + summary,
            valid + "\nSKIP synthetic-case",
            valid + "\nDIAGNOSTIC_STREAM_SOURCE_CHANGED",
        )
        for output in invalid:
            self.assertFalse(loopback.verified_output(output, "DiagnosticTextStreaming"))
        self.assertFalse(loopback.verified_output(valid, "PublicNativeReturn"))

    def test_stream_command_uses_fixed_project_and_keeps_raw_output_private(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            stdout = "\n".join(["DIAGNOSTIC_STREAM_PASS " + name for name in loopback.STREAM_CASES]
                + [loopback.SUITES["DiagnosticTextStreaming"][1]])
            child = subprocess.CompletedProcess([], 0, stdout, "PRIVATE_PATH_TOKEN")
            with patch.object(sys, "argv", ["probe", "--suite", "DiagnosticTextStreaming", "--report", str(path)]), \
                    patch.object(sys, "platform", "win32"), patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(loopback.subprocess, "run", return_value=child) as invoke:
                self.assertEqual(loopback.main(), 0)
            self.assertIn("DiagnosticTextStreaming.csproj", " ".join(invoke.call_args.args[0]))
            result = json.loads(path.read_text())
            self.assertEqual(result["passed_checks"], 12)
            self.assertEqual(result["skipped_checks"], 0)
            self.assertIs(result["full_diagnostic_zip_exercised"], False)
            self.assertIs(result["native_interop_exercised"], False)
            self.assertNotIn("PRIVATE", path.read_text())

    def test_owned_core_requires_all_seventeen_without_failure_or_skip(self):
        valid = "\n".join([f"PASS case {i}" for i in range(17)] + ["RESULT 17 passed; 0 skipped"])
        self.assertTrue(loopback.verified_output(valid, "OwnedCoreProcess"))
        for text in (valid.replace("PASS case 16", "SKIP case 16"), valid.replace("17 passed", "5 passed"),
                     valid.replace("0 skipped", "12 skipped"), valid + "\nRESULT 17 passed; 0 skipped",
                     valid + "\nCI_OWNED_CORE_FAILURE case=17 phase=PARENT_DEATH code=OTHER"):
            self.assertFalse(loopback.verified_output(text, "OwnedCoreProcess"))

    def test_owned_core_executes_only_fixed_published_apphost_and_binds_its_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            image = root / "ff-owned-core-tests/OwnedCoreProcess.exe"
            image.parent.mkdir()
            image.write_bytes(b"inert synthetic apphost bytes; never executed by this unit test")
            report = root / "receipt.json"
            output = "\n".join([f"PASS case {i}" for i in range(17)] + ["RESULT 17 passed; 0 skipped"])
            with patch.object(sys, "argv", ["probe", "--suite", "OwnedCoreProcess", "--report", str(report)]), \
                    patch.object(sys, "platform", "win32"), patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_TEMP": temporary}), \
                    patch.object(loopback.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, output, "PRIVATE_TOKEN")) as invoke:
                self.assertEqual(loopback.main(), 0)
            self.assertEqual(invoke.call_args.args[0], [str(image)])
            result = json.loads(report.read_text())
            self.assertEqual(result["passed_checks"], 17)
            self.assertEqual(result["test_apphost_sha256"], loopback.hashlib.sha256(image.read_bytes()).hexdigest())
            self.assertTrue(result["test_apphost_unchanged"])
            self.assertEqual(result["required_runtime"], "win-x86")
            self.assertFalse(result["easytier_executed"])
            self.assertFalse(result["driver_exercised"])
            self.assertNotIn("PRIVATE", report.read_text())

    def test_owned_core_mutated_or_removed_apphost_cannot_leave_success_receipt(self):
        for remove in (False, True):
            with self.subTest(remove=remove), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                image = root / "ff-owned-core-tests/OwnedCoreProcess.exe"
                image.parent.mkdir(); image.write_bytes(b"inert original")
                report = root / "receipt.json"
                output = "\n".join([f"PASS case {i}" for i in range(17)] + ["RESULT 17 passed; 0 skipped"])
                def child(*args, **kwargs):
                    if remove:
                        image.unlink()
                    else:
                        image.write_bytes(b"inert changed")
                    return subprocess.CompletedProcess([], 0, output, "")
                with patch.object(sys, "argv", ["probe", "--suite", "OwnedCoreProcess", "--report", str(report)]), \
                        patch.object(sys, "platform", "win32"), patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_TEMP": temporary}), \
                        patch.object(loopback.subprocess, "run", side_effect=child), redirect_stdout(io.StringIO()):
                    self.assertEqual(loopback.main(), 1)
                self.assertFalse(json.loads(report.read_text())["passed"])


if __name__ == "__main__":
    unittest.main()
