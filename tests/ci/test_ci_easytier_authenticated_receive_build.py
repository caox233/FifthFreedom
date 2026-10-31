"""New recipe binding and finite build orchestration, without running a runtime."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_easytier_authenticated_receive_build as receive
import ci_easytier_build as build
import ci_prepare_easytier_source as source
import test_ci_easytier_build as fixtures


class AuthenticatedReceiveRecipeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-receive-recipe-")
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "recipe.json"
        self.lock = json.loads(receive.LOCK.read_bytes())

    def load(self):
        self.path.write_bytes(source.encoded(self.lock))
        return receive.load_recipe(self.path)

    def test_reviewed_extension_retains_six_guards_notices_lock_and_closed_admission(self):
        actual = self.load()
        baseline = json.loads(source.LOCK.read_bytes())
        self.assertEqual(actual["patches"][:6], baseline["patches"])
        self.assertEqual(len(actual["patches"]), 8)
        for field in ("upstream", "archive", "cargo_lock_sha256", "omitted_files", "materialized_links", "required_notices", "build", "admission"):
            self.assertEqual(actual[field], baseline[field])
        self.assertEqual(actual["contract_checks"], [{"filter": f, "passed": n, "exact": e} for f, n, e in receive.CHECKS])
        self.assertEqual(sum(n for _, n, _ in receive.CHECKS), 44)
        self.assertEqual(receive.CHECKS[0][1], 15)
        self.assertEqual(receive.CHECKS[-1], ("peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip", 1, True))
        self.assertNotEqual(receive.PROFILE, "easytier-build-only")
        for row in actual["patches"]:
            source.checked_bytes(ROOT / row["path"], row)

    def test_missing_reordered_or_old_candidate_patches_and_omissions_are_rejected(self):
        original = copy.deepcopy(self.lock)
        variants = []
        for field in ("patches", "omitted_files", "materialized_links", "required_notices", "contract_checks"):
            value = copy.deepcopy(original)
            value[field].pop()
            variants.append(value)
        value = copy.deepcopy(original)
        value["patches"][-2:] = reversed(value["patches"][-2:])
        variants.append(value)
        variants.append(json.loads(source.LOCK.read_bytes()))
        for value in variants:
            self.lock = value
            with self.subTest(keys=value.keys()), self.assertRaises(build.BuildError):
                self.load()

    def test_recipe_cannot_change_inputs_source_counts_or_admission(self):
        original = copy.deepcopy(self.lock)
        changes = (("build", "target", "other-target"), ("admission", "runtime_admission_ready", True),
                   ("baseline_recipe", "sha256", "0" * 64), ("authenticated_receive_evidence", "sha256", "0" * 64))
        for field, key, value in changes:
            self.lock = copy.deepcopy(original)
            self.lock[field][key] = value
            with self.subTest(field=field), self.assertRaises(build.BuildError):
                self.load()
        self.lock = copy.deepcopy(original)
        self.lock["contract_checks"][0]["passed"] = 14
        with self.assertRaises(build.BuildError):
            self.load()
        self.lock = copy.deepcopy(original)
        self.lock["modified_files"][0]["patched_sha256"] = "0" * 64
        with self.assertRaises(build.BuildError):
            self.load()
        self.lock = copy.deepcopy(original)
        self.lock["expected_source_tree_sha256"] = "NOT_A_SOURCE_HASH"
        with self.assertRaises(build.BuildError):
            self.load()

    def test_entry_selects_new_lock_profile_and_contracts_without_changing_default(self):
        with patch.object(build, "hosted_build") as hosted:
            receive.main()
            hosted.assert_called_once_with(receive.LOCK, receive.PROFILE, checks=receive.CHECKS)
        with patch.object(build, "hosted_build") as hosted:
            build.main()
            hosted.assert_called_once_with(source.LOCK, "easytier-build-only")
        self.assertEqual([n for _, n, _ in build.CHECKS], [4, 10, 9, 2, 7, 3])


class AuthenticatedReceiveBuildTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.EasyTierBuildEvidenceTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.commands = []
        self.fixture.fixture.lock["expected_source_tree_sha256"] = self.fixture.prepared["source_tree_sha256"]

    def compiler(self, command, **kwargs):
        self.commands.append(command)
        if "test" in command:
            selection = command[command.index("--lib") + 1]
            count, exact = next((count, exact) for name, count, exact in receive.CHECKS if name == selection)
            self.assertIn("--locked", command)
            self.assertEqual("--exact" in command, exact)
            kwargs["stdout"].write(f"test result: ok. {count} passed; 0 failed; 0 ignored; 0 measured; 0 filtered out\n".encode())
            return subprocess.CompletedProcess(command, 0)
        return self.fixture.fake_compile(command, **kwargs)

    def run_build(self, compiler=None):
        f = self.fixture
        with patch.object(build.subprocess, "run", side_effect=compiler or self.compiler):
            return build.build_source(f.fixture.output, f.target, f.output, f.fixture.lock,
                                      environment={}, checks=receive.CHECKS)

    def test_exact_15_plus_29_commands_then_compile_only_and_bounded_receipts(self):
        receipt = self.run_build()
        self.assertEqual([r["passed"] for r in receipt["runs"][:-1]], [15, 4, 10, 9, 2, 3, 1])
        self.assertEqual(len(self.commands), 8)
        self.assertEqual(self.commands[-1][2:4], ["build", "--locked"])
        self.assertEqual({p.name for p in self.fixture.output.iterdir()}, {"source-receipt.json", "receipt.json"})
        for field in ("runtime_executed", "runtime_binary_uploaded", "public_startup_allowed"):
            self.assertFalse(receipt[field])

    def test_missing_new_test_or_skip_stops_before_release_and_keeps_failure(self):
        def partial(command, **kwargs):
            self.commands.append(command)
            kwargs["stdout"].write(b"test result: ok. 14 passed; 0 failed; 1 ignored; 0 measured; 0 filtered out\n")
            return subprocess.CompletedProcess(command, 0)
        with self.assertRaisesRegex(build.BuildError, "COUNT_MISMATCH"):
            self.run_build(partial)
        self.assertEqual(len(self.commands), 1)
        receipt = json.loads((self.fixture.output / "receipt.json").read_bytes())
        self.assertEqual(receipt["status"], "failed")
        self.assertNotIn("built_executables", receipt)

    def test_wrong_pinned_whole_tree_never_runs_the_compiler(self):
        self.fixture.fixture.lock["expected_source_tree_sha256"] = "0" * 64
        with self.assertRaisesRegex(build.BuildError, "SOURCE_CHANGED"):
            self.run_build()
        self.assertEqual(self.commands, [])
        self.assertFalse(self.fixture.output.exists())


if __name__ == "__main__":
    unittest.main()
