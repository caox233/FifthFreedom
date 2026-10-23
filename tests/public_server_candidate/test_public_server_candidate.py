"""Synthetic PE/ELF byte fixtures only: never execute them or publish as candidates."""
import hashlib
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import public_server_candidate as candidate


def pe(machine, dll, marker):
    data = bytearray(256)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 60, 128)
    data[128:132] = b"PE\0\0"
    struct.pack_into("<H", data, 132, machine)
    # Minimal shape only; no sections or executable code, never run this fixture.
    struct.pack_into("<H", data, 148, 2)
    struct.pack_into("<H", data, 150, 0x2000 if dll else 0)
    struct.pack_into("<H", data, 152, 0x10B if machine == 0x14C else 0x20B)
    return bytes(data) + marker.encode() + b"\0SYNTHETIC_NONEXECUTABLE_FIXTURE"


def elf(marker="SYNTHETIC_NONEXECUTABLE_FIXTURE"):
    data = bytearray(64)
    data[:7] = b"\x7fELF\x02\x01\x01"
    struct.pack_into("<HHI", data, 16, 3, 62, 1)
    struct.pack_into("<H", data, 52, 64)
    return bytes(data) + marker.encode() + b"\0SYNTHETIC_NONEXECUTABLE_FIXTURE"


class PublicServerCandidateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-synthetic-public-candidate-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.checkout = self.root / "checkout"
        self.checkout.mkdir()
        self.release = {"release_id": "synthetic-development", "compat_id": "synthetic-contract"}
        (self.checkout / "release-identity.json").write_bytes(candidate.json_bytes(self.release))
        (self.checkout / "LICENSE").write_text("synthetic source license")
        (self.checkout / "THIRD_PARTY_NOTICES.txt").write_text("synthetic third-party notices")
        self.commit, self.run, self.attempt = "a" * 40, "123456789", "2"
        self.output = self.root / "output"
        self.windows, self.linux = self.root / "windows", self.root / "linux"
        roles = {"FifthFreedom.component-only.exe": "launcher", "uplay_r1_loader.dll": "main",
                 "scbl_gameplay.dll": "gameplay", "FifthFreedom.Server.exe": "server"}
        win = {name: pe(machine, dll, self.marker(roles[name]))
               for name, (_, machine, dll) in candidate.WINDOWS.items()}
        self.write_lane(self.windows, "windows-components", win)
        self.write_lane(self.linux, "linux-lan-baseline", {
            "dedicated_server": elf(self.marker("server")), "verify_public_package": elf()})

    def marker(self, role):
        return f"SCBL_RELEASE_V1|{role}|{self.release['release_id']}|{self.release['compat_id']}|END_SCBl"

    def folder(self, artifact, lane):
        return artifact / "ci" / lane

    def write_lane(self, artifact, lane, components):
        folder = self.folder(artifact, lane)
        (folder / "components").mkdir(parents=True)
        rows = {}
        for name, data in components.items():
            (folder / "components" / name).write_bytes(data)
            rows[name] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        receipt = {"schema": 1, "lane": lane, "commit": self.commit, "run_id": self.run,
                   "run_attempt": self.attempt, "github_actions": True, "tracked_source_dirty": False,
                   "status": "components_built_and_selected_tests_passed", "exit_code": 0,
                   "complete_package": False, "game_executed": False, "live_acceptance": False,
                   "deployed": False, "published": False, "components": rows}
        if lane == "linux-lan-baseline":
            receipt["workspace"] = "src/native"
        self.save_lane(artifact, lane, receipt)

    def save_lane(self, artifact, lane, receipt):
        folder = self.folder(artifact, lane)
        (folder / "receipt.json").write_bytes(candidate.json_bytes(receipt))
        (folder / "components/SHA256SUMS.txt").write_text("".join(
            f"{row['sha256']}  {name}\n" for name, row in sorted(receipt["components"].items())))

    def receipt(self, artifact=None, lane="windows-components"):
        return json.loads((self.folder(artifact or self.windows, lane) / "receipt.json").read_text())

    def component(self, name="FifthFreedom.component-only.exe"):
        return self.folder(self.windows, "windows-components") / "components" / name

    def replace_component(self, name, data):
        self.component(name).write_bytes(data)
        receipt = self.receipt()
        receipt["components"][name] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        self.save_lane(self.windows, "windows-components", receipt)

    def assemble(self):
        return candidate.assemble(self.windows, self.linux, self.output, self.checkout,
                                  self.commit, self.run, self.attempt)

    def test_copy_preserves_originals_and_keeps_two_distinct_identities_pending_verification(self):
        original = {name: candidate.sha_file(self.component(name)) for name in candidate.WINDOWS}
        receipt = self.assemble()
        self.assertEqual(receipt["status"], "assembled_pending_native_verification")
        self.assertTrue(all(value is False for value in receipt["scope"].values()))
        for name, (relative, _, _) in candidate.WINDOWS.items():
            target = self.output / "client" / relative
            self.assertEqual(original[name], candidate.sha_file(target))
            self.assertEqual(original[name], candidate.sha_file(self.component(name)))
            self.assertEqual(target.stat().st_nlink, 1)
            self.assertFalse(os.path.samefile(target, self.component(name)))
        client = (self.output / "client/package/RELEASE-CATALOG.json").read_bytes()
        manifest = json.loads((self.output / "package/PUBLIC-SERVER-CATALOG.json").read_text())
        self.assertEqual(manifest["admitted_client"]["manifest_sha256"], candidate.digest(client))
        self.assertEqual(manifest["service"]["path"], "bin/FifthFreedom.Server")
        self.assertNotEqual(receipt["expected_native_result"]["service"]["manifest_sha256"], candidate.digest(client))
        self.assertNotIn("native_verification", receipt)

    def test_other_run_attempt_revision_or_dirty_failed_receipt_is_rejected_before_output(self):
        original = self.receipt()
        for key, bad in (("commit", "b" * 40), ("run_id", "999"), ("run_attempt", "1"),
                         ("schema", True),
                         ("tracked_source_dirty", True), ("github_actions", False), ("exit_code", 1),
                         ("status", "failed"), ("complete_package", True), ("deployed", True)):
            with self.subTest(field=key):
                self.save_lane(self.windows, "windows-components", {**original, key: bad})
                with self.assertRaisesRegex(candidate.CandidateError, "RUN_OR_SCOPE"):
                    self.assemble()
                self.assertFalse(self.output.exists())

    def test_tampered_bytes_cannot_be_hidden_by_unchanged_artifact_receipt(self):
        self.component().write_bytes(self.component().read_bytes()[:-1] + b"!")
        with self.assertRaisesRegex(candidate.CandidateError, "HASH_MISMATCH"):
            self.assemble()
        self.assertFalse(self.output.exists())

    def test_checksum_list_must_match_all_receipt_rows_without_duplicates(self):
        sums = self.folder(self.windows, "windows-components") / "components/SHA256SUMS.txt"
        sums.write_text(sums.read_text() + sums.read_text().splitlines()[0] + "\n")
        with self.assertRaisesRegex(candidate.CandidateError, "CHECKSUM_LIST"):
            self.assemble()

    def test_self_consistent_elf_substitution_for_windows_role_is_rejected(self):
        self.replace_component("FifthFreedom.Server.exe", elf(self.marker("server")))
        with self.assertRaisesRegex(candidate.CandidateError, "CLIENT_PE"):
            self.assemble()

    def test_wrong_windows_architecture_is_rejected_even_with_matching_hash_and_marker(self):
        self.replace_component("FifthFreedom.component-only.exe", pe(0x8664, False, self.marker("launcher")))
        with self.assertRaisesRegex(candidate.CandidateError, "CLIENT_PE"):
            self.assemble()

    def test_missing_declared_optional_header_is_not_a_valid_pe_component(self):
        value = bytearray(pe(0x14C, False, self.marker("launcher")))
        struct.pack_into("<H", value, 148, 0)
        self.replace_component("FifthFreedom.component-only.exe", bytes(value))
        with self.assertRaisesRegex(candidate.CandidateError, "CLIENT_PE"):
            self.assemble()

    def test_valid_pe_with_foreign_role_marker_is_rejected_by_original_producer(self):
        self.replace_component("FifthFreedom.component-only.exe", pe(0x14C, False, self.marker("server")))
        with self.assertRaisesRegex(candidate.CandidateError, "CATALOG_CONSTRUCTION"):
            self.assemble()

    def test_extra_component_row_cannot_expand_the_admission_catalog(self):
        receipt = self.receipt()
        receipt["components"]["private.key"] = receipt["components"]["FifthFreedom.component-only.exe"]
        self.save_lane(self.windows, "windows-components", receipt)
        with self.assertRaisesRegex(candidate.CandidateError, "COMPONENT_SET"):
            self.assemble()

    def test_unselected_artifact_files_never_enter_the_candidate(self):
        (self.windows / "private.key").write_text("SYNTHETIC_PRIVATE_CANARY")
        (self.component().parent / "account.sqlite").write_text("SYNTHETIC_PRIVATE_CANARY")
        self.assemble()
        self.assertTrue(all(b"SYNTHETIC_PRIVATE_CANARY" not in path.read_bytes()
                            for path in self.output.rglob("*") if path.is_file()))

    def test_duplicate_receipt_keys_are_rejected(self):
        path = self.folder(self.windows, "windows-components") / "receipt.json"
        path.write_text(path.read_text().rstrip()[:-1] + ', "run_id": "999"}')
        with self.assertRaisesRegex(candidate.CandidateError, "JSON_DUPLICATE"):
            self.assemble()

    def test_hardlinked_component_is_rejected(self):
        os.link(self.component(), self.root / "linked.exe")
        with self.assertRaisesRegex(candidate.CandidateError, "FILE_INVALID"):
            self.assemble()

    @unittest.skipIf(os.name == "nt", "Creating symlinks requires a distinct Windows privilege")
    def test_symlink_component_and_output_parent_are_rejected_without_writes(self):
        original = self.component()
        renamed = original.with_suffix(".saved")
        original.rename(renamed)
        original.symlink_to(renamed)
        with self.assertRaisesRegex(candidate.CandidateError, "LINK_REJECTED"):
            self.assemble()
        original.unlink()
        renamed.rename(original)
        outside = self.root / "outside"
        outside.mkdir()
        (self.root / "linked-parent").symlink_to(outside, target_is_directory=True)
        self.output = self.root / "linked-parent/output"
        with self.assertRaisesRegex(candidate.CandidateError, "OUTPUT_PARENT"):
            self.assemble()
        self.assertFalse((outside / "output").exists())

    def test_existing_output_is_preserved(self):
        self.output.mkdir()
        sentinel = self.output / "evidence"
        sentinel.write_bytes(b"preserve original")
        with self.assertRaisesRegex(candidate.CandidateError, "OUTPUT_ALREADY"):
            self.assemble()
        self.assertEqual(sentinel.read_bytes(), b"preserve original")

    def test_verifier_tampering_blocks_process_execution(self):
        receipt = self.assemble()
        (self.output / "tools/verify_public_package").write_bytes(elf("tampered"))
        with patch.object(subprocess, "run") as run:
            with self.assertRaisesRegex(candidate.CandidateError, "CHANGED_BEFORE"):
                candidate.verify_native(self.output, receipt)
            run.assert_not_called()

    def test_nonzero_or_wrong_native_identity_never_marks_assembly_verified(self):
        receipt = self.assemble()
        for result in (subprocess.CompletedProcess([], 1, b"", b"SYNTHETIC_ERROR"),
                       subprocess.CompletedProcess([], 0, b'{"schema":1,"result":"verified"}', b"")):
            with patch.object(subprocess, "run", return_value=result):
                with self.assertRaises(candidate.CandidateError):
                    candidate.verify_native(self.output, receipt)
        self.assertEqual(receipt["status"], "assembled_pending_native_verification")
        self.assertNotIn("native_verification", receipt)

    def metadata(self, **changes):
        info = self.component().stat()
        values = {name: getattr(info, name) for name in (
            "st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
        values.update(st_mode=stat.S_IFREG | 0o666, st_birthtime_ns=1, st_ctime_ns=1)
        return SimpleNamespace(**{**values, **changes})

    def test_windows_path_handle_metadata_semantics_preserve_real_byte_reads(self):
        path_info = self.metadata(st_mode=stat.S_IFREG | 0o777)
        handle_info = self.metadata(st_ctime_ns=2)
        expected = self.component().read_bytes()
        # Model CPython 3.12's path-stat birthtime/extension mode versus fstat
        # ChangeTime/no-extension mode while reading the real synthetic file.
        with patch.object(candidate.sys, "platform", "win32"), \
                patch.object(candidate, "regular", return_value=path_info), \
                patch.object(candidate.os, "fstat", return_value=handle_info):
            self.assertEqual(candidate.read_small(self.component()), expected)
            self.assertEqual(candidate.sha_file(self.component()),
                             {"bytes": len(expected), "sha256": candidate.digest(expected)})

    def test_windows_cross_api_binding_still_rejects_changed_common_metadata(self):
        path_info = self.metadata(st_mode=stat.S_IFREG | 0o777)
        replacements = {"st_dev": path_info.st_dev + 1, "st_ino": path_info.st_ino + 1,
                        "st_mode": stat.S_IFREG | 0o444, "st_nlink": 2,
                        "st_size": path_info.st_size + 1, "st_mtime_ns": path_info.st_mtime_ns + 1,
                        "st_birthtime_ns": 3}
        for reader in (candidate.read_small, candidate.sha_file):
            for field, value in replacements.items():
                with self.subTest(reader=reader.__name__, field=field), \
                        patch.object(candidate.sys, "platform", "win32"), \
                        patch.object(candidate, "regular", return_value=path_info), \
                        patch.object(candidate.os, "fstat", return_value=self.metadata(st_ctime_ns=2, **{field: value})):
                    with self.assertRaisesRegex(candidate.CandidateError, "INPUT_CHANGED"):
                        reader(self.component())

    def test_windows_full_handle_metadata_changes_during_read_are_rejected(self):
        for reader in (candidate.read_small, candidate.sha_file):
            for change in ({"st_ctime_ns": 3}, {"st_mode": stat.S_IFREG | 0o777}):
                with self.subTest(reader=reader.__name__, field=next(iter(change))), \
                        patch.object(candidate.sys, "platform", "win32"), \
                        patch.object(candidate, "regular", return_value=self.metadata()), \
                        patch.object(candidate.os, "fstat", side_effect=[
                            self.metadata(st_ctime_ns=2), self.metadata(**{"st_ctime_ns": 2, **change})]):
                    with self.assertRaisesRegex(candidate.CandidateError, "INPUT_CHANGED"):
                        reader(self.component())

    def test_windows_full_path_metadata_changes_during_read_are_rejected(self):
        for reader in (candidate.read_small, candidate.sha_file):
            for change in ({"st_ctime_ns": 3}, {"st_mode": stat.S_IFREG | 0o777}):
                with self.subTest(reader=reader.__name__, field=next(iter(change))), \
                        patch.object(candidate.sys, "platform", "win32"), \
                        patch.object(candidate, "regular", side_effect=[self.metadata(), self.metadata(**change)]), \
                        patch.object(candidate.os, "fstat", return_value=self.metadata(st_ctime_ns=2)):
                    with self.assertRaises(candidate.CandidateError):
                        reader(self.component())

    def test_linux_cross_api_binding_keeps_full_mode_and_ctime_checks(self):
        for reader in (candidate.read_small, candidate.sha_file):
            for change in ({"st_ctime_ns": 3}, {"st_mode": stat.S_IFREG | 0o777}):
                with self.subTest(reader=reader.__name__, field=next(iter(change))), \
                        patch.object(candidate.sys, "platform", "linux"), \
                        patch.object(candidate, "regular", return_value=self.metadata()), \
                        patch.object(candidate.os, "fstat", return_value=self.metadata(**change)):
                    with self.assertRaisesRegex(candidate.CandidateError, "INPUT_CHANGED"):
                        reader(self.component())


if __name__ == "__main__":
    unittest.main()
