"""Synthetic source archives and a real local git-apply; no dependency build/run."""
import copy
import io
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_prepare_easytier_source as prepare


class CleanSourcePreparationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-clean-source-unit-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.output = self.root / "source"
        self.original = b"fn secure() { old(); }\n"
        self.patched = b"fn secure() { guarded(); }\n"
        self.items = {"Cargo.lock": b"SYNTHETIC LOCK\n", "LICENSE": b"SYNTHETIC SOURCE NOTICE\n",
                      "easytier/secure.rs": self.original, "assets/readme.txt": b"source asset\n",
                      "easytier/third_party/Packet.dll": b"MZ_SYNTHETIC_HELD_PREBUILT"}
        self.links = {"easytier/LICENSE": "../LICENSE", "easytier/assets": "../assets"}
        patch = (b"diff --git a/easytier/secure.rs b/easytier/secure.rs\n"
                 b"--- a/easytier/secure.rs\n+++ b/easytier/secure.rs\n@@ -1 +1 @@\n-"
                 + self.original + b"+" + self.patched)
        (self.root / "guard.patch").write_bytes(patch)
        self.lock = {"schema": 1, "upstream": {"commit": "a" * 40},
                     "patches": [{"path": "guard.patch", **self.row(patch)}],
                     "cargo_lock_sha256": prepare.sha(self.items["Cargo.lock"]),
                     "modified_files": [{"path": "easytier/secure.rs", "original_sha256": prepare.sha(self.original),
                                         "patched_sha256": prepare.sha(self.patched)}],
                     "omitted_files": [{"path": "easytier/third_party/Packet.dll", **self.row(self.items["easytier/third_party/Packet.dll"]),
                                        "reason": "synthetic non-Linux prebuilt"}],
                     "materialized_links": [{"path": path, "target": target} for path, target in self.links.items()],
                     "required_notices": [{"path": "LICENSE", **self.row(self.items["LICENSE"])}],
                     "build": {"target": "x86_64-unknown-linux-gnu", "rust_toolchain": "1.95.0"},
                     "admission": {"runtime_admission_ready": False, "runtime_publication_approved": False}}
        self.archive = self.root / "original.tar"
        self.write_archive()

    def row(self, data):
        return {"bytes": len(data), "sha256": prepare.sha(data)}

    def write_archive(self, extra=()):
        with tarfile.open(self.archive, "w") as archive:
            for name, data in self.items.items():
                info = tarfile.TarInfo(name)
                info.size, info.mode = len(data), 0o644
                archive.addfile(info, io.BytesIO(data))
            for name, target in self.links.items():
                info = tarfile.TarInfo(name)
                info.type, info.linkname, info.mode = tarfile.SYMTYPE, target, 0o777
                archive.addfile(info)
            for info, data in extra:
                archive.addfile(info, io.BytesIO(data))
        self.lock["archive"] = self.row(self.archive.read_bytes())

    def run_prepare(self):
        return prepare.prepare(self.archive, self.output, self.lock, self.root)

    def test_real_patch_and_exact_omissions_preserve_original_and_required_sources(self):
        original = self.archive.read_bytes()
        result = self.run_prepare()
        self.assertEqual(self.archive.read_bytes(), original)
        self.assertEqual((self.output / "easytier/secure.rs").read_bytes(), self.patched)
        self.assertEqual((self.output / "Cargo.lock").read_bytes(), self.items["Cargo.lock"])
        self.assertFalse((self.output / "easytier/third_party/Packet.dll").exists())
        self.assertEqual((self.output / "easytier/LICENSE").read_bytes(), self.items["LICENSE"])
        self.assertEqual((self.output / "easytier/assets/readme.txt").read_bytes(), self.items["assets/readme.txt"])
        self.assertFalse(any(path.is_symlink() for path in self.output.rglob("*")))
        self.assertFalse(result["complete_upstream_snapshot"])
        self.assertFalse(result["dependency_source_closure_verified"])
        self.assertFalse(result["runtime_built"])
        self.assertFalse(result["admission"]["runtime_admission_ready"])
        self.assertEqual(result["source_tree_sha256"], prepare.sha(prepare.encoded(result["source_files"])))

    def test_archive_digest_and_patch_digest_are_checked_before_output(self):
        data = self.archive.read_bytes()
        self.archive.write_bytes(data[:-1] + b"!")
        with self.assertRaisesRegex(prepare.PreparationError, "DIGEST_MISMATCH"):
            self.run_prepare()
        self.assertFalse(self.output.exists())
        self.archive.write_bytes(data)
        patch = self.root / "guard.patch"
        patch.write_bytes(patch.read_bytes()[:-1] + b"!")
        with self.assertRaisesRegex(prepare.PreparationError, "DIGEST_MISMATCH"):
            self.run_prepare()
        self.assertFalse(self.output.exists())

    def test_two_fresh_trees_have_identical_receipts_without_absolute_paths(self):
        first = self.run_prepare()
        self.output = self.root / "separate-source"
        second = self.run_prepare()
        self.assertEqual(prepare.encoded(first), prepare.encoded(second))
        self.assertNotIn(str(self.root), prepare.encoded(second).decode())

    def test_patch_application_uses_the_verified_snapshot_if_original_path_changes(self):
        read = prepare.checked_bytes
        def changed_after_read(path, row, maximum=prepare.MAX_ARCHIVE):
            data = read(path, row, maximum)
            if path.name == "guard.patch":
                path.write_bytes(b"UNREVIEWED_REPLACEMENT_MUST_NOT_BE_APPLIED")
            return data
        with patch.object(prepare, "checked_bytes", side_effect=changed_after_read):
            result = self.run_prepare()
        self.assertEqual((self.output / "easytier/secure.rs").read_bytes(), self.patched)
        self.assertEqual(result["source_files"]["easytier/secure.rs"]["sha256"], prepare.sha(self.patched))

    def test_untrusted_paths_duplicates_and_special_members_never_write_outside(self):
        for name in ("../outside", "/absolute", "a/../../outside", "a\\outside", "a/.git/config", "Cargo.lock"):
            with self.subTest(name=name):
                info = tarfile.TarInfo(name)
                info.size = 1
                self.write_archive([(info, b"x")])
                with self.assertRaisesRegex(prepare.PreparationError, "LAYOUT_INVALID"):
                    self.run_prepare()
                self.assertFalse(self.output.exists())
        info = tarfile.TarInfo("hardlink")
        info.type, info.linkname = tarfile.LNKTYPE, "Cargo.lock"
        self.write_archive([(info, b"")])
        with self.assertRaisesRegex(prepare.PreparationError, "SPECIAL_ENTRY"):
            self.run_prepare()
        self.assertFalse(self.output.exists())

    def test_omission_set_and_unreviewed_prebuilt_inputs_fail_closed(self):
        original = copy.deepcopy(self.lock)
        self.lock["omitted_files"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(prepare.PreparationError, "OMISSION_MISMATCH"):
            self.run_prepare()
        self.lock = original
        self.items["another/input.txt"] = b"\x7fELF_SYNTHETIC_PREBUILT"
        self.write_archive()
        with self.assertRaisesRegex(prepare.PreparationError, "UNREVIEWED_PREBUILT"):
            self.run_prepare()
        self.assertFalse(self.output.exists())

    def test_unreviewed_links_and_outside_targets_are_not_followed(self):
        self.links["easytier/LICENSE"] = "../../outside"
        self.write_archive()
        with self.assertRaisesRegex(prepare.PreparationError, "LINK_SET_MISMATCH"):
            self.run_prepare()
        self.lock["materialized_links"][0]["target"] = "../../outside"
        with self.assertRaisesRegex(prepare.PreparationError, "LINK_TARGET_INVALID"):
            self.run_prepare()
        self.assertFalse(self.output.exists())

    def test_cargo_lock_and_required_notice_cannot_be_silently_changed(self):
        for path in ("Cargo.lock", "LICENSE"):
            original = self.items[path]
            self.items[path] = b"changed source input\n"
            self.write_archive()
            with self.subTest(path=path), self.assertRaises(prepare.PreparationError):
                self.run_prepare()
            self.assertFalse(self.output.exists())
            self.items[path] = original

    def test_unexpected_patch_change_is_rejected_against_reviewed_after_hash(self):
        self.lock["modified_files"][0]["patched_sha256"] = "0" * 64
        with self.assertRaisesRegex(prepare.PreparationError, "PATCH_SCOPE_OR_DIGEST_MISMATCH"):
            self.run_prepare()
        self.assertFalse((self.output / "FIFTH-FREEDOM-SOURCE-RECEIPT.json").exists())

    def test_existing_source_evidence_cannot_be_overwritten(self):
        self.output.mkdir()
        sentinel = self.output / "existing"
        sentinel.write_bytes(b"original evidence")
        with self.assertRaisesRegex(prepare.PreparationError, "OUTPUT_EXISTS"):
            self.run_prepare()
        self.assertEqual(sentinel.read_bytes(), b"original evidence")


if __name__ == "__main__":
    unittest.main()
