"""Real staging/archive round trips with inert, never-executed component bytes."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_artifact_publication as publication
import ci_candidate
import ci_native_lifecycle as native
import fusionfix_provenance as provenance
import public_server_candidate as candidate

COMMIT, RUN, ATTEMPT = "a" * 40, "12345", "1"


class ArtifactPublicationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-inert-publication-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.artifacts = self.root / "artifacts"

    def common(self, lane):
        return {"schema": 1, "lane": lane, "commit": COMMIT, "run_id": RUN, "run_attempt": ATTEMPT,
                "github_actions": True, "tracked_source_dirty": False, "exit_code": 0,
                "complete_package": False, "game_executed": False, "live_acceptance": False,
                "deployed": False, "published": False}

    def fixture(self, lane):
        folder = self.artifacts / "ci" / lane
        if lane == "windows-components":
            sources = self.root / "inert-components"; sources.mkdir()
            files = {}
            for name in candidate.WINDOWS:
                path = sources / name
                path.write_bytes(b"SYNTHETIC INERT INPUT; NEVER EXECUTE\n" + name.encode())
                files[name] = path
            receipt = {**self.common(lane), "status": "components_built_and_selected_tests_passed"}
            with patch.object(ci_candidate, "ART", self.artifacts):
                ci_candidate.collect(lane, files, receipt)
            payload = folder / "components"
        else:
            payload = folder / "candidate"; payload.mkdir(parents=True)
            for name in publication.ASSEMBLY_PROGRAM:
                path = payload / name; path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"SYNTHETIC INERT METADATA; NEVER EXECUTE\n" + name.encode())
            for name, source in publication.STATIC_DOCUMENTS.items():
                path = payload / name; path.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / source, path)
            (payload / "README.component-only.txt").write_bytes(publication.ASSEMBLY_README)
            provenance.copy_notices(payload / "licenses/FusionFix", ROOT)
            rows = {path.relative_to(payload).as_posix(): candidate.sha_file(path)
                    for path in payload.rglob("*") if path.is_file()}
            identity = {"schema": 1, "result": "verified", "platform": "linux-x86_64",
                        "service": {"executable_sha256": rows["bin/FifthFreedom.Server"]["sha256"],
                                    "manifest_sha256": rows["package/PUBLIC-SERVER-CATALOG.json"]["sha256"]},
                        "admitted_client": {"storage_key": "SYNTHETIC_FIXTURE_ONLY"}}
            admitted = {"schema": 1, "kind": "public-server-component-candidate", "source_commit": COMMIT,
                        "run_id": RUN, "run_attempt": ATTEMPT, "scope": dict(candidate.SCOPE), "files": rows,
                        "status": "native_dual_identity_verified", "native_verification": identity,
                        "expected_native_result": copy.deepcopy(identity)}
            (payload / "CANDIDATE-RECEIPT.json").write_bytes(candidate.json_bytes(admitted))
            sums = {**rows, "CANDIDATE-RECEIPT.json": candidate.sha_file(payload / "CANDIDATE-RECEIPT.json")}
            (payload / "SHA256SUMS.txt").write_text("".join(
                f"{row['sha256']}  {name}\n" for name, row in sorted(sums.items())), encoding="ascii")
            receipt = {**self.common(lane), "status": "native_dual_identity_and_static_preflight_verified",
                       "complete_windows_client": False, "native_public_profile_enabled": False,
                       "native_verification": identity}
        (folder / "receipt.json").write_bytes(candidate.json_bytes(receipt))
        return payload, receipt

    def stage(self, lane, destination=None):
        destination = destination or self.root / "public-payload"
        return publication.stage(self.artifacts, destination, lane, ROOT, COMMIT, RUN, ATTEMPT)

    def cli(self, lane, destination):
        return subprocess.run([sys.executable, "-B", str(ROOT / "scripts/ci_artifact_publication.py"),
            "--artifacts", str(self.artifacts), "--destination", str(destination), "--lane", lane,
            "--commit", COMMIT, "--run-id", RUN, "--run-attempt", ATTEMPT],
            capture_output=True, text=True, timeout=30)

    def roundtrip(self, source, name, include_hidden):
        archive = self.root / (name + ".zip")
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as writer:
            for path in sorted(source.rglob("*")):
                relative = path.relative_to(source)
                if path.is_file() and (include_hidden or not any(part.startswith(".") for part in relative.parts)):
                    writer.writestr(relative.as_posix(), path.read_bytes())
        destination = self.root / name; destination.mkdir()
        with zipfile.ZipFile(archive) as reader:
            # This archive was created above from the owned fixture only.
            reader.extractall(destination)
        return destination

    def test_public_payload_archive_roundtrip_preserves_all_five_hidden_files_and_production_admission(self):
        for lane in publication.LANES:
            with self.subTest(lane=lane):
                payload, receipt = self.fixture(lane)
                private = self.artifacts / "logs/.private-sentinel"
                private.parent.mkdir(exist_ok=True); private.write_bytes(b"INERT PRIVATE TEST SENTINEL")
                destination = self.root / (lane + "-stage")
                rows = self.stage(lane, destination)
                self.assertEqual(len(rows), 61 if lane == "windows-components" else 65)
                self.assertEqual(set(rows), {p.relative_to(destination).as_posix()
                                            for p in destination.rglob("*") if p.is_file()})
                hidden = [name for name in rows if any(part.startswith(".") for part in Path(name).parts)]
                self.assertEqual(len(hidden), 5)
                downloaded = self.roundtrip(destination, lane + "-download", True)
                self.assertEqual(publication.collect_rows(downloaded, lane, ROOT, COMMIT, RUN, ATTEMPT), rows)
                for name, expected in rows.items():
                    self.assertEqual(candidate.sha_file(downloaded / name), expected)
                    self.assertEqual((downloaded / name).read_bytes(), (self.artifacts / name).read_bytes())
                self.assertFalse((downloaded / "logs").exists())
                if lane == "windows-components":
                    _, components, _ = candidate.lane_inputs(downloaded, lane, candidate.WINDOWS, COMMIT, RUN, ATTEMPT)
                    self.assertEqual(components, receipt["components"])
                else:
                    package = downloaded / "ci" / lane / "candidate"
                    admitted = candidate.read_json(package / "CANDIDATE-RECEIPT.json")
                    self.assertEqual(len(native.validate_input(package, admitted, COMMIT, RUN, ATTEMPT)), 64)

    def test_old_hidden_exclusion_loses_exact_five_declared_inputs_and_fails_production_validator(self):
        for lane in publication.LANES:
            with self.subTest(lane=lane):
                _, receipt = self.fixture(lane)
                destination = self.root / (lane + "-stage")
                self.stage(lane, destination)
                downloaded = self.roundtrip(destination, lane + "-old-default", False)
                subfolder = "components" if lane == "windows-components" else "candidate"
                payload = downloaded / "ci" / lane / subfolder
                expected = {publication.HIDDEN_PREFIX + name for name in publication.HIDDEN_SOURCE}
                admitted = (receipt["fusionfix_notices"]["files"] if lane == "windows-components" else
                            candidate.read_json(payload / "CANDIDATE-RECEIPT.json")["files"])
                self.assertEqual({name for name in admitted if not (payload / name).exists()}, expected)
                with self.assertRaisesRegex(publication.PublicationError, "PUBLICATION_MISSING_FILE"):
                    publication.collect_rows(downloaded, lane, ROOT, COMMIT, RUN, ATTEMPT)
                if lane != "windows-components":
                    with self.assertRaisesRegex(native.ProbeError, "^CANDIDATE_INPUT_MISSING$"):
                        native.validate_input(payload, candidate.read_json(payload / "CANDIDATE-RECEIPT.json"),
                                              COMMIT, RUN, ATTEMPT)

    def test_unlisted_hidden_visible_and_empty_directory_inputs_reject_before_staging(self):
        payload, _ = self.fixture("windows-components")
        for name, is_directory in ((".private", False), ("private.txt", False), (".private-directory", True)):
            with self.subTest(name=name):
                path = payload / name
                if is_directory: path.mkdir()
                else: path.write_bytes(b"INERT PRIVATE TEST INPUT")
                with self.assertRaises(publication.PublicationError): self.stage("windows-components")
                self.assertFalse((self.root / "public-payload").exists())
                if is_directory: path.rmdir()
                else: path.unlink()

    def test_changed_pinned_hidden_bytes_cannot_be_authorized_by_rewriting_receipts(self):
        payload, receipt = self.fixture("windows-components")
        name = publication.HIDDEN_PREFIX + ".gitignore"
        (payload / name).write_bytes(b"INERT UNAPPROVED REPLACEMENT")
        receipt["fusionfix_notices"]["files"][name] = candidate.sha_file(payload / name)
        (payload.parent / "receipt.json").write_bytes(candidate.json_bytes(receipt))
        with self.assertRaisesRegex(publication.PublicationError, "PUBLICATION_NOTICE_RECEIPT_INVALID"):
            self.stage("windows-components")
        self.assertFalse((self.root / "public-payload").exists())

    def test_candidate_extra_declared_private_path_and_changed_checksum_list_are_rejected(self):
        payload, _ = self.fixture("linux-public-server-integrity")
        receipt_path = payload / "CANDIDATE-RECEIPT.json"
        original = receipt_path.read_bytes()
        receipt = json.loads(original)
        private = payload / ".private"; private.write_bytes(b"INERT PRIVATE TEST INPUT")
        receipt["files"][".private"] = candidate.sha_file(private)
        receipt_path.write_bytes(candidate.json_bytes(receipt))
        with self.assertRaisesRegex(publication.PublicationError, "PUBLICATION_CANDIDATE_SET_INVALID"):
            self.stage("linux-public-server-integrity")
        receipt_path.write_bytes(original); private.unlink()
        (payload / "SHA256SUMS.txt").write_bytes(b"invalid checksum list\n")
        with self.assertRaisesRegex(publication.PublicationError, "PUBLICATION_CHECKSUMS_INVALID"):
            self.stage("linux-public-server-integrity")
        self.assertFalse((self.root / "public-payload").exists())

    @unittest.skipIf(os.name == "nt", "Unprivileged POSIX symlink/FIFO fixtures")
    def test_symlinks_hardlinks_and_nonregular_inputs_are_rejected(self):
        payload, _ = self.fixture("windows-components")
        source = payload / "README.txt"
        original = source.read_bytes()
        external = self.root / "external"; external.write_bytes(original)
        for kind in ("symlink", "hardlink", "fifo"):
            with self.subTest(kind=kind):
                source.unlink()
                if kind == "symlink": source.symlink_to(external)
                elif kind == "hardlink": os.link(external, source)
                else: os.mkfifo(source)
                with self.assertRaises((publication.PublicationError, candidate.CandidateError)):
                    self.stage("windows-components")
                self.assertFalse((self.root / "public-payload").exists())
                source.unlink(); source.write_bytes(original)
        nested = payload / "licenses/FusionFix/corresponding-source/MemoryModule/upstream/example"
        relocated = self.root / "relocated-example"; nested.rename(relocated); nested.symlink_to(relocated, target_is_directory=True)
        with self.assertRaisesRegex(publication.PublicationError, "PUBLICATION_LINK_REJECTED"):
            self.stage("windows-components")

    def test_wrong_run_existing_output_and_interrupted_copy_preserve_inputs(self):
        payload, _ = self.fixture("windows-components")
        with self.assertRaises(candidate.CandidateError):
            publication.stage(self.artifacts, self.root / "wrong-run", "windows-components", ROOT, COMMIT, "999", ATTEMPT)
        self.assertFalse((self.root / "wrong-run").exists())
        output = self.root / "existing"; output.mkdir(); (output / "original").write_bytes(b"preserve")
        with self.assertRaisesRegex(publication.PublicationError, "PUBLICATION_OUTPUT_EXISTS"):
            self.stage("windows-components", output)
        self.assertEqual((output / "original").read_bytes(), b"preserve")
        with patch.object(candidate, "checked_copy", side_effect=OSError("INERT COPY FAILURE")):
            with self.assertRaises(OSError): self.stage("windows-components")
        self.assertFalse((self.root / "public-payload").exists())
        self.assertTrue((payload / "README.txt").is_file())

    def test_actual_cli_reports_success_and_fixed_malformed_receipt_failure_without_payload(self):
        lane = "linux-public-server-integrity"
        payload, _ = self.fixture(lane)
        success = self.cli(lane, self.root / "successful-cli-stage")
        self.assertEqual((success.returncode, success.stderr), (0, ""))
        report = json.loads(success.stdout)
        self.assertEqual((report["status"], report["files"], report["pinned_hidden_source_files"]),
                         ("public_payload_staged", 65, 5))
        self.assertFalse(report["complete_game_package"])
        for name in (payload.parent / "receipt.json", payload / "CANDIDATE-RECEIPT.json"):
            original = name.read_bytes()
            values = (None, [], {"files": None}, {"files": []})
            for malformed in values:
                with self.subTest(file=name.name, malformed=malformed):
                    name.write_bytes(candidate.json_bytes(malformed))
                    destination = self.root / "rejected-cli-stage"
                    result = self.cli(lane, destination)
                    self.assertEqual(result.returncode, 1)
                    self.assertEqual(result.stdout, "PUBLIC_ARTIFACT_STAGING_FAILED\n")
                    self.assertEqual(result.stderr, "")
                    self.assertFalse(destination.exists())
            name.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
