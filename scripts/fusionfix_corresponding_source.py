"""Pinned source/notice snapshots for the existing loader; never execute them."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat

from fusionfix_provenance import identity, pinned, read_snapshot, require


REFERENCE = Path("docs/development-reference/fusionfix-loader-memorymodule")
PACKAGE_PREFIX = "corresponding-source/MemoryModule/"
COMMIT = "5f83e41c3a3e7c6e8284a5c1afa5a38790809461"
TREE = "4cf1cf78190c2bd40fcd88584d3f0be36a1af947"
REFERENCE_PINS = {
    "README.md": {"bytes": 3547, "sha256": "f680c1edac04712465891b0f3e3c1978eabb62bde543bbe3971968e3acb6b77b"},
    "SOURCE-MANIFEST.json": {"bytes": 11141, "sha256": "b94b1f2e9ff1cbbd7c982f31fee662148f4ec0b21358460bcebbbaf10ffcd7b8"},
}
MIT_PATH = Path("docs/third-party/WidescreenFixesPack-upstream-MIT.txt")
MIT_PIN = {"bytes": 1067, "sha256": "389eef0fb81f8b3bc7aca2025da43cf007acfb390a70f49439d269e51de0b8dc"}
MIT_BLOB = "574d883cdb6ddc02823f907dcfa85e3990438b71"
BAT_PATH = "scripts/run-appveyor.bat"
BAT_CRLF_PIN = {"bytes": 1428, "sha256": "2886757f5e8a66b03389913c47bf482dd4e8a1a535c307889af83566680fa445"}
NOTICE_SCOPE = b"""FusionFix upstream source notice and loader source availability

WidescreenFixesPack-upstream-MIT.txt is the exact upstream project notice at
708f7528fb8b54c79d4ac3c6071ea7ccc01ae162, also identical at the historical ASI
source candidate 2547fdeed707359e67bd4ac0d608de936d78186f. It is supplied as an
upstream source attribution reference. The retained ASI's exact source/build
identity remains unresolved; this notice does not grant permission for every
member of the frozen archive, embedded binary, game data or SDK input.

