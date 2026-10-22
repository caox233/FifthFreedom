"""Annotations must identify failures without publishing fixture values or logs."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_candidate
import ci_diagnostics as diagnostics


class AnnotationSafetyTests(unittest.TestCase):
    def test_command_escaping_prevents_extra_workflow_commands(self):
        value = "percent%\r\n::warning::not a new command"
        self.assertEqual(diagnostics.escape_command_data(value), "percent%25%0D%0A::warning::not a new command")
        self.assertEqual(diagnostics.escape_command_property("a,b:c%\r\n"), "a%2Cb%3Ac%25%0D%0A")
        output = io.StringIO()
        with patch.dict("os.environ", {"GITHUB_ACTIONS": "true"}), redirect_stdout(output):
            diagnostics.emit_error(value)
        self.assertEqual(len(output.getvalue().splitlines()), 1)
        self.assertTrue(output.getvalue().startswith("::error title=Fifth Freedom CI::"))
        self.assertNotIn("\r", output.getvalue())

    def test_annotation_length_is_bounded(self):
        output = io.StringIO()
        with redirect_stdout(output):
            diagnostics.emit_error("x" * 5000)
        self.assertIn("[truncated]", output.getvalue())
        self.assertLess(len(output.getvalue()), diagnostics.MAX_MESSAGE + 100)

    def test_subtest_values_and_traceback_never_enter_method_summary(self):
        class FailedFixture(unittest.TestCase):
            def runTest(self):
                with self.subTest(password="PRIVATE_VALUE", endpoint="https://private.invalid"):
                    raise RuntimeError("Bearer PRIVATE_TRACEBACK_VALUE")
        FailedFixture.__qualname__ = "FailedFixture"
        result = unittest.TestResult()
        FailedFixture().run(result)
        summary = diagnostics.test_failure_summary("source", result)
        self.assertIn("error_events=1", summary)
        self.assertIn("FailedFixture.runTest", summary)
        for value in ("PRIVATE_VALUE", "private.invalid", "PRIVATE_TRACEBACK_VALUE", "Bearer"):
            self.assertNotIn(value, summary)

    def test_unsafe_test_identity_is_not_published(self):
        class FakeTest:
            def id(self):
                return "test_private.Class.test_case\n::error::private"
        self.assertEqual(diagnostics.method_id(FakeTest()), "unavailable-test-method")

    def test_compiler_summary_keeps_codes_not_paths_or_values(self):
        text = ("C:/Users/private/App.cs(2): error CS0246: secret=PRIVATE_A\n"
                "error[E0433]: PRIVATE_B\n"
                "fatal error LNK1181: D:/private/file.lib\n"
                "error NETSDK1005: PRIVATE_C\n"
                "warning CS9999: not an error\n")
        details = diagnostics.diagnostic_details(text)
        self.assertEqual(details["compiler_codes"], ["CS0246", "E0433", "LNK1181", "NETSDK1005"])
        self.assertNotIn("PRIVATE", str(details))
        self.assertNotIn("private", str(details))
        self.assertNotIn("CS9999", str(details))

    def test_only_exact_known_ui_assertions_are_exposed(self):
        label = "retained UI route remains reachable"
        details = diagnostics.diagnostic_details(
            "FAIL " + label + "\nFAIL account=PRIVATE_VALUE\nSystem.Exception: " + label +
            "\nSystem.Exception: PRIVATE_EXCEPTION\n", ui_labels={label})
        self.assertEqual(details["ui_checks"], [label])
        self.assertNotIn("PRIVATE", str(details))

    def test_ui_label_allowlist_excludes_dynamic_values(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "Probe.cs"
            path.write_text('Check(valid, "static route contract");\nCheck(valid, "account " + username);\n', encoding="utf-8")
            self.assertEqual(diagnostics.known_ui_labels([path]), {"static route contract"})

    def test_nested_steps_emit_once_and_do_not_print_exception_message(self):
        output = io.StringIO()
        with redirect_stdout(output), self.assertRaises(diagnostics.AnnotatedFailure):
            with diagnostics.diagnostic_step("windows-components"):
                with diagnostics.diagnostic_step("ci-wpf-compile"):
                    raise RuntimeError("PRIVATE_TOKEN_VALUE")
        self.assertEqual(output.getvalue().count("CI_ERROR_SUMMARY"), 1)
        self.assertIn("step=ci-wpf-compile", output.getvalue())
        self.assertNotIn("PRIVATE_TOKEN_VALUE", output.getvalue())

    def test_only_new_step_logs_contribute_codes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = root / "logs"
            folder.mkdir()
            (folder / "old-ci-wpf-compile.stderr.txt").write_text("error CS9999: PRIVATE_STALE\n")
            def fail(*args, **kwargs):
                (folder / "new-ci-wpf-compile.stdout.txt").write_text("error CS0246: PRIVATE_CURRENT\n")
                raise RuntimeError("PRIVATE_EXCEPTION")
            output = io.StringIO()
            with patch.object(ci_candidate, "ART", root), patch.object(ci_candidate.build, "run", side_effect=fail), redirect_stdout(output):
                with self.assertRaises(diagnostics.AnnotatedFailure):
                    ci_candidate.run_build_step("ci-wpf-compile", ["unused-test-command"])
            self.assertIn("CS0246", output.getvalue())
            self.assertNotIn("CS9999", output.getvalue())
            self.assertNotIn("PRIVATE", output.getvalue())

    def test_bounded_read_does_not_include_large_log_prefix(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "build.txt"
            path.write_bytes(b"PRIVATE_PREFIX" + b"x" * (diagnostics.MAX_LOG_BYTES + 10) + b"error CS0246")
            text = diagnostics.read_bounded_logs([path])
            self.assertEqual(len(text.encode()), diagnostics.MAX_LOG_BYTES)
            self.assertNotIn("PRIVATE_PREFIX", text)
            self.assertIn("CS0246", text)


if __name__ == "__main__":
    unittest.main()
