"""Static PE import contracts; no image is loaded or executed."""
import struct
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from pe_imports import pe_imports


def fixture(wide=True, legacy_delay=False):
    data = bytearray(0xA00)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 60, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x86, 1)
    struct.pack_into("<H", data, 0x94, 240 if wide else 224)
    optional = 0x98
    struct.pack_into("<H", data, optional, 0x20B if wide else 0x10B)
    struct.pack_into("<Q" if wide else "<I", data,
                     optional + (24 if wide else 28), 0x400000)
    struct.pack_into("<I", data, optional + 60, 0x200)
    directory = optional + (112 if wide else 96)
    struct.pack_into("<I", data, directory - 4, 16)
    struct.pack_into("<II", data, directory + 8, 0x1000, 40)
    struct.pack_into("<II", data, directory + 13 * 8, 0x1100, 64)
    section = optional + (240 if wide else 224)
    data[section:section + 5] = b".data"
    struct.pack_into("<IIII", data, section + 8, 0x800, 0x1000, 0x800, 0x200)
    struct.pack_into("<IIIII", data, 0x200, 0x1400, 0, 0, 0x1200, 0x1400)
    struct.pack_into("<IIIIIIII", data, 0x300, 0 if legacy_delay else 1,
                     0x401220 if legacy_delay else 0x1220, 0, 0x1400, 0, 0, 0, 0)
    data[0x400:0x400 + 13] = b"KERNEL32.dll\0"
    data[0x420:0x420 + 11] = b"Packet.DLL\0"
    return data, directory, section


class PeImportsTests(unittest.TestCase):
    def assert_invalid(self, data):
        with self.assertRaisesRegex(ValueError, "^PE_IMPORTS_INVALID$"):
            pe_imports(bytes(data))

    def test_normal_and_delay_pe32_and_pe32plus(self):
        for wide in (False, True):
            with self.subTest(wide=wide):
                self.assertEqual(pe_imports(bytes(fixture(wide)[0])),
                    {"schema": 1, "normal": ["kernel32.dll"], "delay": ["packet.dll"]})

    def test_legacy_delay_virtual_address_is_converted(self):
        self.assertEqual(pe_imports(bytes(fixture(False, True)[0]))["delay"], ["packet.dll"])

    def test_absent_directories_are_empty(self):
        data, directory, _ = fixture()
        data[directory:directory + 128] = bytes(128)
        self.assertEqual(pe_imports(bytes(data)), {"schema": 1, "normal": [], "delay": []})

    def test_import_names_are_deduplicated_and_sorted(self):
        data, directory, _ = fixture()
        struct.pack_into("<I", data, directory + 12, 80)
        data[0x214:0x228] = data[0x200:0x214]
        data[0x228:0x23C] = data[0x200:0x214]
        struct.pack_into("<I", data, 0x228 + 12, 0x1220)
        self.assertEqual(pe_imports(bytes(data))["normal"], ["kernel32.dll", "packet.dll"])

    def test_truncated_headers_and_sections_are_rejected(self):
        original = fixture()[0]
        for end in (0, 63, 128, 152, 300, 511, 512, len(original) - 1):
            with self.subTest(end=end):
                self.assert_invalid(original[:end])

    def test_unknown_optional_header_is_rejected(self):
        data, _, _ = fixture()
        struct.pack_into("<H", data, 0x98, 0x107)
        self.assert_invalid(data)

    def test_unterminated_import_directory_is_rejected(self):
        data, directory, _ = fixture()
        struct.pack_into("<I", data, directory + 12, 20)
        self.assert_invalid(data)

    def test_virtual_only_directory_is_rejected(self):
        data, directory, section = fixture()
        struct.pack_into("<I", data, section + 8, 0x1000)
        struct.pack_into("<I", data, directory + 8, 0x1800)
        self.assert_invalid(data)

    def test_directory_must_fit_one_backed_region(self):
        data, directory, _ = fixture()
        struct.pack_into("<I", data, directory + 8, 0x17F0)
        self.assert_invalid(data)

    def test_overlapping_virtual_or_raw_sections_are_rejected(self):
        for raw_overlap in (False, True):
            data, _, section = fixture()
            struct.pack_into("<H", data, 0x86, 2)
            data[section + 40:section + 80] = data[section:section + 40]
            if raw_overlap:
                struct.pack_into("<I", data, section + 40 + 12, 0x2000)
            self.assert_invalid(data)

    def test_invalid_delay_attributes_and_legacy_underflow_are_rejected(self):
        for attributes, name in ((2, 0x1220), (0, 0x1000)):
            data, _, _ = fixture(False)
            struct.pack_into("<II", data, 0x300, attributes, name)
            self.assert_invalid(data)

    def test_bad_name_characters_paths_and_missing_nul_are_rejected(self):
        for name in (b"../secret.dll\0", b"C:\\secret.dll\0", b"bad name.dll\0",
                     b"x\xff.dll\0", b"x.exe\0", b"x" * 256, b"\0"):
            with self.subTest(name=name[:30]):
                data, _, _ = fixture()
                data[0x400:0x500] = b"x" * 256
                data[0x400:0x400 + len(name)] = name
                self.assert_invalid(data)

    def test_directory_count_cannot_escape_optional_header(self):
        data, directory, _ = fixture()
        struct.pack_into("<I", data, directory - 4, 17)
        self.assert_invalid(data)

    def test_existing_capture_image_is_inspected_without_execution(self):
        # The public retained binary is an independent real linker-produced PE.
        path = ROOT / "src/launcher/src/Assets/SCBL.LAN.Capture.exe"
        result = pe_imports(path.read_bytes())
        self.assertIn("kernel32.dll", result["normal"])
        self.assertEqual(result["delay"], [])


if __name__ == "__main__":
    unittest.main()
