#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tempfile
import zipapp
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the standalone SCBL manager zipapp")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source_root = Path(__file__).resolve().parent
    package = source_root / "scblctl"
    if not package.is_dir():
        raise SystemExit(f"missing package: {package}")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    versions_path = source_root.parents[1] / "COMPONENT_VERSIONS.json"
    versions = json.loads(versions_path.read_text(encoding="utf-8"))
    components = versions.get("serverComponents")
    manager_version = components.get("manager") if isinstance(components, dict) else None
    if not isinstance(manager_version, str):
        raise SystemExit("COMPONENT_VERSIONS.json lacks serverComponents.manager")
    with tempfile.TemporaryDirectory(prefix="scblctl-build-") as temporary:
        staging = Path(temporary)
        shutil.copytree(package, staging / "scblctl")
        (staging / "scblctl" / "__init__.py").write_text(
            '"""SCBL 3.0 server management core."""\n\n'
            "from .config import CONFIG_SCHEMA_VERSION\n\n"
            '__all__ = ["CONFIG_SCHEMA_VERSION"]\n'
            f'__version__ = {manager_version!r}\n',
            encoding="utf-8",
        )
        (staging / "__main__.py").write_text(
            "from scblctl.cli import main\nraise SystemExit(main())\n",
            encoding="utf-8",
        )
        zipapp.create_archive(
            staging,
            target=args.output,
            interpreter="/usr/bin/env python3",
            compressed=True,
        )
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    checksum = args.output.with_name(args.output.name + ".sha256")
    checksum.write_text(f"{digest}  {args.output.name}\n", encoding="utf-8")
    print(args.output)
    print(checksum)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
