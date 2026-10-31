"""Capture migration receipts must distinguish identity from rebuild/publication."""
import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import capture_helper_receipt as receipt


def synthetic_pe(machine=0x14C, characteristics=0x102, magic=0x10B):
    data = bytearray(256)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 64)
    data[64:68] = b"PE\0\0"
    struct.pack_into("<H", data, 68, machine)
    struct.pack_into("<HHH", data, 84, 96, characteristics, magic)
    return bytes(data)


class CaptureHelperReceiptTests(unittest.TestCase):
    def test_retained_real_input_does_not_close_absent_source_gate(self):
        result, code = receipt.inspect()
        self.assertEqual(code, 2)
        self.assertEqual(result["status"], "blocked_source_not_supplied")
        self.assertTrue(result["retained_binary"]["manifest_verified"])
        self.assertTrue(result["retained_binary"]["sha256_size_and_x86_pe_verified"])
        self.assertFalse(result["reported_source"]["digest_verified"])

    def test_source_identity_is_separate_from_rebuild_and_publication(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "private-local-source.cpp"
            source.write_bytes(b"synthetic source only")
            with patch.object(receipt, "REPORTED_SOURCE_SHA256", hashlib.sha256(source.read_bytes()).hexdigest()):
                result, code = receipt.inspect(source=source)
        self.assertEqual(code, 0)
        self.assertTrue(result["reported_source"]["digest_verified"])
        for key in ("rebuilt_binary_verified", "source_binary_equivalence_verified",
                    "publication_approved", "capture_executed", "source_contents_included"):
            self.assertFalse(result[key])
        serialized = json.dumps(result)
        self.assertNotIn("private-local-source", serialized)
        self.assertNotIn("synthetic source only", serialized)
        self.assertIn("review_source_and_build_recipe", result["remaining_gates"])

    def test_mismatching_and_missing_source_fail_without_exposing_path(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "sensitive-local-input.cpp"
            for exists in (False, True):
                if exists:
                    source.write_bytes(b"not the reported source")
                result, code = receipt.inspect(source=source)
                self.assertEqual(code, 1)
                self.assertIn("reported_source_digest_mismatch_or_unavailable", result["errors"])
                self.assertNotIn("sensitive-local-input", json.dumps(result))

    def test_independently_changed_manifest_cannot_admit_replacement_binary(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            binary = synthetic_pe()
            target = root / receipt.BINARY_PATH
            target.parent.mkdir(parents=True)
            target.write_bytes(binary)
            manifest = root / "scripts/local-assets.json"
            manifest.parent.mkdir()
            manifest.write_text(json.dumps({"files": {receipt.BINARY_PATH: {
                "sha256": hashlib.sha256(binary).hexdigest(), "bytes": len(binary)}}}))
            result, code = receipt.inspect(root=root)
            self.assertEqual(code, 1)
            self.assertFalse(result["retained_binary"]["manifest_verified"])
            self.assertFalse(result["retained_binary"]["sha256_size_and_x86_pe_verified"])

    def test_pe_parser_rejects_other_architectures_dll_and_truncated_headers(self):
        self.assertTrue(receipt.is_x86_executable(synthetic_pe()))
        for data in (b"MZ", synthetic_pe(machine=0x8664), synthetic_pe(magic=0x20B),
                     synthetic_pe(characteristics=0x2102), synthetic_pe()[:150]):
            with self.subTest(length=len(data)):
                self.assertFalse(receipt.is_x86_executable(data))
        data = bytearray(synthetic_pe())
        struct.pack_into("<I", data, 0x3C, 0xFFFFFFFF)
        self.assertFalse(receipt.is_x86_executable(bytes(data)))

    def test_oversized_source_and_directory_are_not_read_as_sources(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "input.cpp"
            source.write_bytes(b"12345")
            with self.assertRaisesRegex(ValueError, "input_too_large"):
                receipt.read_regular(source, 4)
            with self.assertRaisesRegex(ValueError, "nonregular_input"):
                receipt.read_regular(Path(folder), 4)

    def test_linked_input_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            source, link = Path(folder) / "input.cpp", Path(folder) / "link.cpp"
            source.write_bytes(b"synthetic source")
            try:
                link.symlink_to(source)
            except OSError:
                self.skipTest("This platform/account cannot create symbolic links")
            with self.assertRaisesRegex(ValueError, "symlink_input"):
                receipt.read_regular(link, 1024)


if __name__ == "__main__":
    unittest.main()
