"""Mock native execution only; fixtures are inert bytes, never a public service."""
import copy
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_public_preflight as preflight


class PublicPreflightAdapterTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-preflight-unit-")
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name)
        self.candidate = self.base / "candidate"
        (self.candidate / "bin").mkdir(parents=True)
        self.binary = self.candidate / "bin/FifthFreedom.Server"
        self.binary.write_bytes(b"INERT_UNIT_FIXTURE_NOT_AN_EXECUTABLE")
        self.runner_temp = self.base / "runner-temp"
        self.runner_temp.mkdir()
        identity = {"service": {"manifest_sha256": "a" * 64},
                    "admitted_client": {"storage_key": "1|synthetic-release|synthetic-compat|" + "b" * 64}}
        self.receipt = {"status": "native_dual_identity_verified", "scope": dict(preflight.candidate.SCOPE),
                        "native_verification": identity, "expected_native_result": copy.deepcopy(identity),
                        "files": {"bin/FifthFreedom.Server": preflight.candidate.sha_file(self.binary)}}
        self.expected = preflight.expected_report(identity)
        # Unit control-flow coverage is portable. This replaces only this module's
        # platform selector, never global sys.platform or real file metadata.
        platform = patch.object(preflight, "sys", SimpleNamespace(platform="linux"))
        platform.start()
        self.addCleanup(platform.stop)

    def completed(self, report=None, code=0, stderr=b""):
        return subprocess.CompletedProcess([], code, preflight.candidate.json_bytes(
            self.expected if report is None else report), stderr)

    def rejected(self):
        return subprocess.CompletedProcess([], 1, b"", b"PUBLIC_PROFILE_NOT_READY: fixed test error")

    def check(self):
        return preflight.check(self.candidate, self.receipt, self.runner_temp)

    def assert_cleaned(self):
        self.assertEqual(list(self.runner_temp.iterdir()), [])

    def test_ephemeral_inputs_are_separate_private_removed_and_not_in_receipt(self):
        private_paths = []
        def process(args, **kwargs):
            private = Path(kwargs["cwd"])
            self.assertTrue(private.is_relative_to(self.runner_temp))
            self.assertFalse(private.is_relative_to(self.candidate))
            private_paths.append(private)
            if "--check-public-deployment" not in args:
                return self.rejected()
            config = Path(args[-1])
            document = json.loads(config.read_bytes())
            key = Path(document["auth_key_file"])
            data = Path(document["data_directory"])
            self.assertEqual(len(key.read_bytes()), 32)
            self.assertEqual(list(data.iterdir()), [])
            self.assertEqual(document["overlay_ipv4"], "10.77.0.1")
            if os.name != "nt":
                self.assertEqual([stat.S_IMODE(p.stat().st_mode) for p in (private, data, config, key)],
                                 [0o700, 0o700, 0o600, 0o600])
            return self.completed()
        original = preflight.snapshot(self.candidate)
        with patch.object(preflight.subprocess, "run", side_effect=process) as run:
            result = self.check()
        self.assertEqual(run.call_count, 2)
        self.assertFalse(result["static_preflight"]["startup_allowed"])
        self.assertTrue(result["ordinary_public_startup_rejected"])
        self.assertTrue(result["ephemeral_inputs_removed"])
        self.assertFalse(result["native_public_profile_enabled"])
        serialized = json.dumps(result)
        self.assertTrue(all(str(path) not in serialized and not path.exists() for path in private_paths))
        self.assertNotIn("auth_key_file", serialized)
        self.assertEqual(preflight.snapshot(self.candidate), original)
        self.assert_cleaned()

    def test_unverified_or_changed_candidate_is_rejected_before_execution(self):
        original = copy.deepcopy(self.receipt)
        for change in ({"status": "assembled_pending_native_verification"},
                       {"scope": {**original["scope"], "native_public_profile_enabled": True}},
                       {"native_verification": {}}):
            self.receipt = {**original, **change}
            with self.subTest(change=next(iter(change))), patch.object(preflight.subprocess, "run") as run:
                with self.assertRaisesRegex(preflight.PreflightError, "UNVERIFIED_CANDIDATE"):
                    self.check()
                run.assert_not_called()
        self.receipt = original
        self.binary.write_bytes(b"CHANGED_INERT_FIXTURE")
        with patch.object(preflight.subprocess, "run") as run:
            with self.assertRaisesRegex(preflight.PreflightError, "EXECUTABLE_CHANGED"):
                self.check()
            run.assert_not_called()

    def test_report_requires_exact_identity_closed_flags_types_and_blockers(self):
        variants = [{**self.expected, key: value} for key, value in (
            ("schema", True), ("startup_allowed", 0), ("startup_allowed", True),
            ("service_manifest_sha256", "c" * 64), ("admitted_client_storage_key", "wrong"),
            ("blockers", []), ("private_path", "PRIVATE_CANARY"))]
        for report in variants:
            with self.subTest(changed=next(key for key in report if report[key] != self.expected.get(key)
                                          or type(report[key]) is not type(self.expected.get(key)))), \
                    patch.object(preflight.subprocess, "run", return_value=self.completed(report)) as run:
                with self.assertRaisesRegex(preflight.PreflightError, "REPORT_MISMATCH"):
                    self.check()
                self.assertEqual(run.call_count, 1)
                self.assert_cleaned()

    def test_duplicate_report_or_native_failure_never_exports_raw_output(self):
        results = [subprocess.CompletedProcess([], 0, b'{"schema":1,"schema":1}', b""),
                   subprocess.CompletedProcess([], 1, b"PRIVATE_CANARY", b"PRIVATE_CANARY"),
                   subprocess.CompletedProcess([], 0, b"PRIVATE_CANARY", b"PRIVATE_CANARY")]
        for result in results:
            with self.subTest(code=result.returncode), patch.object(preflight.subprocess, "run", return_value=result):
                with self.assertRaises(preflight.PreflightError) as failure:
                    self.check()
                self.assertNotIn("PRIVATE_CANARY", str(failure.exception))
                self.assert_cleaned()

    def test_ordinary_public_startup_must_fail_with_the_exact_gate_code(self):
        for result in (subprocess.CompletedProcess([], 0, b"", b"PUBLIC_PROFILE_NOT_READY:"),
                       subprocess.CompletedProcess([], 1, b"", b"DIFFERENT_FAILURE"),
                       subprocess.CompletedProcess([], 1, b"unexpected", b"PUBLIC_PROFILE_NOT_READY:")):
            with self.subTest(code=result.returncode), \
                    patch.object(preflight.subprocess, "run", side_effect=[self.completed(), result]):
                with self.assertRaisesRegex(preflight.PreflightError, "PUBLIC_GATE_NOT_CLOSED"):
                    self.check()
                self.assert_cleaned()

    def test_private_or_candidate_mutations_are_rejected(self):
        original = self.binary.read_bytes()
        for target in ("private", "candidate"):
            def process(args, **kwargs):
                if "--check-public-deployment" not in args:
                    return self.rejected()
                if target == "private":
                    (Path(kwargs["cwd"]) / "data/account.db").write_bytes(b"MUST_NOT_CREATE_STATE")
                else:
                    self.binary.write_bytes(b"CHANGED_INERT_FIXTURE")
                return self.completed()
            with self.subTest(target=target), patch.object(preflight.subprocess, "run", side_effect=process):
                with self.assertRaisesRegex(preflight.PreflightError, "CHANGED"):
                    self.check()
                self.assert_cleaned()
            self.binary.write_bytes(original)

    def test_timeout_and_oversized_output_remove_ephemeral_inputs(self):
        with patch.object(preflight.subprocess, "run", side_effect=subprocess.TimeoutExpired("PRIVATE_CANARY", 120)):
            with self.assertRaisesRegex(preflight.PreflightError, "PROCESS_FAILED") as failure:
                self.check()
            self.assertNotIn("PRIVATE_CANARY", str(failure.exception))
            self.assert_cleaned()
        with patch.object(preflight.subprocess, "run", return_value=self.completed(stderr=b"x" * 16385)):
            with self.assertRaisesRegex(preflight.PreflightError, "OUTPUT_LIMIT"):
                self.check()
            self.assert_cleaned()

    def test_temporary_state_cannot_be_placed_inside_candidate(self):
        with patch.object(preflight.subprocess, "run") as run:
            with self.assertRaisesRegex(preflight.PreflightError, "TEMP_INSIDE_CANDIDATE"):
                preflight.check(self.candidate, self.receipt, self.candidate)
            run.assert_not_called()

    def test_real_entry_is_linux_only_and_cannot_silently_skip(self):
        with patch.object(preflight, "sys", SimpleNamespace(platform="win32")), \
                patch.object(preflight.subprocess, "run") as run:
            with self.assertRaisesRegex(preflight.PreflightError, "LINUX_REQUIRED"):
                self.check()
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
