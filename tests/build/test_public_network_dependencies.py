"""Offline identity checks for public-network references; no runtime is installed."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import public_network_dependencies as audit


def fixture(name="package/core"):
    stream = io.BytesIO()
    content = b"synthetic non-executable input"
    with zipfile.ZipFile(stream, "w") as archive:
        archive.writestr(name, content)
    data = stream.getvalue()
    row = {"id": "synthetic", "filename": "synthetic.zip", "bytes": len(data),
           "sha256": hashlib.sha256(data).hexdigest(), "required_members": [
               {"name": name, "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}]}
    return data, {"schema": 1, "artifacts": [row], "closure_holds": ["synthetic license hold"]}


class PublicNetworkDependenciesTests(unittest.TestCase):
    def test_verified_bytes_do_not_approve_publication_or_runtime(self):
        data, lock = fixture()
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "synthetic.zip").write_bytes(data)
            result, code = audit.verify(lock, Path(folder))
        self.assertEqual(code, 0)
        self.assertTrue(result["all_locked_bytes_verified"])
        for field in ("closure_complete", "publication_approved", "runtime_executed", "installed"):
            self.assertFalse(result[field])
        self.assertEqual(result["closure_holds"], ["synthetic license hold"])

    def test_missing_and_tampered_archive_are_distinct(self):
        data, lock = fixture()
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.assertEqual(audit.verify(lock, root)[1], 2)
            (root / "synthetic.zip").write_bytes(data[:-1] + bytes([data[-1] ^ 1]))
            result, code = audit.verify(lock, root)
        self.assertEqual(code, 1)
        self.assertEqual(result["artifacts"][0]["status"], "invalid")

    def test_member_pin_cannot_be_bypassed_by_valid_archive_pin(self):
        data, lock = fixture()
        lock["artifacts"][0]["required_members"][0]["sha256"] = "0" * 64
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "synthetic.zip").write_bytes(data)
            self.assertEqual(audit.verify(lock, Path(folder))[1], 1)

    def test_member_checks_use_the_hashed_snapshot_if_disk_contents_change(self):
        data, lock = fixture()
        original_matches = audit.matches
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "synthetic.zip"
            path.write_bytes(data)
            def change_after_hash(content, row):
                valid = original_matches(content, row)
                if row.get("filename") == "synthetic.zip":
                    path.write_bytes(b"replaced after the checksum read")
                return valid
            with patch.object(audit, "matches", side_effect=change_after_hash):
                self.assertEqual(audit.verify(lock, Path(folder))[1], 0)
            self.assertEqual(path.read_bytes(), b"replaced after the checksum read")

    def test_directory_is_rejected_before_opening_archive(self):
        _, lock = fixture()
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "synthetic.zip").mkdir()
            self.assertEqual(audit.verify(lock, Path(folder))[1], 1)

    def test_unsafe_archive_member_is_rejected_without_extraction(self):
        data, lock = fixture("../outside")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "synthetic.zip").write_bytes(data)
            self.assertEqual(audit.verify(lock, root)[1], 1)
            self.assertEqual(sorted(p.name for p in root.iterdir()), ["synthetic.zip"])

    def test_locked_download_identity_has_no_latest_url_or_silent_digest_upgrade(self):
        lock = json.loads(audit.LOCK.read_text())
        for row in lock["artifacts"]:
            self.assertIn("/releases/download/v" + row["version"] + "/", row["source_url"])
            self.assertNotIn("latest", row["source_url"])
            self.assertFalse(row["runtime_executed"])
            self.assertFalse(row["redistribution_approved"])
            if row["repository"] == "basil00/WinDivert":
                self.assertIsNone(row["official_api_digest"])
                self.assertIn("no_api_digest", row["verification_basis"])
        self.assertTrue(any("Npcap" in hold for hold in lock["closure_holds"]))
        self.assertTrue(any("Wintun" in hold for hold in lock["closure_holds"]))

    def test_clean_checkout_has_valid_lock_and_notices_but_missing_binary_cache(self):
        with tempfile.TemporaryDirectory() as folder:
            result, code = audit.verify(json.loads(audit.LOCK.read_text()), Path(folder))
        self.assertEqual(code, 2)
        self.assertEqual(len(result["artifacts"]), 4)
        self.assertTrue(all(row["status"] == "missing" for row in result["artifacts"]))
        self.assertTrue(all(row["verified"] for row in result["retained_notices"]))


if __name__ == "__main__":
    unittest.main()
