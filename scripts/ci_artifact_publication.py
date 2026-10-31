"""Stage exact public component payloads, including pinned hidden source files.

Only this fresh bounded directory may use upload-artifact's include-hidden-files.
Logs and other runner output are deliberately outside this publication boundary.
No downloaded executable, upstream script, service or game is started here.
"""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tempfile

import fusionfix_provenance as provenance
import public_server_candidate as candidate

ROOT = Path(__file__).resolve().parents[1]
LANES = ("windows-components", "linux-public-server-integrity")
NOTICE_PREFIX = "licenses/FusionFix/"
HIDDEN_SOURCE = {
    ".gitattributes": (140, "ba9707535af7fde7fd5c60fb8947a0b8d487a6e7c2409892504cd0b6f3dad395"),
    ".gitignore": (87, "1aa380980a310bc003db68218bf85d9001100582b2efba687ce413a5346c4616"),
    ".travis.yml": (3079, "2aac9d0f35ac618918b66baa9f023614bfd5655879e23b967e11d9445483a761"),
    "example/DllLoader/.gitignore": (9, "d7d3dd5d67de6776268fdd2e858c777725ddd162b57f6116407ca445f8c2f1cb"),
    "example/SampleDLL/.gitignore": (16, "9f22abfcdb63dd119c4fce27972c1bc93c15ed37792b0cd7997eb16974a76388"),
}
HIDDEN_PREFIX = NOTICE_PREFIX + "corresponding-source/MemoryModule/upstream/"
STATIC_DOCUMENTS = {"LICENSE": "LICENSE", "THIRD_PARTY_NOTICES.txt": "THIRD_PARTY_NOTICES.txt",
                    "ThirdPartyNotices/Tomlyn-2.10.1.txt": "src/launcher/src/ThirdPartyNotices/Tomlyn-2.10.1.txt"}
WINDOWS_README = ("Fifth Freedom CI component compilation evidence.\n"
    "NOT a complete or runtime-validated game package. Do not install these files into a game.\n"
    "The launcher may lack privacy-held default saves. Package admission remains a separate strict gate.\n"
    "Linux public donor is reference evidence, not an integrated/public-deployable server.\n"
    "No game, production service, or capture was started.\n").encode()
ASSEMBLY_README = ("Fifth Freedom component-only public-server integrity candidate.\n"
    "NOT a complete Windows client or installable game package.\n"
    "client/ contains the four unchanged Windows role bytes only as admission evidence.\n"
    "Privacy-held saves remain excluded and the complete-package gate remains separate.\n"
    "The ordinary native Public command remains closed; controlled owner-lease development probes are separate.\n"
    "No deployment or game execution occurred.\n"
    "Artifacts may reset executable permissions; offline verification uses tools/verify_public_package ROOT.\n").encode()
ASSEMBLY_PROGRAM = {"bin/FifthFreedom.Server", "tools/verify_public_package",
                    "package/PUBLIC-SERVER-CATALOG.json", "client/package/RELEASE-CATALOG.json",
                    *("client/" + value[0] for value in candidate.WINDOWS.values())}


class PublicationError(ValueError):
    """Fixed diagnostics only; never echo untrusted file contents."""


def require(condition, code):
    if not condition:
        raise PublicationError(code)


def directory(path):
    for current in (*reversed(path.parents), path):
        info = current.lstat()
        require(stat.S_ISDIR(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400,
                "PUBLICATION_DIRECTORY_INVALID")


def inventory(root, expected):
    """Reject unlisted files/directories and links before staging any input."""
    directory(root)
    parents = {str(parent) for name in expected for parent in PurePosixPath(name).parents}
    actual, pending = set(), [root]
    while pending:
        with os.scandir(pending.pop()) as entries:
            for entry in entries:
                path = Path(entry.path)
                name = path.relative_to(root).as_posix()
                # Windows DirEntry.stat() leaves inode/device/link-count
                # fields incomplete. Query fresh, non-following full-path
                # metadata before checking links or file type on every OS.
                info = path.lstat()
                require(not stat.S_ISLNK(info.st_mode)
                        and not getattr(info, "st_file_attributes", 0) & 0x400,
                        "PUBLICATION_LINK_REJECTED")
                if stat.S_ISDIR(info.st_mode):
                    require(name in parents, "PUBLICATION_EXTRA_DIRECTORY")
                    pending.append(path)
                else:
                    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1,
                            "PUBLICATION_FILE_INVALID")
                    require(name in expected, "PUBLICATION_EXTRA_FILE")
                    actual.add(name)
    require(actual == set(expected), "PUBLICATION_MISSING_FILE")


def notice_rows(root):
    # Existing frozen-source validator pins every original file, reference,
    # notice and canonical Git tree. This private scratch is never uploaded.
    with tempfile.TemporaryDirectory(prefix="ff-reviewed-notices-") as temporary:
        rows = {NOTICE_PREFIX + name: row for name, row in
                provenance.copy_notices(Path(temporary) / "notices", root).items()}
    hidden = {name: row for name, row in rows.items()
              if any(part.startswith(".") for part in PurePosixPath(name).parts)}
    require(hidden == {HIDDEN_PREFIX + name: {"bytes": size, "sha256": digest}
                       for name, (size, digest) in HIDDEN_SOURCE.items()}, "PUBLICATION_HIDDEN_SOURCE_INVALID")
    return rows


def checksums(path, rows):
    lines = candidate.read_small(path).decode("ascii").splitlines()
    require(lines == [f"{row['sha256']}  {name}" for name, row in sorted(rows.items())],
            "PUBLICATION_CHECKSUMS_INVALID")


