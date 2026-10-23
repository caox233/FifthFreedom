"""Synthetic compiler output tests; no EasyTier executable is run or distributed."""
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_easytier_build as build
import test_ci_prepare_easytier_source as fixtures


class EasyTierBuildEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.CleanSourcePreparationTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.fixture.lock["build"].update(bins=["easytier-core", "easytier-cli"])
        self.fixture.lock["upstream"]["source_date_epoch"] = 1
        self.prepared = self.fixture.run_prepare()
        self.output = self.fixture.root / "evidence"
        self.target = self.fixture.root / "target"

    def fake_compile(self, command, **kwargs):
        if "test" in command:
            selection = command[command.index("--lib") + 1]
            count = next(count for name, count, _ in build.CHECKS if name == selection)
            kwargs["stdout"].write(f"test result: ok. {count} passed; 0 failed; 0 ignored; 0 measured; 100 filtered out\n".encode())
        else:
            self.assertIn("build", command)
            for name in ("easytier-core", "easytier-cli"):
                path = self.target / self.fixture.lock["build"]["target"] / "release" / name
                path.parent.mkdir(parents=True, exist_ok=True)
                data = bytearray(64)
                data[:6], data[18:20] = b"\x7fELF\x02\x01", b"\x3e\0"
                path.write_bytes(data)  # Inert ELF header fixture, never executable.
        return subprocess.CompletedProcess(command, 0)

    def run_build(self, compiler=None):
        with patch.object(build.subprocess, "run", side_effect=compiler or self.fake_compile):
            return build.build_source(self.fixture.output, self.target, self.output, self.fixture.lock, environment={})

    def test_receipts_bind_source_and_exact_contracts_without_copying_or_running_binaries(self):
        result = self.run_build()
        self.assertEqual([row["passed"] for row in result["runs"][:-1]], [4, 10, 9, 2, 7, 3])
        self.assertEqual(result["patch_manifest_sha256"], self.prepared["patch_manifest_sha256"])
        self.assertEqual(set(path.name for path in self.output.iterdir()), {"receipt.json", "source-receipt.json"})
        self.assertFalse(result["runtime_executed"])
        self.assertFalse(result["runtime_binary_uploaded"])
        self.assertFalse(result["public_startup_allowed"])

    def test_zero_skipped_partial_duplicate_or_failed_contract_summaries_fail(self):
        for text in ("", "test result: ok. 0 passed; 0 failed; 0 ignored;", "test result: ok. 4 passed; 0 failed; 1 ignored;",
                     "test result: ok. 3 passed; 0 failed; 0 ignored;", "test result: FAILED. 4 passed; 1 failed; 0 ignored;",
                     "test result: ok. 4 passed; 0 failed; 0 ignored;\ntest result: ok. 4 passed; 0 failed; 0 ignored;"):
            with self.subTest(text=text), self.assertRaises(build.BuildError):
                build.require_count(text, 4)

    def test_changed_prepared_source_prevents_any_compiler_execution(self):
        (self.fixture.output / "easytier/secure.rs").write_bytes(b"changed\n")
        with patch.object(build.subprocess, "run") as command, self.assertRaises(build.BuildError):
            build.build_source(self.fixture.output, self.target, self.output, self.fixture.lock)
        command.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_source_changed_during_compile_is_rejected_and_failure_receipt_preserved(self):
        def changing(command, **kwargs):
            result = self.fake_compile(command, **kwargs)
            if "build" in command:
                (self.fixture.output / "Cargo.lock").write_bytes(b"altered\n")
            return result
        with self.assertRaisesRegex(build.BuildError, "SOURCE_CHANGED"):
            self.run_build(changing)
        self.assertEqual(json.loads((self.output / "receipt.json").read_bytes())["status"], "failed")

    def test_compiler_failure_does_not_publish_raw_output_or_continue(self):
        def failed(command, **kwargs):
            kwargs["stdout"].write(b"PRIVATE_SENTINEL_NOT_FOR_RECEIPT\n")
            return subprocess.CompletedProcess(command, 101)
        with self.assertRaisesRegex(build.BuildError, "COMMAND_FAILED"):
            self.run_build(failed)
        raw = (self.output / "receipt.json").read_text()
        self.assertNotIn("PRIVATE_SENTINEL", raw)
        self.assertEqual(len(json.loads(raw)["runs"]), 1)

    def test_target_inside_source_or_existing_evidence_is_rejected(self):
        with self.assertRaisesRegex(build.BuildError, "TARGET_INSIDE_SOURCE"):
            build.build_source(self.fixture.output, self.fixture.output / "target", self.output, self.fixture.lock)
        self.output.mkdir()
        (self.output / "original").write_bytes(b"evidence")
        with self.assertRaises(FileExistsError):
            self.run_build()
        self.assertEqual((self.output / "original").read_bytes(), b"evidence")


if __name__ == "__main__":
    unittest.main()
