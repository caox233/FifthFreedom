"""Install the pinned protobuf compiler with TLS and upstream digest verification."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
import tempfile
import urllib.request
import zipfile

VERSION = "35.1"
# GitHub's publisher-provided asset digests, checked 2026-10-22 at:
# https://github.com/protocolbuffers/protobuf/releases/expanded_assets/v35.1
ARCHIVES = {
    "linux": ("protoc-35.1-linux-x86_64.zip", "6930ebf62bd4ea607b98fff052596c6ee564b9835b4ce172c75a3f53ae9d91b7"),
    "windows": ("protoc-35.1-win64.zip", "5d3ff218d7d91eea95f7569bcb5a98f3030f8996d44151279d9772edcff76082"),
}


def extract_verified(archive: Path, destination: Path, expected_sha256: str) -> None:
    actual = hashlib.sha256(archive.read_bytes()).hexdigest()
    if actual != expected_sha256:
        raise ValueError(f"protoc archive SHA256 mismatch: expected {expected_sha256}, got {actual}")
    with zipfile.ZipFile(archive) as bundle:
        for item in bundle.infolist():
            path = PurePosixPath(item.filename)
            mode = item.external_attr >> 16
            if path.is_absolute() or ".." in path.parts or "\\" in item.filename or ":" in item.filename or stat.S_ISLNK(mode):
                raise ValueError("Unsafe protoc archive member")
        # All members are validated before any extraction; archive hash was checked above.
        bundle.extractall(destination)


def install(destination: Path, target: str) -> Path:
    name, digest = ARCHIVES[target]
    destination = destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        raise FileExistsError(f"Refusing to replace an existing tool directory: {destination}")
    with tempfile.TemporaryDirectory(prefix="protoc-verified-", dir=destination.parent) as temporary:
        staging = Path(temporary)
        archive = staging / name
        url = f"https://github.com/protocolbuffers/protobuf/releases/download/v{VERSION}/{name}"
        # Default TLS verification and inherited HTTPS proxy remain enabled.
        with urllib.request.urlopen(url, timeout=120) as response, archive.open("xb") as output:
            shutil.copyfileobj(response, output)
        extracted = staging / "tool"
        extract_verified(archive, extracted, digest)
        executable = extracted / "bin" / ("protoc.exe" if target == "windows" else "protoc")
        if target == "linux":
            executable.chmod(0o755)
        version = subprocess.check_output([str(executable), "--version"], text=True).strip()
        if version != f"libprotoc {VERSION}":
            raise ValueError(f"Unexpected protoc version: {version}")
        extracted.rename(destination)
    print(f"Verified {name} SHA256={digest}; {version}")
    return destination / "bin" / executable.name


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--platform", choices=ARCHIVES, default="windows" if os.name == "nt" else "linux")
    parser.add_argument("--github-env", action="store_true", help="Register PROTOC and its directory for later Actions steps")
    args = parser.parse_args()
    executable = install(args.destination, args.platform)
    if args.github_env:
        if os.environ.get("GITHUB_ACTIONS") != "true":
            raise RuntimeError("--github-env requires a GitHub Actions runner")
        with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
            output.write(f"PROTOC={executable}\n")
        with open(os.environ["GITHUB_PATH"], "a", encoding="utf-8") as output:
            output.write(f"{executable.parent}\n")


if __name__ == "__main__":
    main()
