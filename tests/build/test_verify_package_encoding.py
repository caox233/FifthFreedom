"""Package integrity must not depend on the runner's default text encoding.

These are owned, non-executable integrity fixtures, not distributable packages.
Only the locale-default text-opening boundary is controlled; the production
verifier reads real files and computes every digest itself.
"""
from contextlib import contextmanager, redirect_stdout
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import verify_package


@contextmanager
def cp1252_default_text_reads():
    """Model a Windows locale default without changing explicitly chosen UTF-8."""
    actual_open = Path.open

    def locale_open(path, mode="r", buffering=-1, encoding=None, errors=None, newline=None):
        if "b" not in mode and encoding in (None, "locale"):
            encoding = "cp1252"
        return actual_open(path, mode, buffering, encoding, errors, newline)

    with patch.object(Path, "open", locale_open):
        yield


class PackageChecksumEncodingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="package-unicode-integrity-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.notes_name = "更新公告.txt"
        identity = {"release_id": "owned-integrity-fixture", "compat_id": "fixture-compat"}
        components = []
        for role, name in (
            ("launcher", "FifthFreedom.exe"),
            ("main", "runtime/uplay_r1_loader.dll"),
            ("gameplay", "runtime/scbl_gameplay.dll"),
            ("server", "runtime/FifthFreedom.Server.exe"),
        ):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            data = ("owned non-executable " + role).encode("ascii")
            path.write_bytes(data)
            components.append({"role": role, "path": name, **identity, **self.digest(data)})
        (self.root / self.notes_name).write_bytes("测试更新说明\n".encode("utf-8"))
        package = self.root / "package"
        package.mkdir()
        self.catalog = {"schema": 1, **identity, "components": components}
        (package / "RELEASE-CATALOG.json").write_text(json.dumps(self.catalog), encoding="utf-8")
        files = {path.relative_to(self.root).as_posix(): self.digest(path.read_bytes())
                 for path in self.root.rglob("*") if path.is_file()}
        (package / "PACKAGE-MANIFEST.json").write_text(json.dumps({"files": files}), encoding="utf-8")
        self.sums = package / "SHA256SUMS.txt"
        # Match the real packager's UTF-8 names and Windows line endings.
        rows = "".join(f"{self.digest(path.read_bytes())['sha256']}  {path.relative_to(self.root).as_posix()}\r\n"
                       for path in sorted(self.root.rglob("*")) if path.is_file())
        self.sums.write_bytes(rows.encode("utf-8"))

    @staticmethod
    def digest(data):
        return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}

    def verify_with_windows_default(self):
        with cp1252_default_text_reads(), redirect_stdout(io.StringIO()):
            return verify_package.verify(self.root)

    def test_utf8_checksum_filename_survives_cp1252_default(self):
        original = {path.relative_to(self.root).as_posix(): path.read_bytes()
                    for path in self.root.rglob("*") if path.is_file()}
        wrong_name = self.notes_name.encode("utf-8").decode("cp1252")
        self.assertNotEqual(wrong_name, self.notes_name)
        self.assertFalse((self.root / wrong_name).exists())
        self.assertEqual(self.verify_with_windows_default(), self.catalog)
        self.assertEqual(original, {path.relative_to(self.root).as_posix(): path.read_bytes()
                                    for path in self.root.rglob("*") if path.is_file()})

    def test_corrupted_unicode_checksum_is_rejected(self):
        original = self.sums.read_bytes()
        actual = hashlib.sha256((self.root / self.notes_name).read_bytes()).hexdigest().encode("ascii")
        self.assertEqual(original.count(actual), 1)
        self.sums.write_bytes(original.replace(actual, b"0" * 64))
        with self.assertRaisesRegex(AssertionError, self.notes_name):
            self.verify_with_windows_default()

    def test_corrupted_unicode_payload_is_rejected(self):
        path = self.root / self.notes_name
        old = path.read_bytes()
        path.write_bytes(bytes([old[0] ^ 1]) + old[1:])
        with self.assertRaisesRegex(AssertionError, self.notes_name):
            self.verify_with_windows_default()

    def test_non_utf8_checksum_file_is_rejected_without_fallback(self):
        # A locale-compatible but invalid UTF-8 name must not select a second
        # interpretation of the checksum file after the specified format fails.
        self.sums.write_bytes(self.sums.read_bytes() + b"0" * 64 + b"  \xff.txt\r\n")
        with self.assertRaises(UnicodeDecodeError):
            self.verify_with_windows_default()


if __name__ == "__main__":
    unittest.main()
