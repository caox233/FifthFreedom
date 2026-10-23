"""Composition and failure gates; compiler mocks never count as a Windows build."""
import copy
import difflib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_easytier_windows_owned_read as build


class SourceFixture:
    """Small real git-apply inputs for whole-tree mutation controls."""
    def __init__(self, directory):
        self.work = directory / "work"
        self.repository = directory / "repository"
        self.repository.mkdir()
        for name in ("easytier", "tun"):
            (self.work / (name + "-source")).mkdir(parents=True)
        root = self.work / "easytier-source"
        rows, fragments = [], []
        for name in sorted(build.FILES):
            added = "owned_read" in name
            before = "" if added else "// original\n"
            after = "// owned read\n"
            if not added:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(before.encode())
            fragments.extend(difflib.unified_diff(before.splitlines(True), after.splitlines(True),
                fromfile="/dev/null" if added else "a/" + name, tofile="b/" + name))
            rows.append({"path": name, "original_sha256": None if added else build.source.sha(before.encode()),
                         "patched_bytes": len(after), "patched_sha256": build.source.sha(after.encode())})
        (root / "Cargo.lock").write_bytes(b"# synthetic unchanged dependency lock\n")
        (root / "untouched.rs").write_bytes(b"// unrelated source must stay intact\n")
        (self.work / "tun-source/source.rs").write_bytes(b"// paired backend unchanged\n")
        data = "".join(fragments).encode()
        (self.repository / "read.patch").write_bytes(data)
        pin = {"path": "read.patch", "bytes": len(data), "sha256": build.source.sha(data)}
        current = {"easytier": build.paired.inventory(root), "tun": build.paired.inventory(self.work / "tun-source")}
        self.prepared = {"source_files": current, "source_tree_sha256": build.source.sha(build.source.encoded(current)),
            "patches": [], "patch_manifest_sha256": build.source.sha(build.source.encoded([])),
            "cargo_lock_patched_sha256": current["easytier"]["Cargo.lock"]["sha256"],
            "upstream": {}, "tun_upstream": {}, "archives": {}, "omitted_link_input": {"path": "Packet.lib"}}
        expected = copy.deepcopy(current)
        for row in rows:
            expected["easytier"][row["path"]] = {"bytes": row["patched_bytes"], "sha256": row["patched_sha256"]}
        self.read = {"patch": pin, "files": rows}
        self.lock = {"base_recipe": {}, "read_recipe": {}, "kind": "independent-windows-owned-read-build-only",
            "build": {"rust_toolchain": "1.95.0", "target": "x86_64-pc-windows-msvc"},
            "base_prepared": {key: self.prepared[key] for key in ("source_tree_sha256", "patch_manifest_sha256", "cargo_lock_patched_sha256")},
            "expected_prepared": {"source_tree_sha256": build.source.sha(build.source.encoded(expected)),
                "patch_manifest_sha256": build.source.sha(build.source.encoded([pin])),
                "cargo_lock_patched_sha256": self.prepared["cargo_lock_patched_sha256"]}}

    def derive(self):
        with patch.object(build, "load_inputs", return_value=({}, self.read)), patch.object(build, "verify_read"):
            return build.derive(self.work, self.prepared, self.lock, self.repository)


