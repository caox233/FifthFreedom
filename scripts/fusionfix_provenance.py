"""Read-only frozen FusionFix/ASI-loader identity and notice verification.

No network, extraction, loading or execution. A successful identity check does
not clear the explicitly retained source, license or publication holds.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]


class EvidenceError(ValueError):
    pass


def require(condition, code):
    if not condition:
        raise EvidenceError(code)


def identity(data):
    return dict(bytes=len(data), sha256=hashlib.sha256(data).hexdigest())


def read_snapshot(path, limit):
    original = Path(path).lstat()
    require(stat.S_ISREG(original.st_mode), "INPUT_FILE_INVALID")
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NONBLOCK", 0)
                         | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0))
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and 0 < before.st_size <= limit, "INPUT_FILE_INVALID")
        require((original.st_dev, original.st_ino) == (before.st_dev, before.st_ino), "INPUT_CHANGED")
        with os.fdopen(os.dup(descriptor), "rb") as stream:
            data = stream.read(limit + 1)
        after = os.fstat(descriptor)
        current = Path(path).lstat()
        fields = ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns")
        require(all(getattr(before, field) == getattr(after, field) for field in fields)
                and stat.S_ISREG(current.st_mode)
                and (current.st_dev, current.st_ino) == (before.st_dev, before.st_ino)
                and len(data) == before.st_size, "INPUT_CHANGED")
        return data
    finally:
        os.close(descriptor)


def pinned(data, expected):
    require(identity(data) == {key: expected[key] for key in ("bytes", "sha256")}, "INPUT_HASH_MISMATCH")
    return data


def archive_member(content, name, expected):
    # The caller pins this same immutable byte snapshot before ZIP processing.
    with zipfile.ZipFile(io.BytesIO(content)) as archive:
        names = archive.namelist()
        require(len(names) <= 32 and len(names) == len(set(names)) and name in names, "ARCHIVE_LAYOUT_INVALID")
        info = archive.getinfo(name)
        require(info.file_size == expected["bytes"] and info.file_size <= 4 * 1024 * 1024
                and not info.flag_bits & 1, "ARCHIVE_MEMBER_INVALID")
        return pinned(archive.read(info), expected)


def pe_resources(data):
    """Read numeric PE32 RT_VERSION/RCDATA entries, never scan embedded strings."""
    def unpack(fmt, offset):
        size = struct.calcsize(fmt)
        require(0 <= offset <= len(data) - size, "PE_BOUND_INVALID")
        return struct.unpack_from(fmt, data, offset)
    require(data[:2] == b"MZ", "PE_HEADER_INVALID")
    pe, = unpack("<I", 60)
    require(data[pe:pe + 4] == b"PE\0\0", "PE_HEADER_INVALID")
    machine, count, stamp, _, _, optional, _ = unpack("<HHIIIHH", pe + 4)
    require(machine == 0x14c and 1 <= count <= 32 and optional >= 120, "PE_HEADER_INVALID")
    require(unpack("<H", pe + 24)[0] == 0x10b, "PE_HEADER_INVALID")
    rva, resource_size = unpack("<II", pe + 24 + 112)
    sections = []
    for index in range(count):
        size, start, raw_size, raw = unpack("<IIII", pe + 24 + optional + index * 40 + 8)
        sections.append((start, raw_size, raw))
    def mapped(address, length):
        for start, raw_size, raw in sections:
            if start <= address and address + length <= start + raw_size:
                offset = raw + address - start
                require(0 <= offset <= len(data) - length, "PE_BOUND_INVALID")
                return offset
        raise EvidenceError("PE_RESOURCE_MAPPING_INVALID")
    base = mapped(rva, resource_size)
    require(0 < resource_size <= len(data), "PE_RESOURCE_BOUND_INVALID")
    found, seen = {}, set()
    def directory(relative, path):
        require(len(path) < 3 and len(seen) < 256 and relative not in seen and 0 <= relative <= resource_size - 16,
                "PE_RESOURCE_TREE_INVALID")
        seen.add(relative)
        named, numeric = unpack("<HH", base + relative + 12)
        require(named == 0 and numeric <= 128 and relative + 16 + numeric * 8 <= resource_size,
                "PE_RESOURCE_TREE_INVALID")
        for index in range(numeric):
            key, target = unpack("<II", base + relative + 16 + index * 8)
            require(key < 65536, "PE_RESOURCE_NAME_INVALID")
            if not path and key not in (10, 16):
                continue
            current = (*path, key)
            if target & 0x80000000:
                directory(target & 0x7fffffff, current)
            else:
                require(len(current) == 3 and len(found) < 256 and target <= resource_size - 16 and current not in found,
                        "PE_RESOURCE_TREE_INVALID")
                address, size, _, _ = unpack("<IIII", base + target)
                require(size <= 4 * 1024 * 1024, "PE_RESOURCE_BOUND_INVALID")
                offset = mapped(address, size)
                found[current] = data[offset:offset + size]
    directory(0, ())
    return stamp, found


def version_fields(content):
    selected = {"CompanyName", "FileDescription", "FileVersion", "InternalName", "LegalCopyright",
                "OriginalFilename", "ProductName", "ProductVersion", "GitHash"}
    fields, visits = {}, 0
    def aligned(value): return (value + 3) & ~3
    def block(start, parent_end, path):
        nonlocal visits
        visits += 1
        require(visits <= 128 and len(path) < 6 and start + 6 <= parent_end, "VERSION_BOUND_INVALID")
        length, value_length, kind = struct.unpack_from("<HHH", content, start)
        end = start + length
        require(length >= 6 and end <= parent_end and kind in (0, 1), "VERSION_BLOCK_INVALID")
        cursor, name = start + 6, bytearray()
        while cursor + 2 <= end and content[cursor:cursor + 2] != b"\0\0":
            name.extend(content[cursor:cursor + 2]); cursor += 2
            require(len(name) <= 512, "VERSION_KEY_INVALID")
        require(cursor + 2 <= end, "VERSION_KEY_INVALID")
        key = bytes(name).decode("utf-16le")
        cursor = aligned(cursor + 2)
        size = value_length * (2 if kind == 1 else 1)
        require(cursor + size <= end, "VERSION_VALUE_INVALID")
        value = content[cursor:cursor + size]
        if not path:
            require(key == "VS_VERSION_INFO" and size == 52 and struct.unpack_from("<I", value)[0] == 0xfeef04bd,
                    "VERSION_ROOT_INVALID")
            numbers = struct.unpack("<13I", value)
            for label, high, low in (("fixed_file_version", numbers[2], numbers[3]),
                                     ("fixed_product_version", numbers[4], numbers[5])):
                fields[label] = ".".join(map(str, (high >> 16, high & 65535, low >> 16, low & 65535)))
        elif len(path) == 3 and path[1] == "StringFileInfo" and key in selected:
            require(kind == 1 and key not in fields, "VERSION_STRING_INVALID")
            fields[key] = value.decode("utf-16le").rstrip("\0")
        cursor = aligned(cursor + size)
        while cursor + 6 <= end:
            child_length = block(cursor, end, (*path, key))
            cursor = aligned(cursor + child_length)
        return length
    block(0, len(content), ())
    return fields


def inspect_member(content):
    stamp, resources = pe_resources(content)
    versions = [dict(resource_path=list(path), **identity(value), fields=version_fields(value))
                for path, value in sorted(resources.items()) if path[0] == 16]
    return dict(coff_timestamp=stamp, version_resources=versions), resources


def verify(root=ROOT, upstream_zip=None):
    record = json.loads(read_snapshot(root / "docs/fusionfix-provenance.json", 131072))
    archive = pinned(read_snapshot(root / record["frozen_archive"]["path"], 2 * 1024 * 1024), record["frozen_archive"])
    asset_pins = json.loads(read_snapshot(root / "scripts/local-assets.json", 131072))
    pinned(archive, asset_pins["files"][record["frozen_archive"]["path"]])
    donor = pinned(read_snapshot(root / record["frozen_archive"]["donor_path"], 2 * 1024 * 1024), record["frozen_archive"])
    require(donor == archive, "DONOR_ARCHIVE_DIFFERS")
    resources, members = {}, {}
    for role, expected in record["members"].items():
        content = archive_member(archive, expected["path"], expected)
        observed, found = inspect_member(content)
        require(observed == expected["pe"], "PE_METADATA_DIFFERS")
        members[role], resources[role] = content, found
    for item in record["loader_embedded_inputs"]:
        path = tuple(item["resource_path"])
        require(path in resources["loader"], "EMBEDDED_RESOURCE_MISSING")
        pinned(resources["loader"][path], item)
    for item in record["notices"]:
        content = pinned(read_snapshot(root / item["notice_path"], 65536), item)
        blob = hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest()
        require(blob == item["git_blob"], "NOTICE_GIT_BLOB_DIFFERS")
    upstream_status = "not_rechecked_in_this_run"
    if upstream_zip is not None:
        asset = record["official_loader_asset"]
        content = pinned(read_snapshot(upstream_zip, 2 * 1024 * 1024), asset)
        other = archive_member(content, asset["member_path"], record["members"]["loader"])
        with zipfile.ZipFile(io.BytesIO(content)) as source:
            require(source.namelist() == [asset["member_path"]], "UPSTREAM_LAYOUT_DIFFERS")
        require(other == members["loader"], "UPSTREAM_MEMBER_DIFFERS")
        upstream_status = "renamed_identical_member_verified"
    return dict(schema="ff-fusionfix-static-check-v1", status="identity_and_notices_verified",
        upstream_asset=upstream_status, notices_checked=len(record["notices"]),
        embedded_inputs_checked=len(record["loader_embedded_inputs"]),
        unresolved_holds=record["unresolved_holds"], permission_closure=False,
        independently_rebuilt=False, binary_executed=False, inputs_modified=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream-loader-zip", type=Path)
    args = parser.parse_args()
    try:
        result = verify(upstream_zip=args.upstream_loader_zip)
    except (EvidenceError, OSError, ValueError, TypeError, KeyError, struct.error, zipfile.BadZipFile):
        print(json.dumps(dict(schema="ff-fusionfix-static-check-v1", status="verification_failed")))
        return 1
    print(json.dumps(result, sort_keys=True, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
