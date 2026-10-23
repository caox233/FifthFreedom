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
import ci_easytier_peer_map_build as peer_map
import ci_easytier_build as build
import ci_prepare_easytier_source as source
import test_ci_easytier_build as fixtures


class PeerMapRecipeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-peer-map-recipe-")
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "recipe.json"
        self.lock = json.loads(peer_map.LOCK.read_bytes())

    def load(self):
        self.path.write_bytes(source.encoded(self.lock))
        return peer_map.load_recipe(self.path)

    def test_reviewed_extension_retains_ten_prior_patches_notices_lock_and_closed_admission(self):
        actual = self.load()
        baseline = json.loads(peer_map.challenge.LOCK.read_bytes())
        self.assertEqual(actual["patches"][:10], baseline["patches"])
        self.assertEqual(len(actual["patches"]), 12)
        for field in ("upstream", "archive", "cargo_lock_sha256", "omitted_files", "materialized_links", "required_notices", "build", "admission"):
            self.assertEqual(actual[field], baseline[field])
        self.assertEqual(actual["contract_checks"], [{"filter": f, "passed": n, "exact": e} for f, n, e in peer_map.CHECKS])
        self.assertEqual(sum(n for _, n, _ in peer_map.CHECKS), 71)
        self.assertEqual(peer_map.CHECKS[0], ("fifth_freedom_peer_map_challenge_contract", 12, False))
        self.assertEqual(peer_map.CHECKS[1:], peer_map.challenge.CHECKS)
        self.assertEqual(actual["added_files"][-1]["path"], peer_map.ADDED_MODULE)
        self.assertEqual(actual["added_files"][:-1], baseline["added_files"])
        self.assertEqual(actual["modified_files"][:-3], baseline["modified_files"])
        self.assertEqual(tuple(row["path"] for row in actual["modified_files"][-3:]), peer_map.MODIFIED_FILES)
        self.assertNotEqual(actual["expected_source_tree_sha256"], baseline["expected_source_tree_sha256"])
        self.assertEqual(peer_map.CHECKS[-1], ("peers::peer_conn::tests::peer_conn_secure_mode_data_roundtrip", 1, True))
        self.assertNotEqual(peer_map.PROFILE, "easytier-build-only")
        for row in actual["patches"]:
            source.checked_bytes(ROOT / row["path"], row)

    def test_missing_reordered_or_old_candidate_patches_and_omissions_are_rejected(self):
        original = copy.deepcopy(self.lock)
        variants = []
        for field in ("patches", "omitted_files", "materialized_links", "required_notices", "contract_checks", "added_files", "modified_files"):
            value = copy.deepcopy(original)
            value[field].pop()
            variants.append(value)
        value = copy.deepcopy(original)
        value["patches"][-2:] = reversed(value["patches"][-2:])
        variants.append(value)
        variants.append(json.loads(peer_map.challenge.LOCK.read_bytes()))
        variants.append(json.loads(source.LOCK.read_bytes()))
        for value in variants:
            self.lock = value
            with self.subTest(keys=value.keys()), self.assertRaises(build.BuildError):
                self.load()

    def test_recipe_cannot_change_inputs_source_counts_or_admission(self):
        original = copy.deepcopy(self.lock)
        changes = (("build", "target", "other-target"), ("admission", "runtime_admission_ready", True),
                   ("baseline_recipe", "sha256", "0" * 64), ("peer_map_evidence", "sha256", "0" * 64))
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
        for field, key, value in (("modified_files", "original_sha256", "0" * 64),
                                  ("modified_files", "patched_sha256", "0" * 64),
                                  ("added_files", "bytes", 1),
                                  ("added_files", "mode", 0o755),
                                  ("added_files", "sha256", "0" * 64)):
            self.lock = copy.deepcopy(original)
            self.lock[field][-1][key] = value
            with self.subTest(field=field, key=key), self.assertRaises(build.BuildError):
                self.load()
        self.lock = copy.deepcopy(original)
        self.lock["expected_source_tree_sha256"] = "0" * 64
        with self.assertRaises(build.BuildError):
            self.load()

    def test_evidence_cannot_expand_or_duplicate_the_reviewed_source_scope(self):
        # Actual isolated repository inputs, so an accidental dict collapse of
        # duplicate rows or a foreign source addition is observable.
        with tempfile.TemporaryDirectory(prefix="ff-peer-map-evidence-") as temp:
            repository = Path(temp)
            inputs = (
                "scripts/easytier-source.lock.json",
                "scripts/easytier-authenticated-receive-source.lock.json",
                "scripts/easytier-data-challenge-source.lock.json",
                peer_map.challenge.receive.EVIDENCE,
                peer_map.challenge.EVIDENCE,
                peer_map.EVIDENCE,
            )
            for name in inputs:
                target = repository / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((ROOT / name).read_bytes())
            path = repository / peer_map.EVIDENCE
            original = json.loads(path.read_bytes())
            variants = []
            value = copy.deepcopy(original)
            value["source_files"].append(copy.deepcopy(value["source_files"][0]))
            variants.append(value)
            value = copy.deepcopy(original)
            value["source_files"][0]["path"] = "easytier/src/unreviewed.rs"
            variants.append(value)
            value = copy.deepcopy(original)
            value["source_files"][0]["baseline_sha256"] = None
            variants.append(value)
            for value in variants:
                path.write_bytes(source.encoded(value))
                with self.assertRaisesRegex(build.BuildError, "SOURCE_SCOPE_MISMATCH"):
                    peer_map.expected_recipe(repository)

    def test_entry_selects_new_lock_profile_and_contracts_without_changing_default(self):
        with patch.object(build, "hosted_build") as hosted:
            peer_map.main()
            hosted.assert_called_once_with(peer_map.LOCK, peer_map.PROFILE, checks=peer_map.CHECKS)
        with patch.object(build, "hosted_build") as hosted:
            peer_map.challenge.main()
            hosted.assert_called_once_with(peer_map.challenge.LOCK, peer_map.challenge.PROFILE, checks=peer_map.challenge.CHECKS)
        with patch.object(build, "hosted_build") as hosted:
            build.main()
            hosted.assert_called_once_with(source.LOCK, "easytier-build-only")
        self.assertEqual([n for _, n, _ in build.CHECKS], [4, 10, 9, 2, 7, 3])


