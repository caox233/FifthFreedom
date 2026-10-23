"""Annotations must identify failures without publishing fixture values or logs."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import sqlite3
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

    def test_method_summary_exposes_only_known_exception_types(self):
        class DatabaseFixture(unittest.TestCase):
            def runTest(self):
                for error in (PermissionError("C:/private/PRIVATE_ACCOUNT.sqlite"),
                              sqlite3.OperationalError("PRIVATE_DATABASE_CONTENT")):
                    with self.subTest(error=error):
                        raise error
        DatabaseFixture.__qualname__ = "DatabaseFixture"
        result = unittest.TestResult()
        DatabaseFixture().run(result)
        summary = diagnostics.test_failure_summary("server", result)
        self.assertIn("exception_types=OperationalError,PermissionError", summary)
        self.assertIn("error_events=2", summary)
        self.assertNotIn("PRIVATE_", summary)
        self.assertNotIn("C:/", summary)

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
            "\nSystem.Exception: PRIVATE_EXCEPTION\nSystem.InvalidOperationException: " + label +
            "\nSystem.InvalidOperationException: PRIVATE_NEW_EXCEPTION\n", ui_labels={label})
        self.assertEqual(details["ui_checks"], [label])
        self.assertNotIn("PRIVATE", str(details))

    def test_preflight_annotations_keep_only_fixed_codes_without_input_or_raw_output(self):
        text = ("PREFLIGHT_NATIVE_REJECTED: /private/PRIVATE_KEY.key=PRIVATE_VALUE\n"
                "PREFLIGHT_REPORT_MISMATCH: PRIVATE_NATIVE_STDOUT\n"
                "PREFLIGHT_UNKNOWN_PRIVATE_CODE\nPREFLIGHT_PROCESS_FAILED_PRIVATE_SUFFIX\n")
        details = diagnostics.diagnostic_details(text)
        self.assertEqual(details["preflight_codes"], ["PREFLIGHT_NATIVE_REJECTED", "PREFLIGHT_REPORT_MISMATCH"])
        self.assertNotIn("PRIVATE", str(details))
        self.assertNotIn("/private", str(details))
        output = io.StringIO()
        with patch.dict("os.environ", {"GITHUB_ACTIONS": "true"}), redirect_stdout(output):
            with self.assertRaises(diagnostics.AnnotatedFailure):
                with diagnostics.diagnostic_step("ci-static-public-deployment-preflight"):
                    raise RuntimeError(text)
        self.assertIn("preflight_codes=PREFLIGHT_NATIVE_REJECTED | PREFLIGHT_REPORT_MISMATCH", output.getvalue())
        self.assertNotIn("PRIVATE", output.getvalue())
        self.assertNotIn("/private", output.getvalue())

    def test_ui_label_allowlist_excludes_dynamic_values(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "Probe.cs"
            path.write_text('Check(valid, "static route contract");\nCheck(valid, "account " + username);\n'
                            'Check(valid && Nested(left, right),\n    "multiline route contract");\n'
                            'Check(valid,\n    "account " + username);\n', encoding="utf-8")
            self.assertEqual(diagnostics.known_ui_labels([path]), {"static route contract", "multiline route contract"})

    def test_owned_process_summary_reports_fixed_guard_code_without_fixture_values(self):
        class LifecycleError(RuntimeError):
            pass
        class FailedFixture(unittest.TestCase):
            def runTest(self):
                raise LifecycleError("EXECUTABLE_PATH_INVALID /private/PRIVATE_ACCOUNT ELF=PRIVATE_BYTES "
                                     "EXECUTABLE_FILE_INVALID_PRIVATE_SUFFIX EXECUTABLE_PRIVATE_UNKNOWN")
        FailedFixture.__qualname__ = "FailedFixture"
        result = unittest.TestResult()
        FailedFixture().run(result)
        summary = diagnostics.test_failure_summary("server", result)
        self.assertIn("owned_process_codes=EXECUTABLE_PATH_INVALID", summary)
        self.assertIn("exception_types=LifecycleError", summary)
        self.assertNotIn("PRIVATE", summary)
        self.assertNotIn("/private", summary)
        self.assertNotIn("EXECUTABLE_FILE_INVALID", summary)
        details = diagnostics.diagnostic_details(result.errors[0][1])
        self.assertEqual(details["owned_process_codes"], ["EXECUTABLE_PATH_INVALID"])
        self.assertNotIn("PRIVATE", str(details))

    def test_nested_steps_emit_once_and_do_not_print_exception_message(self):
        for actions, prefix in (("false", "CI_ERROR_SUMMARY"), ("true", "::error title=Fifth Freedom CI::")):
            with self.subTest(github_actions=actions):
                output = io.StringIO()
                with patch.dict("os.environ", {"GITHUB_ACTIONS": actions}), redirect_stdout(output):
                    with self.assertRaises(diagnostics.AnnotatedFailure):
                        with diagnostics.diagnostic_step("windows-components"):
                            with diagnostics.diagnostic_step("ci-wpf-compile"):
                                raise RuntimeError("PRIVATE_TOKEN_VALUE")
                self.assertEqual(output.getvalue().count(prefix), 1)
                self.assertEqual(len(output.getvalue().splitlines()), 1)
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

    def test_managed_failure_keeps_only_reviewed_suite_case_and_exception(self):
        marker = "CI_MANAGED_FAILURE suite=PublicSessionControl case=05 exception=CryptographicException"
        details = diagnostics.diagnostic_details(marker + "\r\n" + marker + "\n"
            "CI_MANAGED_FAILURE suite=PublicSessionControl case=SETUP_PACKAGE exception=IOException\n")
        self.assertEqual(details["managed_cases"], ["PublicSessionControl:05:CryptographicException",
            "PublicSessionControl:SETUP_PACKAGE:IOException"])
        output = io.StringIO()
        with redirect_stdout(output), self.assertRaises(diagnostics.AnnotatedFailure):
            with diagnostics.diagnostic_step("ci-test-PublicSessionControl"):
                raise RuntimeError(marker + "\n/private/PRIVATE_PATH token=PRIVATE_TOKEN")
        self.assertIn("managed_cases=PublicSessionControl:05:CryptographicException", output.getvalue())
        self.assertNotIn("PRIVATE", output.getvalue())
        self.assertNotIn("/private", output.getvalue())

    def test_managed_unknown_values_suffixes_and_injected_fields_are_not_published(self):
        template = "CI_MANAGED_FAILURE suite={suite} case={case} exception={kind}"
        cases = [("PRIVATE_SUITE", "01", "Exception"), ("PublicSessionControl", "16", "Exception"),
                 ("PublicSessionControl", "00", "Exception"), ("PublicSessionControl", "PRIVATE_CASE", "Exception"),
                 ("PublicSessionControl", "01", "PRIVATE_TYPE"),
                 ("PublicSessionControl", "01", "Exception token=PRIVATE_TOKEN"),
                 ("PublicSessionControl", "01", "Exception_PRIVATE_SUFFIX")]
        text = "\n".join(template.format(suite=suite, case=case, kind=kind) for suite, case, kind in cases)
        self.assertEqual(diagnostics.diagnostic_details(text)["managed_cases"], [])
        self.assertNotIn("PRIVATE", str(diagnostics.diagnostic_details(text)))


if __name__ == "__main__":
    unittest.main()
