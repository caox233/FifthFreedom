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
        summary = "RESULT 25 passed, 0 skipped"
        valid = "\n".join([f"PASS case {i}" for i in range(25)] + [summary])
        self.assertTrue(loopback.verified_output(valid, "PublicNativeReturn"))
        for changed in (valid.replace("PASS case 24", "SKIP case 24"), valid.replace(summary, "RESULT 14 passed, 11 skipped"),
                        valid + "\n" + SUMMARY, valid.replace(summary, "RESULT 25 passed, 1 skipped"),
                        valid + "\nCI_MANAGED_FAILURE suite=PublicNativeReturn case=15 exception=UnauthorizedAccessException"):
            self.assertFalse(loopback.verified_output(changed, "PublicNativeReturn"))
        self.assertFalse(loopback.verified_output(valid))

    def test_return_command_and_receipt_are_distinct_from_native_interop(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            stdout = "\n".join([f"PASS case {i}" for i in range(25)] + ["RESULT 25 passed, 0 skipped"])
            child = subprocess.CompletedProcess([], 0, stdout, "PRIVATE_NOT_RETAINED")
            with patch.object(sys, "argv", ["probe", "--suite", "PublicNativeReturn", "--report", str(path)]), \
                    patch.object(sys, "platform", "win32"), patch.dict("os.environ", {"RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(loopback.subprocess, "run", return_value=child) as invoke:
                self.assertEqual(loopback.main(), 0)
            self.assertIn("PublicNativeReturn.csproj", " ".join(invoke.call_args.args[0]))
            self.assertNotIn("--native-interop", invoke.call_args.args[0])
            receipt = json.loads(path.read_text())
            self.assertEqual(receipt["passed_checks"], 25)
            self.assertEqual(receipt["skipped_checks"], 0)
            self.assertIs(receipt["native_interop_exercised"], False)
            self.assertNotIn("PRIVATE", path.read_text())


if __name__ == "__main__":
    unittest.main()
