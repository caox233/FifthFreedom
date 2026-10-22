#!/usr/bin/env python3
"""Build one portable all-client-component patch from a published channel."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "server/manager/scblctl/client_component_catalog.json"
VERSION = re.compile(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,79}")
SHA256 = re.compile(r"[0-9a-f]{64}")
ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def build_patch(update_root: Path, channel: str, output: Path) -> Path:
    if channel not in {"stable", "test"}:
        raise ValueError("channel must be stable or test")
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))["components"]
    manifest_path = (
        update_root / "components/channels" / channel / "client_components_v2.json"
    )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != 2 or manifest.get("channel") != channel:
        raise ValueError("component channel manifest is invalid")
    combined = {**manifest.get("components", {}), **manifest.get("files", {})}
    if set(combined) != set(catalog):
        raise ValueError("component channel manifest is not a complete baseline")

    records: list[dict[str, object]] = []
    sources: dict[str, Path] = {}
    for name in sorted(catalog):
        entry = combined[name]
        spec = catalog[name]
        version = entry.get("version")
        digest = entry.get("sha256")
        size = entry.get("size")
        filename = spec["fileName"]
        if (
            not isinstance(version, str)
            or not VERSION.fullmatch(version)
            or not isinstance(digest, str)
            or not SHA256.fullmatch(digest)
            or type(size) is not int
            or size <= 0
            or entry.get("updateMode") != spec["updateMode"]
        ):
            raise ValueError(f"invalid component entry: {name}")
        source = update_root / "components/artifacts" / name / version / filename
        if (
            not source.is_file()
            or source.stat().st_size != size
            or sha256(source) != digest
        ):
            raise ValueError(f"component artifact is missing or changed: {name}")
        archive_path = f"artifacts/{name}/{filename}"
        record: dict[str, object] = {
            "component": name,
            "version": version,
            "file": filename,
            "path": archive_path,
            "sha256": digest,
            "size": size,
            "updateMode": spec["updateMode"],
        }
        notes = entry.get("releaseNotes")
        if notes is not None:
            record["releaseNotes"] = notes
        records.append(record)
        sources[archive_path] = source

    payload = {
        "schemaVersion": 1,
        "packageType": "scbl-client-components-patch",
        "components": records,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.unlink(missing_ok=True)
    with zipfile.ZipFile(output, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        manifest_info = zipfile.ZipInfo("scbl-client-patch.json", ZIP_TIMESTAMP)
        manifest_info.compress_type = zipfile.ZIP_DEFLATED
        bundle.writestr(
            manifest_info,
            (json.dumps(payload, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8"),
        )
        for name in sorted(sources):
            info = zipfile.ZipInfo(name, ZIP_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            bundle.writestr(info, sources[name].read_bytes())
    return output


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--channel", choices=("stable", "test"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = build_patch(args.root.resolve(), args.channel, args.output.resolve())
    print(output)
    print(sha256(output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