class OwnedReadBuildTests(unittest.TestCase):
    def test_exact_reviewed_recipes_contracts_and_disjoint_source_scope(self):
        lock = json.loads(build.LOCK.read_bytes())
        no_packet, read = build.load_inputs(lock)
        build.verify_read(read)
        self.assertEqual(len(read["files"]), 4)
        self.assertTrue(build.FILES.isdisjoint({row["path"] for row in no_packet["files"]}))
        self.assertEqual(lock["expected_prepared"]["cargo_lock_patched_sha256"], no_packet["expected_prepared"]["cargo_lock_patched_sha256"])
        self.assertEqual(lock["build"], no_packet["build"])

    def test_changed_linked_production_module_or_test_input_reject(self):
        lock = json.loads(build.LOCK.read_bytes())
        _, read = build.load_inputs(lock)
        folder = Path(read["patch"]["path"]).parent
        paths = [read["patch"]["path"], *(row["path"] for row in read["contract_inputs"]),
                 str(folder / "source/owned_read.rs"), str(folder / "source/owned_read_windows.rs")]
        for target in (paths[1], paths[-1]):
            with tempfile.TemporaryDirectory() as directory:
                repository = Path(directory)
                for name in paths:
                    path = repository / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes((build.ROOT / name).read_bytes())
                build.verify_read(read, repository)
                (repository / target).write_bytes(b"changed")
                with self.assertRaises((build.paired.BuildError, build.source.PreparationError)):
                    build.verify_read(read, repository)

    def test_wrong_pin_or_base_cannot_reuse_prior_candidate_identity(self):
        original = json.loads(build.LOCK.read_bytes())
        for key in ("base_recipe", "read_recipe"):
            value = copy.deepcopy(original)
            value[key]["sha256"] = "0" * 64
            with self.assertRaises(build.source.PreparationError):
                build.load_inputs(value)
        value = copy.deepcopy(original)
        value["base_prepared"]["source_tree_sha256"] = "0" * 64
        with self.assertRaisesRegex(build.paired.BuildError, "OWNED_READ_BASE"):
            build.load_inputs(value)

    def test_execution_or_feature_changes_and_bool_schema_reject(self):
        original = json.loads(build.LOCK.read_bytes())
        variants = []
        for key, value in (("execute_binaries", True), ("upload_binaries", True), ("no_default_features", False),
                           ("features", ["faketcp"]), ("target", "x86_64-unknown-linux-gnu")):
            row = copy.deepcopy(original)
            row["build"][key] = value
            variants.append(row)
        row = copy.deepcopy(original)
        row["schema"] = True
        variants.append(row)
        row = copy.deepcopy(original)
        row["admission"]["public_startup_allowed"] = True
        variants.append(row)
        for row in variants:
            with self.assertRaises(build.paired.BuildError):
                build.load_inputs(row)

    def test_real_patch_addition_retains_unrelated_tree_and_lock(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SourceFixture(Path(directory))
            result = fixture.derive()
            build.check_prepared(fixture.work, result, fixture.lock)
            self.assertEqual(result["source_files"]["tun"], fixture.prepared["source_files"]["tun"])
            self.assertEqual(result["source_files"]["easytier"]["untouched.rs"], fixture.prepared["source_files"]["easytier"]["untouched.rs"])
            self.assertFalse(result["runtime_executed"])
            self.assertEqual(len(result["patches"]), 1)

    def test_whole_tree_mutation_cannot_hide_behind_successful_declared_patch(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SourceFixture(Path(directory))
            original = build.paired.apply_patch
            def altered(*args):
                original(*args)
                (fixture.work / "tun-source/source.rs").write_bytes(b"unexpected mutation")
            with patch.object(build.paired, "apply_patch", side_effect=altered):
                with self.assertRaisesRegex(build.paired.BuildError, "OWNED_READ_PATCH_SCOPE"):
                    fixture.derive()

    def test_existing_new_module_or_changed_original_reject_before_git_apply(self):
        for path in ("easytier/src/owned_read.rs", "easytier/src/lib.rs"):
            with tempfile.TemporaryDirectory() as directory:
                fixture = SourceFixture(Path(directory))
                target = fixture.work / "easytier-source" / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"unreviewed")
                # Even a freshly measured tree cannot acquire the original four-file contract.
                fixture.prepared["source_files"]["easytier"] = build.paired.inventory(fixture.work / "easytier-source")
                fixture.prepared["source_tree_sha256"] = build.source.sha(build.source.encoded(fixture.prepared["source_files"]))
                fixture.lock["base_prepared"]["source_tree_sha256"] = fixture.prepared["source_tree_sha256"]
                with self.assertRaisesRegex(build.paired.BuildError, "OWNED_READ_BASE"):
                    fixture.derive()

    def test_receipt_patch_list_or_live_source_tamper_reject(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SourceFixture(Path(directory))
            prepared = fixture.derive()
            altered = copy.deepcopy(prepared)
            altered["patches"] = []
            with self.assertRaisesRegex(build.paired.BuildError, "OWNED_READ_INPUT_CHANGED"):
                build.check_prepared(fixture.work, altered, fixture.lock)
            (fixture.work / "easytier-source/untouched.rs").write_bytes(b"after preparation")
            with self.assertRaisesRegex(build.paired.BuildError, "SOURCE_CHANGED"):
                build.check_prepared(fixture.work, prepared, fixture.lock)

    def _build(self, directory, *, passed=8, base_error=False, mutate_after=False):
        fixture = SourceFixture(Path(directory))
        prepared = fixture.derive()
        compiled = {"runs": [{"stage": "core-cli-build", "exit_code": 0}], "built_executables": {"synthetic": {}},
                    "selected_graph": {}, "paired_tun_resolution_workspace_union": {}, "build_inputs": {}}
        def native(command, **kwargs):
            self.assertIn("--locked", command)
            self.assertIn("x86_64-pc-windows-msvc", command)
            self.assertEqual(kwargs["timeout"], 600)
            self.assertEqual(kwargs["env"]["CARGO_INCREMENTAL"], "0")
            kwargs["stdout"].write(f"test result: ok. {passed} passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n".encode())
            return subprocess.CompletedProcess(command, 0)
        def core(work, output, given, lock, repository, environment, cargo):
            self.assertEqual(given, prepared)
            self.assertEqual(lock, {"synthetic_no_packet_gate": True})
            if base_error:
                output.mkdir()
                stages = ["interface-contract", "typed-session-contract", "metadata", "selected-graph", "core-cli-build"]
                failed = {"schema": 1, "kind": "independent-windows-no-packet-build-only", "status": "failed",
                    "source_tree_sha256": prepared["source_tree_sha256"], "patch_manifest_sha256": prepared["patch_manifest_sha256"],
                    "cargo_lock_sha256": prepared["cargo_lock_patched_sha256"], "runs": [
                        {"stage": stage, "exit_code": 101 if stage == "core-cli-build" else 0, "log_sha256": "1" * 64,
                         "rust_codes": ["E0308"] if stage == "core-cli-build" else [], "link_codes": [],
                         "known_source_locations": [{"tree": "easytier", "source": "easytier/src/launcher.rs", "line": 10, "column": 20}]
                        } for stage in stages]}
                (output / "receipt.json").write_bytes(build.source.encoded(failed))
                raise build.paired.BuildError("NO_PACKET_IMPORT")
            if mutate_after:
                (work / "tun-source/source.rs").write_bytes(b"late source mutation")
            return compiled
        output = Path(directory) / "output"
        with patch.object(build, "load_inputs", return_value=({"synthetic_no_packet_gate": True}, fixture.read)), \
                patch.object(build, "verify_read"), patch.object(build.subprocess, "run", side_effect=native), \
                patch.object(build.base, "build", side_effect=core) as delegate:
            try:
                result = build.build(fixture.work, output, prepared, fixture.lock, fixture.repository, {})
            except ValueError:
                result = json.loads((output / "receipt.json").read_bytes())
            return result, delegate.call_count

    def test_compiler_orchestration_requires_native8_and_delegates_full_no_packet_gates(self):
        with tempfile.TemporaryDirectory() as directory:
            result, calls = self._build(directory)
            self.assertEqual(calls, 1)
            self.assertEqual(result["status"], "windows_owned_read_built_not_executed")
            self.assertEqual([row["stage"] for row in result["runs"]], ["owned-read-contract", "core-cli-build"])
            self.assertFalse(result["runtime_executed"])

    def test_missing_native_case_never_invokes_full_build(self):
        with tempfile.TemporaryDirectory() as directory:
            result, calls = self._build(directory, passed=7)
            self.assertEqual(calls, 0)
            self.assertEqual(result["status"], "failed")
            self.assertNotIn("built_executables", result)

    def test_delegated_packet_rejection_remains_failed_outer_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            result, calls = self._build(directory, base_error=True)
            self.assertEqual(calls, 1)
            self.assertEqual(result["error_code"], "NO_PACKET_IMPORT")
            self.assertEqual(result["status"], "failed")
            self.assertNotIn("built_executables", result)
            self.assertEqual(result["delegate_diagnostics"], "verified_fixed_projection")
            self.assertEqual(result["runs"][-1]["rust_codes"], ["E0308"])
            self.assertEqual(result["runs"][-1]["known_source_locations"][0]["source"], "easytier/src/launcher.rs")

    def test_delegated_diagnostics_reject_wrong_identity_unknown_payload_and_bad_fields(self):
        with tempfile.TemporaryDirectory() as directory:
            self._build(directory, base_error=True)
            path = Path(directory) / "output/core-build/receipt.json"
            original = json.loads(path.read_bytes())
            prepared = json.loads((Path(directory) / "output/source-receipt.json").read_bytes())
            self.assertIsNotNone(build.delegate_diagnostics(path, prepared))
            for change in (
                    lambda value: value.update(source_tree_sha256="0" * 64),
                    lambda value: value.update(raw_compiler_text="secret-do-not-copy"),
                    lambda value: value["runs"][0].update(raw_message="secret-do-not-copy"),
                    lambda value: value["runs"][0].update(rust_codes=["E0308 secret-do-not-copy"]),
                    lambda value: value["runs"][0].update(stage="private-stage"),
                    lambda value: value["runs"][0].update(exit_code=True),
                    lambda value: value["runs"][0]["known_source_locations"][0].update(source="/private/path")):
                value = copy.deepcopy(original)
                change(value)
                path.write_bytes(build.source.encoded(value))
                self.assertIsNone(build.delegate_diagnostics(path, prepared))

    def test_delegated_diagnostics_reject_duplicate_json_and_size_overflow(self):
        with tempfile.TemporaryDirectory() as directory:
            self._build(directory, base_error=True)
            path = Path(directory) / "output/core-build/receipt.json"
            original = path.read_bytes()
            prepared = json.loads((Path(directory) / "output/source-receipt.json").read_bytes())
            path.write_bytes(original.replace(b'"schema": 1', b'"schema": 1,"schema": 1'))
            self.assertNotEqual(path.read_bytes(), original)
            self.assertIsNone(build.delegate_diagnostics(path, prepared))
            path.write_bytes(b" " * (2 * 1024 * 1024 + 1))
            self.assertIsNone(build.delegate_diagnostics(path, prepared))

    def test_changed_source_after_delegate_never_records_successful_binaries(self):
        with tempfile.TemporaryDirectory() as directory:
            result, calls = self._build(directory, mutate_after=True)
            self.assertEqual(calls, 1)
            self.assertEqual(result["status"], "failed")
            self.assertNotIn("built_executables", result)


if __name__ == "__main__":
    unittest.main()