def public_documents(folder, root, readme_name, readme):
    for name, source in STATIC_DOCUMENTS.items():
        require(candidate.sha_file(folder / name) == candidate.sha_file(root / source),
                "PUBLICATION_DOCUMENT_CHANGED")
    content = candidate.read_small(folder / readme_name, 8192)
    require(content in (readme, readme.replace(b"\n", b"\r\n")), "PUBLICATION_README_CHANGED")


def collect_rows(artifacts, lane, root, commit, run_id, attempt):
    require(lane in LANES and re.fullmatch(r"[a-f0-9]{40}", commit)
            and re.fullmatch(r"[1-9][0-9]{0,19}", run_id)
            and re.fullmatch(r"[1-9][0-9]{0,7}", attempt), "PUBLICATION_RUN_INVALID")
    prefix = "ci/" + lane + "/"
    folder = artifacts / prefix
    receipt = candidate.read_json(folder / "receipt.json")
    require(type(receipt) is dict, "PUBLICATION_LANE_INVALID")
    notices = notice_rows(root)
    if lane == "windows-components":
        _, components, _ = candidate.lane_inputs(artifacts, lane, candidate.WINDOWS, commit, run_id, attempt)
        require(receipt.get("fusionfix_notices") == {"files": notices, "permission_closure": False},
                "PUBLICATION_NOTICE_RECEIPT_INVALID")
        payload = folder / "components"
        names = (set(components) | set(notices) | set(STATIC_DOCUMENTS)
                 | {"README.txt", "SHA256SUMS.txt", "FUSIONFIX-NOTICES-SHA256SUMS.txt"})
        inventory(payload, names)
        checksums(payload / "FUSIONFIX-NOTICES-SHA256SUMS.txt", notices)
        public_documents(payload, root, "README.txt", WINDOWS_README)
        payload_prefix = prefix + "components/"
    else:
        require(receipt.get("schema") == 1 and type(receipt.get("schema")) is int
                and (receipt.get("lane"), receipt.get("commit"), receipt.get("run_id"), receipt.get("run_attempt"))
                    == (lane, commit, run_id, attempt)
                and receipt.get("status") == "native_dual_identity_and_static_preflight_verified"
                and receipt.get("exit_code") == 0 and type(receipt.get("exit_code")) is int
                and receipt.get("github_actions") is True and receipt.get("tracked_source_dirty") is False
                and all(receipt.get(key) is False for key in
                        ("complete_package", "game_executed", "live_acceptance", "deployed", "published",
                         "complete_windows_client", "native_public_profile_enabled")), "PUBLICATION_LANE_INVALID")
        payload = folder / "candidate"
        admitted = candidate.read_json(payload / "CANDIDATE-RECEIPT.json")
        require(type(admitted) is dict and type(admitted.get("files")) is dict,
                "PUBLICATION_CANDIDATE_SET_INVALID")
        expected = ASSEMBLY_PROGRAM | set(STATIC_DOCUMENTS) | set(notices) | {"README.component-only.txt"}
        require(set(admitted.get("files", {})) == expected, "PUBLICATION_CANDIDATE_SET_INVALID")
        names = expected | {"CANDIDATE-RECEIPT.json", "SHA256SUMS.txt"}
        inventory(payload, names)
        # Production admission is read-only: no privileges or process launch.
        import ci_native_lifecycle as native
        rows = native.validate_input(payload, admitted, commit, run_id, attempt)
        require(receipt.get("native_verification") == admitted["native_verification"],
                "PUBLICATION_NATIVE_IDENTITY_INVALID")
        checksums(payload / "SHA256SUMS.txt", {name: row for name, row in rows.items() if name != "SHA256SUMS.txt"})
        public_documents(payload, root, "README.component-only.txt", ASSEMBLY_README)
        payload_prefix = prefix + "candidate/"
    for name, expected in notices.items():
        require(candidate.sha_file(payload / name) == expected, "PUBLICATION_NOTICE_CHANGED")
    rows = {payload_prefix + name: candidate.sha_file(payload / name) for name in sorted(names)}
    rows[prefix + "receipt.json"] = candidate.sha_file(folder / "receipt.json")
    return rows


def stage(artifacts, destination, lane, root, commit, run_id, attempt):
    artifacts, destination, root = (Path(os.path.abspath(path)) for path in (artifacts, destination, root))
    require(not destination.exists() and not destination.is_symlink(), "PUBLICATION_OUTPUT_EXISTS")
    directory(destination.parent)
    require(not destination.is_relative_to(artifacts) and not artifacts.is_relative_to(destination),
            "PUBLICATION_OUTPUT_OVERLAPS_INPUT")
    rows = collect_rows(artifacts, lane, root, commit, run_id, attempt)
    destination.mkdir()
    try:
        for name, row in rows.items():
            candidate.checked_copy(artifacts / name, destination / name, row)
        inventory(destination, rows)
        require(collect_rows(destination, lane, root, commit, run_id, attempt) == rows,
                "PUBLICATION_STAGED_INPUT_CHANGED")
        return rows
    except BaseException:
        # This fresh directory belongs exclusively to this invocation.
        shutil.rmtree(destination)
        raise


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--lane", choices=LANES, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--run-attempt", required=True)
    args = parser.parse_args(argv)
    try:
        rows = stage(args.artifacts, args.destination, args.lane, ROOT, args.commit, args.run_id, args.run_attempt)
        print(json.dumps({"status": "public_payload_staged", "lane": args.lane, "files": len(rows),
                          "pinned_hidden_source_files": len(HIDDEN_SOURCE), "complete_game_package": False}))
        return 0
    except (OSError, ValueError, RuntimeError):
        print("PUBLIC_ARTIFACT_STAGING_FAILED")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