class PeerMapBuildTests(unittest.TestCase):
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
            count, exact = next((count, exact) for name, count, exact in peer_map.CHECKS if name == selection)
            self.assertIn("--locked", command)
            self.assertEqual("--exact" in command, exact)
            kwargs["stdout"].write(f"test result: ok. {count} passed; 0 failed; 0 ignored; 0 measured; 0 filtered out\n".encode())
            return subprocess.CompletedProcess(command, 0)
        return self.fixture.fake_compile(command, **kwargs)

    def run_build(self, compiler=None):
        f = self.fixture
        with patch.object(build.subprocess, "run", side_effect=compiler or self.compiler):
            return build.build_source(f.fixture.output, f.target, f.output, f.fixture.lock,
                                      environment={}, checks=peer_map.CHECKS)

    def test_exact_12_plus_59_commands_then_compile_only_and_bounded_receipts(self):
        receipt = self.run_build()
        self.assertEqual([r["passed"] for r in receipt["runs"][:-1]], [12, 15, 15, 4, 10, 9, 2, 3, 1])
        self.assertEqual(len(self.commands), 10)
        self.assertEqual(self.commands[-1][2:4], ["build", "--locked"])
        self.assertEqual({p.name for p in self.fixture.output.iterdir()}, {"source-receipt.json", "receipt.json"})
        for field in ("runtime_executed", "runtime_binary_uploaded", "public_startup_allowed"):
            self.assertFalse(receipt[field])

    def test_missing_new_test_or_skip_stops_before_release_and_keeps_failure(self):
        def partial(command, **kwargs):
            self.commands.append(command)
            kwargs["stdout"].write(b"test result: ok. 11 passed; 0 failed; 1 ignored; 0 measured; 0 filtered out\n")
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
