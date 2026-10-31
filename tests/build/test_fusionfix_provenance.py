"""Static exact-input provenance contracts; no PE or game is executed."""
import json
import os
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import fusionfix_provenance as provenance
import fusionfix_corresponding_source as source


class FusionFixProvenanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.record = json.loads((ROOT / "docs/fusionfix-provenance.json").read_bytes())
        cls.archive = (ROOT / cls.record["frozen_archive"]["path"]).read_bytes()
        cls.loader = provenance.archive_member(cls.archive, cls.record["members"]["loader"]["path"], cls.record["members"]["loader"])

    def copied_root(self, directory):
        root = Path(directory)
        paths = ["docs/fusionfix-provenance.json", "scripts/local-assets.json",
            self.record["frozen_archive"]["path"], self.record["frozen_archive"]["donor_path"],
            *(item["notice_path"] for item in self.record["notices"])]
        for path in paths:
            destination = root / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / path, destination)
        shutil.copytree(ROOT / source.REFERENCE, root / source.REFERENCE)
        shutil.copyfile(ROOT / source.MIT_PATH, root / source.MIT_PATH)
        return root

    def test_real_frozen_identity_keeps_rebuild_and_permission_holds(self):
        result = provenance.verify()
        self.assertEqual(result["status"], "identity_and_notices_verified")
        self.assertEqual(result["upstream_asset"], "not_rechecked_in_this_run")
        self.assertEqual((result["notices_checked"], result["embedded_inputs_checked"]), (7, 3))
        self.assertIn("ASI_EXACT_SOURCE_AND_BUILD_IDENTITY", result["unresolved_holds"])
        self.assertFalse(result["permission_closure"])
        self.assertFalse(result["independently_rebuilt"])
        self.assertFalse(result["binary_executed"])

    def test_version_reads_root_resource_instead_of_embedded_dll_strings(self):
        meta, resources = provenance.inspect_member(self.loader)
        self.assertEqual(meta["version_resources"][0]["resource_path"], [16, 1, 1024])
        fields = meta["version_resources"][0]["fields"]
        self.assertEqual(fields["ProductVersion"], "9.7.0")
        self.assertEqual(fields["GitHash"], "f9f6ccf")
        self.assertEqual(fields["CompanyName"], "ThirteenAG")
        # These genuine strings occur in embedded binaries, not root metadata.
        self.assertIn("RAD".encode("utf-16le"), self.loader)
        self.assertNotIn("RAD", fields["CompanyName"])
        self.assertEqual(len([path for path in resources if path[0] == 10]), 4)

    def test_modified_archive_notice_and_independent_asset_pin_are_rejected(self):
        for changed in ("archive", "notice", "asset_pin"):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as directory:
                root = self.copied_root(directory)
                if changed == "asset_pin":
                    path = root / "scripts/local-assets.json"
                    value = json.loads(path.read_bytes())
                    value["files"][self.record["frozen_archive"]["path"]]["sha256"] = "0" * 64
                    path.write_text(json.dumps(value))
                else:
                    path = root / (self.record["frozen_archive"]["path"] if changed == "archive" else self.record["notices"][0]["notice_path"])
                    content = bytearray(path.read_bytes()); content[-1] ^= 1; path.write_bytes(content)
                with self.assertRaisesRegex(provenance.EvidenceError, "INPUT_HASH_MISMATCH"):
                    provenance.verify(root)

    def test_wrong_upstream_archive_never_substitutes_frozen_input(self):
        with tempfile.TemporaryDirectory() as directory:
            candidate = Path(directory) / "wrong.zip"
            candidate.write_bytes(self.archive)
            with self.assertRaisesRegex(provenance.EvidenceError, "INPUT_HASH_MISMATCH"):
                provenance.verify(upstream_zip=candidate)
        self.assertEqual((ROOT / self.record["frozen_archive"]["path"]).read_bytes(), self.archive)

    def test_pe_truncation_virtual_only_mapping_and_resource_cycle_fail(self):
        for content in (self.loader[:64], self.loader[:512]):
            with self.assertRaises(provenance.EvidenceError): provenance.pe_resources(content)
        pe = struct.unpack_from("<I", self.loader, 60)[0]
        count = struct.unpack_from("<H", self.loader, pe + 6)[0]
        optional = struct.unpack_from("<H", self.loader, pe + 20)[0]
        rva, size = struct.unpack_from("<II", self.loader, pe + 24 + 112)
        section = next(pe + 24 + optional + index * 40 for index in range(count)
            if struct.unpack_from("<I", self.loader, pe + 24 + optional + index * 40 + 12)[0] == rva)
        changed = bytearray(self.loader); struct.pack_into("<I", changed, section + 16, 0)
        with self.assertRaisesRegex(provenance.EvidenceError, "PE_RESOURCE_MAPPING_INVALID"):
            provenance.pe_resources(changed)
        base = struct.unpack_from("<I", self.loader, section + 20)[0]
        entries = struct.unpack_from("<H", self.loader, base + 14)[0]
        resource = next(base + 16 + index * 8 for index in range(entries)
            if struct.unpack_from("<I", self.loader, base + 16 + index * 8)[0] == 10)
        changed = bytearray(self.loader); struct.pack_into("<I", changed, resource + 4, 0x80000000)
        with self.assertRaisesRegex(provenance.EvidenceError, "PE_RESOURCE_TREE_INVALID"):
            provenance.pe_resources(changed)
        changed = bytearray(self.loader); struct.pack_into("<H", changed, base + 14, 65535)
        with self.assertRaisesRegex(provenance.EvidenceError, "PE_RESOURCE_TREE_INVALID"):
            provenance.pe_resources(changed)

    def test_regular_bounded_snapshot_rejects_directory_and_oversize(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(provenance.EvidenceError): provenance.read_snapshot(Path(directory), 32)
            path = Path(directory) / "file"; path.write_bytes(b"x" * 33)
            with self.assertRaises(provenance.EvidenceError): provenance.read_snapshot(path, 32)
            if hasattr(os, "mkfifo"):
                fifo = Path(directory) / "fifo"; os.mkfifo(fifo)
                with self.assertRaises(provenance.EvidenceError): provenance.read_snapshot(fifo, 32)

    def test_package_notices_preserve_exact_bytes_and_reject_before_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.copied_root(Path(directory) / "source")
            destination = Path(directory) / "licenses/FusionFix"
            rows = provenance.copy_notices(destination, root)
            self.assertEqual(len(rows), 50)
            for name, expected in rows.items():
                self.assertEqual(provenance.identity((destination / name).read_bytes()), expected)
            self.assertEqual((destination / "PROVENANCE.json").read_bytes(),
                             (root / "docs/fusionfix-provenance.json").read_bytes())
            self.assertFalse(json.loads((destination / "PROVENANCE.json").read_bytes())["independent_permission_closure"])
            with self.assertRaises(FileExistsError):
                provenance.copy_notices(destination, root)
            last = root / self.record["notices"][-1]["notice_path"]
            last.write_bytes(last.read_bytes() + b"changed")
            rejected = Path(directory) / "rejected"
            with self.assertRaisesRegex(provenance.EvidenceError, "INPUT_HASH_MISMATCH"):
                provenance.copy_notices(rejected, root)
            self.assertFalse(rejected.exists())


if __name__ == "__main__":
    unittest.main()
