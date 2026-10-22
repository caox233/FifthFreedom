"""Negative coverage for public-input admission and verified tool installation."""
from pathlib import Path
import hashlib
import json
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_checks
import ci_setup_protoc
import ci_candidate


class InputAdmissionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="fifth-ci-inputs-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.name = "assets/example.bin"
        self.data = b"synthetic unit test input; not game save data"
        self.row = {"sha256": hashlib.sha256(self.data).hexdigest(), "bytes": len(self.data)}
        self.write_manifests()

    def write_json(self, name, value):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding="utf-8")

    def write_manifests(self, holds=()):
        self.write_json("scripts/local-assets.json", {"files": {self.name: self.row}})
        self.write_json("src/launcher/src/Assets/DefaultSaves/manifest.json", {"Files": {}})
        self.write_json("docs/ASSET-PUBLICATION.json", {"conditional_privacy_holds": list(holds)})

    def hold(self):
        return {"path": self.name, **self.row, "status": "local_preserved_not_published"}

    def write_input(self, data=None):
        path = self.root / self.name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(self.data if data is None else data)

    def test_missing_unclassified_input_rejects_source_and_package(self):
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["status"], "invalid")
        self.assertEqual(result["errors"][0]["reason"], "unclassified_missing_input")
        self.assertFalse(result["complete_package_ready"])

    def test_known_missing_hold_is_reported_but_never_complete(self):
        self.write_manifests([self.hold()])
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["status"], "blocked_privacy_inputs")
        self.assertEqual(result["errors"], [])
        self.assertEqual(result["missing_held_package_inputs"], [self.name])
        self.assertFalse(result["complete_package_ready"])

    def test_matching_bytes_do_not_clear_privacy_hold(self):
        self.write_manifests([self.hold()])
        self.write_input()
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["status"], "invalid")
        self.assertIn("privacy_held_input_present_in_public_checkout", [x["reason"] for x in result["errors"]])

    def test_corrupt_available_input_is_not_waived(self):
        self.write_input(b"corrupt")
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["status"], "invalid")
        self.assertEqual(result["errors"][0]["reason"], "size_or_sha256_mismatch")

    def test_valid_available_input_is_admitted(self):
        self.write_input()
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["status"], "ready")
        self.assertTrue(result["complete_package_ready"])
        self.assertEqual(result["verified_input_count"], 1)

    def test_hold_cannot_authorize_a_different_digest(self):
        hold = self.hold()
        hold["sha256"] = "0" * 64
        self.write_manifests([hold])
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["errors"][0]["reason"], "privacy_hold_manifest_mismatch")

    def test_donor_only_held_input_cannot_leak_into_checkout(self):
        self.write_input()
        donor = "integration/reference/private.bin"
        self.write_manifests([{**self.hold(), "path": donor}])
        (self.root / donor).parent.mkdir(parents=True)
        (self.root / donor).write_bytes(self.data)
        result = ci_checks.assess_inputs(self.root)
        self.assertEqual(result["status"], "invalid")
        self.assertEqual(result["errors"][0]["path"], donor)

    def test_manifest_path_traversal_is_rejected(self):
        for name in ("../outside", "/absolute", "C:/drive", "nested\\escape"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                ci_checks.checked_path(self.root, name)


class VerifiedToolTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="fifth-ci-protoc-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def archive(self, name="bin/protoc", data=b"unit-test-tool"):
        path = self.root / "tool.zip"
        with zipfile.ZipFile(path, "w") as bundle:
            bundle.writestr(name, data)
        return path, hashlib.sha256(path.read_bytes()).hexdigest()

    def test_wrong_digest_never_extracts(self):
        path, _ = self.archive()
        out = self.root / "output"
        with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
            ci_setup_protoc.extract_verified(path, out, "0" * 64)
        self.assertFalse(out.exists())

    def test_matching_digest_extracts_exact_bytes(self):
        path, digest = self.archive()
        out = self.root / "output"
        ci_setup_protoc.extract_verified(path, out, digest)
        self.assertEqual((out / "bin/protoc").read_bytes(), b"unit-test-tool")

    def test_matching_digest_does_not_allow_path_traversal(self):
        path, digest = self.archive("../outside")
        out = self.root / "output"
        with self.assertRaisesRegex(ValueError, "Unsafe"):
            ci_setup_protoc.extract_verified(path, out, digest)
        self.assertFalse(out.exists())
        self.assertFalse((self.root / "outside").exists())

    def test_existing_tool_directory_is_never_overwritten(self):
        destination = self.root / "existing"
        destination.mkdir()
        (destination / "sentinel").write_text("keep")
        with self.assertRaises(FileExistsError):
            ci_setup_protoc.install(destination, "linux")
        self.assertEqual((destination / "sentinel").read_text(), "keep")


class RunnerReceiptTests(unittest.TestCase):
    def test_launcher_summary_requires_all_pass_rows_and_no_failure(self):
        self.assertEqual(ci_candidate.parse_probe_report("PASS one\nPASS two\nRESULT 2 isolated UI/protocol checks passed.\n", "--self-test"), 2)
        self.assertEqual(ci_candidate.parse_probe_report("PASS one\nRESULT 1 passed; 0 failed; isolated UI and save fixtures only\n", "--lan-tools-self-test"), 1)
        for text in ("", "PASS one\n", "RESULT 0 isolated UI/protocol checks passed.\n",
                     "PASS one\nRESULT 2 isolated UI/protocol checks passed.\n",
                     "PASS one\nFAIL two\nRESULT 1 isolated UI/protocol checks passed.\n"):
            with self.subTest(text=text), self.assertRaises(RuntimeError):
                ci_candidate.parse_probe_report(text, "--self-test")

    def test_server_tests_replace_inherited_fixture_paths_and_clean_owned_root(self):
        inherited = {"SCBL_IDENTITY_TEST_DIR": "/do-not-use", "SCBL_RELEASE_TEST_DIR": "/do-not-use", "PATH": "preserve"}
        with ci_candidate.owned_server_test_env(inherited) as actual:
            identity, database = Path(actual["SCBL_IDENTITY_TEST_DIR"]), Path(actual["SCBL_RELEASE_TEST_DIR"])
            self.assertTrue(identity.is_dir())
            self.assertTrue(database.is_dir())
            self.assertEqual(identity.parent, database.parent)
            self.assertEqual(actual["PATH"], "preserve")
            self.assertNotEqual(actual["SCBL_IDENTITY_TEST_DIR"], inherited["SCBL_IDENTITY_TEST_DIR"])
        self.assertFalse(identity.parent.exists())
        self.assertEqual(inherited["SCBL_RELEASE_TEST_DIR"], "/do-not-use")

    def test_multiple_subtest_errors_do_not_make_passed_count_negative(self):
        class SubtestExample(unittest.TestCase):
            def runTest(self):
                for name in ("one", "two"):
                    with self.subTest(name=name):
                        raise FileNotFoundError("synthetic missing input")
        result = unittest.TestResult()
        SubtestExample().run(result)
        row = ci_checks.result_row("synthetic-gate", result)
        self.assertEqual(row["executed"], 1)
        self.assertEqual(row["failed_test_cases"], 1)
        self.assertEqual(row["errors"], 2)
        self.assertEqual(row["passed"], 0)


if __name__ == "__main__":
    unittest.main()
