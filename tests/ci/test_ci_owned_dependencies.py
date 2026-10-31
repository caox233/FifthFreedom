"""Inert synthetic inputs test adapter gates, not DLL loading or Windows behavior."""
from contextlib import ExitStack, redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_owned_dependencies as adapter
import ci_diagnostics


def archive(member=b"inert file; never a DLL", names=None):
    output = io.BytesIO()
    with warnings.catch_warnings(), zipfile.ZipFile(output, "w") as value:
        warnings.simplefilter("ignore", UserWarning)
        for name in names or [adapter.MEMBER]:
            value.writestr(name, member)
    return output.getvalue()


def success():
    return "\n".join([*("PASS " + case for case in adapter.CASES), adapter.SUMMARY]) + "\n"


class DependencyAdapterTests(unittest.TestCase):
    def fixture(self, root, runner=None):
        stack = ExitStack()
        self.addCleanup(stack.close)
        publish = root / "ff-owned-dependency-tests"; publish.mkdir()
        (publish / "OwnedCoreDependencies.exe").write_bytes(b"inert apphost, never executed")
        member = b"inert synthetic file; not official DLL evidence"
        data = archive(member)
        for name, value in (("ARCHIVE_BYTES", len(data)), ("ARCHIVE_SHA", adapter.sha(data)),
                            ("MEMBER_BYTES", len(member)), ("MEMBER_SHA", adapter.sha(member))):
            stack.enter_context(patch.object(adapter, name, value))
        stack.enter_context(patch.object(adapter.sys, "platform", "win32"))
        stack.enter_context(patch.dict(os.environ, {"GITHUB_ACTIONS": "true",
            "RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_TEMP": str(root)}, clear=True))
        response = stack.enter_context(patch.object(adapter.urllib.request, "urlopen",
            side_effect=lambda *args, **kwargs: io.BytesIO(data)))
        process = stack.enter_context(patch.object(adapter.subprocess, "run", side_effect=runner or (
            lambda *args, **kwargs: subprocess.CompletedProcess(args[0], 0, success(), ""))))
        return response, process

    def test_public_pins_match_preserved_origin_and_case_labels_match_frozen_suite(self):
        value = json.loads((adapter.ROOT / "docs/tasks/wintun-official-binary-evidence.json").read_text())
        self.assertEqual((adapter.URL, adapter.ARCHIVE_BYTES, adapter.ARCHIVE_SHA), tuple(
            value["archive"][key] for key in ("source_url", "bytes", "sha256")))
        member = next(row for row in value["members"] if row["path"] == adapter.MEMBER)
        self.assertEqual((adapter.MEMBER_BYTES, adapter.MEMBER_SHA), (member["bytes"], member["sha256"]))
        source = (adapter.ROOT / "tests/OwnedCoreDependencies/Program.cs").read_text()
        self.assertEqual(tuple(re.findall(r'await Case\("([^\"]+)"', source)), adapter.CASES)

    def test_exact_thirteen_unique_ordered_cases_and_single_summary_required(self):
        self.assertTrue(adapter.verified_output(success().replace("\n", "\r\n")))
        for text in (success().replace("13 passed", "12 passed"), success()+success(),
                     success()+"SKIP private\n", success()+"CI_MANAGED_FAILURE suite=OwnedCoreDependencies case=13 exception=IOException\n",
                     success().replace("PASS "+adapter.CASES[0], "PASS "+adapter.CASES[1]),
                     success().replace("0 skipped", "1 skipped"), success()+"unexpected private output\n"):
            self.assertFalse(adapter.verified_output(text))

    def test_real_zip_reader_rejects_wrong_digest_before_extraction(self):
        for data in (b"", archive(), b"x" * adapter.ARCHIVE_BYTES):
            with self.assertRaisesRegex(ValueError, "^DEPENDENCY_ARCHIVE_INVALID$"):
                adapter.official_member(data)

    def test_synthetic_pinned_zip_rejects_missing_duplicate_and_wrong_member(self):
        member = b"inert"
        for names, expected in ((["other"], "DEPENDENCY_MEMBER_INVALID"),
                                 ([adapter.MEMBER, adapter.MEMBER], "DEPENDENCY_MEMBER_INVALID"),
                                 ([adapter.MEMBER], "DEPENDENCY_MEMBER_INVALID")):
            data = archive(member, names)
            with patch.object(adapter, "ARCHIVE_BYTES", len(data)), patch.object(adapter, "ARCHIVE_SHA", adapter.sha(data)), \
                    patch.object(adapter, "MEMBER_BYTES", len(member)), patch.object(adapter, "MEMBER_SHA", "0" * 64):
                with self.assertRaisesRegex(ValueError, "^"+expected+"$"):
                    adapter.official_member(data)

    def test_only_fixed_apphost_and_verified_separate_input_are_invoked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); response, process = self.fixture(root)
            report = {}; adapter.run(report)
            self.assertTrue(report["passed"])
            self.assertEqual((report["passed_checks"], report["skipped_checks"]), (13, 0))
            response.assert_called_once_with(adapter.URL, timeout=60)
            self.assertEqual(process.call_args.args[0], [str(root / "ff-owned-dependency-tests/OwnedCoreDependencies.exe"),
                "--wintun-input", str(root / "ff-owned-dependency-assets/wintun.dll")])
            self.assertTrue(report["official_inputs_unchanged"] and report["test_apphost_unchanged"]
                            and report["test_adjacent_input_removed"])

    def test_preexisting_adjacent_input_and_asset_directory_are_never_overwritten(self):
        for conflict in ("adjacent", "assets"):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory); response, process = self.fixture(root)
                prior = root / ("ff-owned-dependency-tests/wintun.dll" if conflict == "adjacent" else "ff-owned-dependency-assets")
                if conflict == "adjacent": prior.write_bytes(b"preserve original")
                else: prior.mkdir()
                with self.assertRaises((ValueError, FileExistsError)):
                    adapter.run({})
                response.assert_not_called(); process.assert_not_called()
                if conflict == "adjacent": self.assertEqual(prior.read_bytes(), b"preserve original")
                else: self.assertTrue(prior.is_dir())

    def test_changed_apphost_or_official_input_or_retained_copy_cannot_pass(self):
        for mutation in ("image", "member", "adjacent"):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                def runner(command, **kwargs):
                    path = Path(command[0]) if mutation == "image" else Path(command[2]) if mutation == "member" else Path(command[0]).parent / "wintun.dll"
                    path.write_bytes(b"changed or retained inert input")
                    return subprocess.CompletedProcess(command, 0, success(), "")
                self.fixture(root, runner)
                report = {}
                with self.assertRaises(ValueError): adapter.run(report)
                self.assertNotEqual(report.get("passed"), True)

    def test_nonzero_process_cannot_certify_even_exact_success_stdout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root, lambda command, **kwargs: subprocess.CompletedProcess(command, 1, success(), ""))
            with self.assertRaisesRegex(ValueError, "^DEPENDENCY_RESULT_INVALID$"): adapter.run({})

    def test_fixed_failures_bound_cases_setup_exceptions_and_no_private_suffix(self):
        prefix = "CI_MANAGED_FAILURE suite=OwnedCoreDependencies case="
        good = [prefix+case+" exception=IOException" for case in ("01", "13", "SETUP_OR_CLEANUP")]
        bad = [prefix+case+" exception=IOException" for case in ("00", "14", "CLEANUP")]
        bad += [prefix+"01 exception=ArgumentException", good[0]+" PRIVATE", prefix+"01 exception=IOException_PRIVATE"]
        result = ci_diagnostics.diagnostic_details("\n".join(good+bad))
        self.assertEqual(result["managed_cases"], sorted("OwnedCoreDependencies:"+case+":IOException"
            for case in ("01", "13", "SETUP_OR_CLEANUP")))
        self.assertNotIn("PRIVATE", json.dumps(result))

    def test_main_never_exports_private_exception_or_process_output(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            out = io.StringIO()
            with patch.object(adapter.sys, "argv", ["adapter", "--report", str(path)]), \
                    patch.object(adapter, "run", side_effect=OSError("PRIVATE_SECRET_PATH")), redirect_stdout(out):
                self.assertEqual(adapter.main(), 1)
            self.assertNotIn("PRIVATE", path.read_text()+out.getvalue())
            self.assertEqual(json.loads(path.read_text())["error"], "DEPENDENCY_CHECK_FAILED")

    def test_nonhost_rejects_before_network_or_process(self):
        with patch.object(adapter.sys, "platform", "linux"), patch.object(adapter.urllib.request, "urlopen") as download, \
                patch.object(adapter.subprocess, "run") as process:
            with self.assertRaisesRegex(ValueError, "^DEPENDENCY_HOST_REQUIRED$"): adapter.run({})
            download.assert_not_called(); process.assert_not_called()


if __name__ == "__main__":
    unittest.main()
