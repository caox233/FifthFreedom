import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/public_candidate.py"
spec = importlib.util.spec_from_file_location("public_candidate", SCRIPT)
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)


class PublicCandidateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="fifth-freedom-candidate-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "checkout"
        self.package = self.source / "src/public/fifth_freedom_control"
        self.package.mkdir(parents=True)
        (self.package / "__init__.py").write_text("")
        (self.package / "__main__.py").write_text("def main():\n    print('synthetic candidate entry')\n    return 0\n")
        (self.package / "NOTICE.md").write_text("synthetic source notice")
        (self.source / "LICENSE").write_text("synthetic license")
        self.output = self.root / "candidate"
        self.revision = "a" * 40

    def build(self):
        return candidate.build_candidate(self.source, self.output, self.revision)

    def manifest(self):
        return json.loads((self.output / candidate.MANIFEST).read_text())

    def save_manifest(self, manifest):
        (self.output / candidate.MANIFEST).write_text(json.dumps(manifest))

    def rewrite_archive(self, entries):
        payload = io.BytesIO()
        with zipfile.ZipFile(payload, "w") as archive:
            for name, data in entries:
                info = zipfile.ZipInfo(name)
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | 0o644) << 16
                archive.writestr(info, data)
        value = payload.getvalue()
        (self.output / candidate.PROGRAM).write_bytes(value)
        manifest = self.manifest()
        manifest["program"] = {"name": candidate.PROGRAM, "bytes": len(value),
                               "sha256": hashlib.sha256(value).hexdigest()}
        manifest["files"] = {name: {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                             for name, data in entries}
        self.save_manifest(manifest)

    def entries(self):
        with zipfile.ZipFile(self.output / candidate.PROGRAM) as archive:
            return [(name, archive.read(name)) for name in archive.namelist()]

    def test_reproducible_and_runnable_without_checkout(self):
        first = self.build()
        second = self.root / "repeat"
        candidate.build_candidate(self.source, second, self.revision)
        self.assertEqual((self.output / candidate.PROGRAM).read_bytes(), (second / candidate.PROGRAM).read_bytes())
        self.assertEqual((self.output / candidate.MANIFEST).read_bytes(), (second / candidate.MANIFEST).read_bytes())
        shutil.rmtree(self.source)
        self.assertEqual(first, candidate.verify_candidate(self.output))
        result = subprocess.run([sys.executable, "-I", str(self.output / candidate.PROGRAM)],
                                cwd=self.root, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("synthetic candidate entry", result.stdout.strip())
        self.assertFalse(first["complete_game_package"])
        self.assertFalse(first["native_public_profile_enabled"])

    def test_runtime_inputs_are_excluded_and_license_preserved(self):
        for name in ("runtime.json", "issuer.key", "tls.pem", "accounts.db", "private.SAV"):
            (self.package / name).write_bytes(b"SYNTHETIC_PRIVATE_CANARY")
        self.build()
        with zipfile.ZipFile(self.output / candidate.PROGRAM) as archive:
            self.assertEqual(b"synthetic license", archive.read("LICENSE"))
            self.assertEqual(b"synthetic source notice", archive.read("fifth_freedom_control/NOTICE.md"))
            self.assertTrue(all(b"SYNTHETIC_PRIVATE_CANARY" not in archive.read(name) for name in archive.namelist()))

    def test_corruption_or_extra_files_rejected(self):
        self.build()
        original = (self.output / candidate.PROGRAM).read_bytes()
        (self.output / candidate.PROGRAM).write_bytes(original + b"tamper")
        with self.assertRaises(candidate.CandidateError):
            candidate.verify_candidate(self.output)
        (self.output / candidate.PROGRAM).write_bytes(original)
        (self.output / "private.key").write_bytes(b"synthetic")
        with self.assertRaises(candidate.CandidateError):
            candidate.verify_candidate(self.output)

    def test_self_consistent_traversal_archive_rejected_without_extraction(self):
        self.build()
        self.rewrite_archive(self.entries() + [("../escaped.py", b"print('no')")])
        with self.assertRaises(candidate.CandidateError):
            candidate.verify_candidate(self.output)
        self.assertFalse((self.root / "escaped.py").exists())

    def test_duplicate_zip_members_rejected(self):
        self.build()
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            entries = self.entries()
            self.rewrite_archive(entries + [entries[0]])
        with self.assertRaises(candidate.CandidateError):
            candidate.verify_candidate(self.output)

    def test_manifest_cannot_claim_full_runtime(self):
        self.build()
        manifest = self.manifest()
        manifest["native_public_profile_enabled"] = True
        self.save_manifest(manifest)
        with self.assertRaises(candidate.CandidateError):
            candidate.verify_candidate(self.output)

    def test_duplicate_manifest_fields_rejected(self):
        self.build()
        target = self.output / candidate.MANIFEST
        target.write_text(target.read_text().replace('"schema": 1', '"schema": 1, "schema": 1'))
        with self.assertRaises(candidate.CandidateError):
            candidate.verify_candidate(self.output)

    def test_existing_output_and_missing_entry_rejected(self):
        self.build()
        previous = (self.output / candidate.PROGRAM).read_bytes()
        with self.assertRaises(FileExistsError):
            self.build()
        self.assertEqual(previous, (self.output / candidate.PROGRAM).read_bytes())
        (self.package / "__main__.py").unlink()
        with self.assertRaises(candidate.CandidateError):
            candidate.build_candidate(self.source, self.root / "missing", self.revision)
        self.assertFalse((self.root / "missing").exists())

    def test_input_symlink_rejected(self):
        if sys.platform == "win32":
            self.skipTest("symlink creation needs a separate Windows privilege")
        outside = self.root / "outside.py"
        outside.write_text("synthetic unrelated file")
        (self.package / "linked.py").symlink_to(outside)
        with self.assertRaises(candidate.CandidateError):
            self.build()

    def test_source_change_changes_digest(self):
        first = self.build()
        (self.package / "new_module.py").write_text("VALUE = 2\n")
        second = candidate.build_candidate(self.source, self.root / "changed", self.revision)
        self.assertNotEqual(first["program"]["sha256"], second["program"]["sha256"])


if __name__ == "__main__":
    unittest.main()
