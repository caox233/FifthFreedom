"""Paired-source and receipt regressions; synthetic PE headers are never executed."""
import difflib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tomllib
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_easytier_windows_build as build
import test_ci_prepare_easytier_source as fixtures


class WindowsPairedSourceTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.CleanSourcePreparationTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        self.work = self.root / "work"
        self.work.mkdir()
        self.output = self.root / "receipt"
        self.fixture.items["Cargo.toml"] = b"[workspace]\n"
        self.fixture.items["Cargo.lock"] = (f'version = 4\n[[package]]\nname = "tun-easytier"\nversion = "1.1.1"\n'
                                             f'source = "{build.TUN_SOURCE}"\n\n[[package]]\nname = "retained"\nversion = "1.0.0"\n').encode()
        self.fixture.lock["cargo_lock_sha256"] = build.source.sha(self.fixture.items["Cargo.lock"])
        self.fixture.items["easytier/build.rs"] = b"fn main() { old_thunk(); }\n"
        self.fixture.items["easytier/caller.rs"] = b"fn caller() { old_backend(); }\n"
        self.fixture.write_archive()
        self.tun = self.root / "tun.tar"
        self.tun_original = b'[package]\nname = "tun-easytier"\nversion = "1.1.1"\n'
        with tarfile.open(self.tun, "w") as archive:
            row = tarfile.TarInfo("Cargo.toml")
            row.size, row.mode = len(self.tun_original), 0o644
            archive.addfile(row, io.BytesIO(self.tun_original))
        groups = []
        for name, path, before, after in (
            ("tun", "Cargo.toml", self.tun_original, self.tun_original + b"# actual paired synthetic source\n"),
            ("easytier", "easytier/caller.rs", self.fixture.items["easytier/caller.rs"], b"fn caller() { paired_backend(); }\n"),
        ):
            row = self.make_patch(name + ".patch", path, before, after)
            groups.append({"name": name, "patch": row["path"], "patch_sha256": row["sha256"],
                           "files": [{"path": path, "original_sha256": build.source.sha(before), "patched_sha256": build.source.sha(after)}]})
        modern = self.make_patch("modern.patch", "easytier/build.rs", self.fixture.items["easytier/build.rs"], b"fn main() { modern_sdk(); }\n")
        modern.update(source_path="easytier/build.rs", original_sha256=build.source.sha(self.fixture.items["easytier/build.rs"]),
                      patched_sha256=build.source.sha(b"fn main() { modern_sdk(); }\n"))
        self.lock = {"linux_recipe": self.recipe("linux.json", self.fixture.lock),
                     "session_recipe": self.recipe("session.json", {"groups": groups}),
                     "tun": {"archive": self.fixture.row(self.tun.read_bytes()), "omitted_files": [], "materialized_links": [],
                             "upstream": {"commit": "b" * 40}},
                     "modern_build_patch": modern, "link_input": self.fixture.lock["omitted_files"][0],
                     "build": {"rust_toolchain": "1.95.0", "target": "x86_64-pc-windows-msvc",
                               "bins": ["easytier-core.exe", "easytier-cli.exe"]}}

    def make_patch(self, filename, path, before, after):
        data = "".join(difflib.unified_diff(before.decode().splitlines(True), after.decode().splitlines(True),
                                           fromfile="a/" + path, tofile="b/" + path)).encode()
        (self.root / filename).write_bytes(data)
        return {"path": filename, **self.fixture.row(data)}

    def recipe(self, filename, value):
        data = build.source.encoded(value)
        (self.root / filename).write_bytes(data)
        return {"path": filename, "sha256": build.source.sha(data)}

    def prepare(self):
        return build.prepare(self.fixture.archive, self.tun, self.work, self.lock, self.root)

    def metadata(self):
        return {"packages": [{"name": "tun-easytier", "version": "1.1.1", "source": None, "id": "paired-tun",
                              "manifest_path": str(self.work / "tun-source/Cargo.toml")},
                             {"name": "easytier", "id": "caller", "manifest_path": str(self.work / "easytier-source/easytier/Cargo.toml")}],
                "resolve": {"nodes": [{"id": "caller", "features": ["default", build.FEATURE],
                                        "deps": [{"name": "tun", "pkg": "paired-tun"}]},
                                       {"id": "paired-tun", "features": ["async"], "deps": []}]}}

    def fake_compile(self, command, **kwargs):
        if "metadata" in command:
            kwargs["stdout"].write(json.dumps(self.metadata()).encode())
        else:
            self.assertIn("build", command)
            self.assertIn("--locked", command)
            self.assertIn(build.FEATURE, command)
            self.assertNotIn("run", command)
            for name in self.lock["build"]["bins"]:
                path = self.work / "target" / self.lock["build"]["target"] / "release" / name
                path.parent.mkdir(parents=True, exist_ok=True)
                data = bytearray(256)
                data[:2], data[60:64] = b"MZ", (128).to_bytes(4, "little")
                data[128:132], data[132:134], data[152:154] = b"PE\0\0", b"\x64\x86", b"\x0b\x02"
                path.write_bytes(data)
        return subprocess.CompletedProcess(command, 0)

    def run_build(self, prepared, compiler=None):
        with patch.object(build.subprocess, "run", side_effect=compiler or self.fake_compile):
            return build.build(self.work, self.output, prepared, self.lock, environment={})

    def test_paired_sources_keep_guard_and_only_explicit_link_input(self):
        result = self.prepare()
        self.assertEqual((self.work / "easytier-source/easytier/secure.rs").read_bytes(), self.fixture.patched)
        self.assertIn(b"paired_backend", (self.work / "easytier-source/easytier/caller.rs").read_bytes())
        self.assertIn(b"actual paired", (self.work / "tun-source/Cargo.toml").read_bytes())
        self.assertEqual(len(result["patches"]), 4)
        self.assertNotIn(str(self.root), json.dumps(result))
        build.require_prepared(self.work, result, self.lock)

    def test_changed_pinned_recipe_and_archive_are_rejected_before_source(self):
        (self.root / "session.json").write_bytes(b"{}");
        with self.assertRaisesRegex(build.BuildError, "RECIPE_DIGEST"):
            self.prepare()
        self.assertFalse((self.work / "easytier-source").exists())

    def test_changed_guard_input_never_becomes_a_successful_source_receipt(self):
        self.fixture.items["easytier/secure.rs"] += b"// altered\n"
        self.fixture.write_archive()
        self.lock["linux_recipe"] = self.recipe("linux.json", self.fixture.lock)
        with self.assertRaisesRegex(build.BuildError, "GUARD_BASE"):
            self.prepare()

    def test_pinned_final_source_identity_is_enforced(self):
        self.lock["expected_prepared"] = {"source_tree_sha256": "0" * 64}
        with self.assertRaisesRegex(build.BuildError, "PREPARED_IDENTITY"):
            self.prepare()

    def test_lock_transform_changes_only_exact_git_tun_source(self):
        original = self.fixture.items["Cargo.lock"]
        changed = tomllib.loads(build.transform_lock(original).decode())
        self.assertNotIn("source", changed["package"][0])
        self.assertEqual(changed["package"][1], {"name": "retained", "version": "1.0.0"})
        for wrong in (original.replace(build.TUN_SOURCE.encode(), b"registry+old"), original + original):
            with self.assertRaises((ValueError, build.BuildError)):
                build.transform_lock(wrong)

    def test_metadata_must_prove_actual_path_feature_and_direct_paired_dependency(self):
        self.assertEqual(build.require_resolution(self.metadata(), self.work)["tun_source"], "verified-local-paired-tree")
        for change in range(5):
            data = self.metadata()
            if change == 0: data["packages"][0]["source"] = "registry+old"
            if change == 1: data["packages"][0]["manifest_path"] = str(self.root / "old/Cargo.toml")
            if change == 2: data["resolve"]["nodes"][0]["features"] = ["default"]
            if change == 3: data["resolve"]["nodes"][0]["deps"][0]["pkg"] = "different-tun"
            if change == 4: data["resolve"]["nodes"][1]["features"] = []
            with self.subTest(change=change), self.assertRaises(build.BuildError):
                build.require_resolution(data, self.work)

    def test_build_receipts_bind_resolution_pe_and_no_execution_or_binary_upload(self):
        result = self.run_build(self.prepare())
        self.assertEqual(result["status"], "paired_windows_core_cli_built_not_executed")
        self.assertEqual([row["stage"] for row in result["runs"]], ["metadata", "core-cli-build"])
        self.assertEqual(set(path.name for path in self.output.iterdir()), {"receipt.json", "source-receipt.json"})
        for flag in ("runtime_executed", "runtime_binary_uploaded", "public_startup_allowed", "driver_loaded"):
            self.assertFalse(result[flag])

    def test_changed_prepared_backend_prevents_any_cargo_command(self):
        prepared = self.prepare()
        (self.work / "tun-source/Cargo.toml").write_bytes(b"changed")
        with patch.object(build.subprocess, "run") as command, self.assertRaisesRegex(build.BuildError, "SOURCE_CHANGED"):
            build.build(self.work, self.output, prepared, self.lock, environment={})
        command.assert_not_called()

    def test_changed_source_during_build_is_rejected_after_compiler_success(self):
        prepared = self.prepare()
        def compiler(command, **kwargs):
            result = self.fake_compile(command, **kwargs)
            if "build" in command:
                (self.work / "tun-source/Cargo.toml").write_bytes(b"replaced")
            return result
        with self.assertRaisesRegex(build.BuildError, "SOURCE_CHANGED"):
            self.run_build(prepared, compiler)
        self.assertEqual(json.loads((self.output / "receipt.json").read_bytes())["status"], "failed")

    def test_failed_cargo_exports_only_fixed_codes_and_no_raw_diagnostics(self):
        prepared = self.prepare()
        def failure(command, **kwargs):
            kwargs["stderr"].write(b"PRIVATE_SENTINEL error[E0425] LNK1104\n")
            return subprocess.CompletedProcess(command, 101)
        with self.assertRaisesRegex(build.BuildError, "COMMAND_FAILED"):
            self.run_build(prepared, failure)
        data = (self.output / "receipt.json").read_text()
        self.assertNotIn("PRIVATE_SENTINEL", data)
        self.assertEqual(json.loads(data)["runs"][0]["rust_codes"], ["E0425"])

    def test_compiler_location_is_only_an_exact_known_public_source_suffix(self):
        data = b" --> C:\\PRIVATE\\easytier\\caller.rs:12:4\n --> C:\\PRIVATE\\secret.txt:9:1\n"
        result = build.safe_diagnostics(data, {"easytier": {"easytier/caller.rs": {}}})
        self.assertEqual(result["known_source_locations"], [{"tree": "easytier", "source": "easytier/caller.rs", "line": 12, "column": 4}])
        self.assertNotIn("PRIVATE", json.dumps(result))

    def test_pe_wrong_machine_and_unreviewed_binary_input_fail(self):
        with self.assertRaises(build.BuildError):
            build.pe_identity(b"MZ" + b"\0" * 254)
        self.prepare()
        (self.work / "tun-source/foreign.txt").write_bytes(b"MZUNREVIEWED")
        with self.assertRaisesRegex(build.BuildError, "UNEXPECTED_BINARY"):
            build.inventory(self.work / "tun-source")


if __name__ == "__main__":
    unittest.main()