corresponding-source/MemoryModule/ contains the complete pinned source referenced
by Ultimate ASI Loader v9.7.0, its MPL-2.0 license, access notice and file manifest.
See its README.md for canonical Git modes and the one pinned CRLF checkout form.
No upstream source script or DLL was executed by this copy operation. Source
availability is distinct from independent binary reproduction and complete
redistribution permission. All holds in PROVENANCE.json remain unchanged.
"""


def _blob(content):
    return hashlib.sha1(b"blob " + str(len(content)).encode("ascii") + b"\0" + content).hexdigest()


def _regular_directories(root, relative):
    current = Path(root)
    for part in (None, *relative.parts):
        if part is not None:
            current /= part
        require(stat.S_ISDIR(current.lstat().st_mode), "SOURCE_REFERENCE_DIRECTORY_INVALID")
    return current


def _inventory(root, files):
    directories = {"."}
    for name in files:
        directories.update(str(parent) for parent in PurePosixPath(name).parents)
    found, pending = set(), [root]
    while pending:
        folder = pending.pop()
        with os.scandir(folder) as entries:
            for entry in entries:
                path = Path(entry.path)
                name = path.relative_to(root).as_posix()
                mode = entry.stat(follow_symlinks=False).st_mode
                if stat.S_ISDIR(mode):
                    require(name in directories, "SOURCE_REFERENCE_EXTRA_DIRECTORY")
                    pending.append(path)
                else:
                    require(stat.S_ISREG(mode), "SOURCE_REFERENCE_FILE_INVALID")
                    require(name in files and name not in found, "SOURCE_REFERENCE_EXTRA_FILE")
                    found.add(name)
    require(found == set(files), "SOURCE_REFERENCE_FILE_MISSING")


def _tree(rows):
    def directory(prefix=""):
        entries = {}
        for row in rows:
            if not row["path"].startswith(prefix):
                continue
            suffix = row["path"][len(prefix):]
            name, slash, _ = suffix.partition("/")
            entries[name] = ("40000", None) if slash else (row["mode"], row["git_blob"])
        content = bytearray()
        for name in sorted(entries, key=lambda key: key + ("/" if entries[key][0] == "40000" else "")):
            mode, blob = entries[name]
            if blob is None:
                blob = directory(prefix + name + "/")
            content.extend(mode.encode("ascii") + b" " + name.encode("utf-8") + b"\0" + bytes.fromhex(blob))
        return hashlib.sha1(b"tree " + str(len(content)).encode("ascii") + b"\0" + content).hexdigest()
    return directory()


def source_payloads(root):
    """Validate the complete inventory before returning canonical public bytes.

    The reference JSON is independently pinned, so it cannot authorize a new
    path/hash/mode by being edited together with an input. Windows cannot expose
    POSIX executable modes; the canonical tree and distribution manifest keep
    those modes there. POSIX checkouts must preserve Git's owner-executable bit;
    their read/write and group/other permissions depend on the checkout umask.
    Output copies still receive the canonical 0644/0755 modes.
    """
    reference = _regular_directories(root, REFERENCE)
    documents = {name: pinned(read_snapshot(reference / name, 65536), expected)
                 for name, expected in REFERENCE_PINS.items()}
    manifest = json.loads(documents["SOURCE-MANIFEST.json"])
    require(manifest["schema"] == "ff-memorymodule-source-reference-v1"
            and manifest["commit"] == COMMIT and manifest["git_tree"] == TREE
            and len(manifest["files"]) == 38 and manifest["source_bytes"] == 126092,
            "SOURCE_REFERENCE_MANIFEST_INVALID")
    rows = manifest["files"]
    expected = set(documents) | {"upstream/" + row["path"] for row in rows}
    _inventory(reference, expected)
    payloads = {PACKAGE_PREFIX + name: (content, 0o644) for name, content in documents.items()}
    observed = []
    for row in rows:
        name = row["path"]
        path = reference / "upstream" / name
        require(row["mode"] in ("100644", "100755"), "SOURCE_REFERENCE_MODE_INVALID")
        if os.name != "nt":
            mode = path.lstat().st_mode
            # Git records regular-file executability from S_IXUSR, not the
            # full POSIX permission word. For example, umask 0077 checks out
            # 100644/100755 as 0600/0700 without changing either Git mode.
            # Privilege/sticky bits are not Git semantics and are not accepted.
            git_mode = "100755" if mode & stat.S_IXUSR else "100644"
            require(stat.S_ISREG(mode) and not mode & (stat.S_ISUID | stat.S_ISGID | stat.S_ISVTX)
                    and git_mode == row["mode"],
                    "SOURCE_REFERENCE_MODE_INVALID")
        content = read_snapshot(path, 65536)
        if name == BAT_PATH and identity(content) == BAT_CRLF_PIN:
            # Only the entire independently pinned CRLF representation is
            # accepted. No arbitrary/mixed newline normalization is permitted.
            content = content.replace(b"\r\n", b"\n")
        pinned(content, row)
        blob = _blob(content)
        require(blob == row["git_blob"], "SOURCE_REFERENCE_GIT_BLOB_DIFFERS")
        observed.append(dict(path=name, mode=row["mode"], git_blob=blob))
        payloads[PACKAGE_PREFIX + "upstream/" + name] = (content, int(row["mode"], 8) & 0o777)
    require(_tree(observed) == TREE, "SOURCE_REFERENCE_GIT_TREE_DIFFERS")
    # The exact project notice remains separate from an assertion that the
    # frozen ASI came from this source or that its embedded inputs share MIT.
    _regular_directories(root, MIT_PATH.parent)
    mit = pinned(read_snapshot(Path(root) / MIT_PATH, 65536), MIT_PIN)
    require(_blob(mit) == MIT_BLOB, "SOURCE_REFERENCE_GIT_BLOB_DIFFERS")
    payloads[MIT_PATH.name] = (mit, 0o644)
    payloads["UPSTREAM-NOTICE-SCOPE.txt"] = (NOTICE_SCOPE, 0o644)
    return payloads
