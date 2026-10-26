"""Actual pinned-source packaging, with no upstream script or binary execution."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import fusionfix_corresponding_source as source
import fusionfix_provenance as provenance


class CorrespondingSourceTests(unittest.TestCase):
    def copied_root(self, directory):
        root = Path(directory)
        record = json.loads((ROOT / "docs/fusionfix-provenance.json").read_bytes())
        for relative in ("docs/fusionfix-provenance.json", source.MIT_PATH.as_posix(),
                         *(row["notice_path"] for row in record["notices"])):
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, path)
        shutil.copytree(ROOT / source.REFERENCE, root / source.REFERENCE)
        return root

    def assert_rejected_without_output(self, root, destination):
        with self.assertRaises((provenance.EvidenceError, OSError)):
            provenance.copy_notices(destination, root)
        self.assertFalse(destination.exists())

    def test_actual_copy_contains_complete_canonical_source_and_unchanged_holds(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.copied_root(Path(temporary) / "source")
            destination = Path(temporary) / "candidate-notices"
            rows = provenance.copy_notices(destination, root)
            self.assertEqual(len(rows), 50)  # old 8 + complete source/reference 40 + MIT/scope 2
            self.assertEqual(set(rows), {p.relative_to(destination).as_posix()
                                        for p in destination.rglob("*") if p.is_file()})
            for name, expected in rows.items():
                self.assertEqual(provenance.identity((destination / name).read_bytes()), expected)
            manifest = json.loads((destination / source.PACKAGE_PREFIX / "SOURCE-MANIFEST.json").read_bytes())
            self.assertEqual((manifest["source_files"], manifest["source_bytes"]), (38, 126092))
            self.assertEqual(source._tree(manifest["files"]), source.TREE)
            for row in manifest["files"]:
                path = destination / source.PACKAGE_PREFIX / "upstream" / row["path"]
                self.assertEqual(provenance.identity(path.read_bytes()),
                                 {key: row[key] for key in ("bytes", "sha256")})
                if os.name != "nt":
                    self.assertEqual(stat.S_IMODE(path.stat().st_mode), int(row["mode"], 8) & 0o777)
            self.assertEqual((destination / "PROVENANCE.json").read_bytes(),
                             (root / "docs/fusionfix-provenance.json").read_bytes())
            self.assertFalse(json.loads((destination / "PROVENANCE.json").read_bytes())["independent_permission_closure"])
            self.assertIn(b"exact source/build\nidentity remains unresolved", (destination / "UPSTREAM-NOTICE-SCOPE.txt").read_bytes())
            self.assertEqual((destination / source.MIT_PATH.name).read_bytes(), (ROOT / source.MIT_PATH).read_bytes())

    def test_only_exact_lf_and_crlf_bat_representations_produce_canonical_bytes(self):
        canonical = (ROOT / source.REFERENCE / "upstream" / source.BAT_PATH).read_bytes().replace(b"\r\n", b"\n")
        self.assertNotIn(b"\r", canonical)
        variants = (canonical, canonical.replace(b"\n", b"\r\n"),
                    canonical.replace(b"\n", b"\r\n", 1), canonical + b"changed", canonical.replace(b"\n", b"\r"))
        for index, content in enumerate(variants):
            with self.subTest(variant=index), tempfile.TemporaryDirectory() as temporary:
                root = self.copied_root(Path(temporary) / "source")
                (root / source.REFERENCE / "upstream" / source.BAT_PATH).write_bytes(content)
                destination = Path(temporary) / "output"
                if index < 2:
                    provenance.copy_notices(destination, root)
                    self.assertEqual((destination / source.PACKAGE_PREFIX / "upstream" / source.BAT_PATH).read_bytes(), canonical)
                else:
                    self.assert_rejected_without_output(root, destination)

    def test_modified_source_notice_readme_or_rewritten_manifest_cannot_authorize_copy(self):
        for kind in ("source", "other-crlf", "mit", "readme", "manifest"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = self.copied_root(Path(temporary) / "source")
                base = root / source.REFERENCE
                if kind in ("source", "other-crlf"):
                    path = base / "upstream/MemoryModule.c"
                    path.write_bytes(path.read_bytes() + b"changed" if kind == "source"
                                     else path.read_bytes().replace(b"\n", b"\r\n"))
                elif kind == "mit":
                    path = root / source.MIT_PATH; path.write_bytes(path.read_bytes() + b"changed")
                elif kind == "readme":
                    path = base / "README.md"; path.write_bytes(path.read_bytes() + b"changed")
                else:
                    path = base / "upstream/MemoryModule.c"; path.write_bytes(path.read_bytes() + b"changed")
                    manifest = json.loads((base / "SOURCE-MANIFEST.json").read_bytes())
                    row = next(r for r in manifest["files"] if r["path"] == "MemoryModule.c")
                    row.update(bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                               git_blob=source._blob(path.read_bytes()))
                    (base / "SOURCE-MANIFEST.json").write_text(json.dumps(manifest))
                self.assert_rejected_without_output(root, Path(temporary) / "output")

    def test_extra_hidden_file_empty_directory_and_missing_file_reject_before_output(self):
        for kind in ("hidden", "directory", "missing"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = self.copied_root(Path(temporary) / "source")
                base = root / source.REFERENCE / "upstream"
                if kind == "hidden": (base / ".private-test-sentinel").write_text("inert test input")
                elif kind == "directory": (base / "unexpected-empty").mkdir()
                else: (base / "MemoryModule.h").unlink()
                self.assert_rejected_without_output(root, Path(temporary) / "output")

    @unittest.skipIf(os.name == "nt", "POSIX modes and unprivileged symlink/FIFO controls")
    def test_wrong_mode_symlink_leaf_symlink_parent_and_fifo_are_not_copied(self):
        for kind in ("mode", "leaf", "parent", "fifo"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = self.copied_root(Path(temporary) / "source")
                base = root / source.REFERENCE / "upstream"
                if kind == "mode": (base / "tests/runwine.sh").chmod(0o644)
                elif kind == "leaf":
                    path = base / "MemoryModule.c"; path.unlink()
                    path.symlink_to(ROOT / source.REFERENCE / "upstream/MemoryModule.c")
                elif kind == "parent":
                    path = base / "scripts"; shutil.rmtree(path)
                    path.symlink_to(ROOT / source.REFERENCE / "upstream/scripts", target_is_directory=True)
                else:
                    path = base / "MemoryModule.c"; path.unlink(); os.mkfifo(path)
                self.assert_rejected_without_output(root, Path(temporary) / "output")


if __name__ == "__main__":
    unittest.main()
