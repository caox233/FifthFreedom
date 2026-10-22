from __future__ import annotations

import hashlib
import json
import os
import platform
import re
import shutil
import tempfile
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Callable

from .file_io import atomic_write_text, sha256_file
from .system import CommandRunner


GITHUB_API = "https://api.github.com/repos/EasyTier/EasyTier/releases/latest"
MAX_ARCHIVE_SIZE = 256 * 1024 * 1024
METADATA_NAME = "scbl-managed.json"


class EasyTierInstallError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class EasyTierLayout:
    root: Path = Path("/opt/scbl/external/easytier")
    cache: Path = Path("/var/cache/scbl/external/easytier")

    @property
    def releases(self) -> Path:
        return self.root / "releases"

    @property
    def current(self) -> Path:
        return self.root / "current"


@dataclass(frozen=True, slots=True)
class EasyTierInstallation:
    version: str
    path: Path
    core_sha256: str
    cli_sha256: str
    reused: bool
    previous: Path | None = None


class EasyTierManager:
    def __init__(
        self,
        *,
        layout: EasyTierLayout | None = None,
        runner: CommandRunner | None = None,
        opener: Callable[..., Any] = urllib.request.urlopen,
        machine: Callable[[], str] = platform.machine,
    ) -> None:
        self.layout = layout or EasyTierLayout()
        self.runner = runner or CommandRunner()
        self.opener = opener
        self.machine = machine

    def ensure_installed(self) -> EasyTierInstallation:
        """Reuse a verified installation; only contact GitHub when none exists."""

        current = self.verified_current()
        if current is not None:
            return EasyTierInstallation(
                current.version,
                current.path,
                current.core_sha256,
                current.cli_sha256,
                True,
            )
        return self.install_latest()

    def install_latest(self) -> EasyTierInstallation:
        release = self._latest_release()
        version = release["version"]
        destination = self.layout.releases / version
        previous = self._current_target()
        existing = self._verify_release(destination)
        reused = existing is not None
        if existing is None:
            archive = self._cached_archive(
                release["name"], release["url"], release["size"], release["sha256"]
            )
            self._install_archive(
                archive,
                destination,
                version=version,
                archive_sha256=release["sha256"],
            )
            existing = self._verify_release(destination)
            if existing is None:
                raise EasyTierInstallError("EasyTier 安装后完整性复核失败")
        self._activate(destination)
        self._prune_releases({destination, previous} if previous else {destination})
        return EasyTierInstallation(
            existing.version,
            existing.path,
            existing.core_sha256,
            existing.cli_sha256,
            reused,
            previous,
        )

    def verified_current(self) -> EasyTierInstallation | None:
        target = self._current_target()
        if target is None:
            return None
        return self._verify_release(target)

    def activate(self, target: Path) -> None:
        target = target.resolve(strict=True)
        if self._verify_release(target) is None:
            raise EasyTierInstallError("拒绝激活未通过校验的 EasyTier 目录")
        self._activate(target)

    def _current_target(self) -> Path | None:
        current = self.layout.current
        if not current.is_symlink():
            if current.exists():
                raise EasyTierInstallError(f"EasyTier current 必须是符号链接：{current}")
            return None
        try:
            target = current.resolve(strict=True)
        except FileNotFoundError:
            return None
        if self.layout.releases.resolve() not in target.parents:
            raise EasyTierInstallError("EasyTier current 指向受管目录之外")
        return target

    def _latest_release(self) -> dict[str, Any]:
        request = urllib.request.Request(
            GITHUB_API,
            headers={
                "Accept": "application/vnd.github+json",
                "User-Agent": "SCBL-Server-Manager/3.0",
            },
        )
        try:
            with self.opener(request, timeout=30) as response:
                raw = json.loads(response.read().decode("utf-8"))
        except Exception as exc:
            raise EasyTierInstallError(f"无法读取 EasyTier 官方发布信息：{exc}") from exc
        tag = raw.get("tag_name") if isinstance(raw, dict) else None
        if not isinstance(tag, str) or not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
            raise EasyTierInstallError("EasyTier 官方最新版本号无效")
        arch = _release_arch(self.machine())
        name = f"easytier-linux-{arch}-{tag}.zip"
        assets = {
            item.get("name"): item
            for item in raw.get("assets", [])
            if isinstance(item, dict)
        }
        asset = assets.get(name)
        if not isinstance(asset, dict):
            raise EasyTierInstallError(f"EasyTier 官方发布缺少 {name}")
        digest = asset.get("digest")
        size = asset.get("size")
        url = asset.get("browser_download_url")
        if (
            not isinstance(digest, str)
            or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest)
            or type(size) is not int
            or not 0 < size <= MAX_ARCHIVE_SIZE
            or not isinstance(url, str)
            or not url.startswith("https://github.com/EasyTier/EasyTier/releases/download/")
        ):
            raise EasyTierInstallError("EasyTier 官方制品缺少可信 SHA256、大小或下载地址")
        return {
            "version": tag.removeprefix("v"),
            "name": name,
            "url": url,
            "size": size,
            "sha256": digest.removeprefix("sha256:").lower(),
        }

    def _cached_archive(self, name: str, url: str, size: int, digest: str) -> Path:
        self.layout.cache.mkdir(parents=True, exist_ok=True)
        os.chmod(self.layout.cache, 0o750)
        destination = self.layout.cache / name
        if (
            destination.is_file()
            and not destination.is_symlink()
            and destination.stat().st_size == size
            and sha256_file(destination) == digest
        ):
            print(f"EasyTier 已复用本机缓存：{name}", flush=True)
            return destination
        destination.unlink(missing_ok=True)
        request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/3.0"})
        fd, temporary_name = tempfile.mkstemp(prefix=f".{name}.", dir=self.layout.cache)
        temporary = Path(temporary_name)
        copied = 0
        next_progress = 10
        try:
            print(
                f"正在下载 EasyTier：{name}（{size / 1024 / 1024:.1f} MiB）",
                flush=True,
            )
            with os.fdopen(fd, "wb") as output, self.opener(request, timeout=120) as response:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > MAX_ARCHIVE_SIZE:
                        raise EasyTierInstallError("EasyTier 下载超过大小限制")
                    output.write(chunk)
                    progress = copied * 100 // size
                    if progress >= next_progress:
                        print(f"EasyTier 下载进度：{min(progress, 100)}%", flush=True)
                        next_progress = (progress // 10 + 1) * 10
                output.flush()
                os.fsync(output.fileno())
            if copied != size or sha256_file(temporary) != digest:
                raise EasyTierInstallError("EasyTier 下载制品大小或 SHA256 不匹配")
            os.chmod(temporary, 0o640)
            os.replace(temporary, destination)
        except Exception:
            temporary.unlink(missing_ok=True)
            raise
        return destination

    def _install_archive(
        self, archive: Path, destination: Path, *, version: str, archive_sha256: str
    ) -> None:
        self.layout.releases.mkdir(parents=True, exist_ok=True)
        staging = Path(tempfile.mkdtemp(prefix=f".{version}.", dir=self.layout.releases))
        try:
            with zipfile.ZipFile(archive, "r") as bundle:
                files = [item for item in bundle.infolist() if not item.is_dir()]
                names = [item.filename.replace("\\", "/") for item in files]
                if len(names) != len(set(names)) or any(not _safe_zip_name(name) for name in names):
                    raise EasyTierInstallError("EasyTier ZIP 包含不安全或重复路径")
                if sum(item.file_size for item in files) > 512 * 1024 * 1024:
                    raise EasyTierInstallError("EasyTier ZIP 解压后超过大小限制")
                binaries: dict[str, bytes] = {}
                for info, name in zip(files, names, strict=True):
                    mode = (info.external_attr >> 16) & 0o170000
                    if mode == 0o120000:
                        raise EasyTierInstallError("EasyTier ZIP 不能包含符号链接")
                    base = PurePosixPath(name).name
                    if base in {"easytier-core", "easytier-cli"}:
                        if base in binaries:
                            raise EasyTierInstallError(f"EasyTier ZIP 重复包含 {base}")
                        binaries[base] = bundle.read(info)
            if set(binaries) != {"easytier-core", "easytier-cli"} or any(
                not payload for payload in binaries.values()
            ):
                raise EasyTierInstallError("EasyTier ZIP 缺少 core 或 cli")
            for name, payload in binaries.items():
                path = staging / name
                path.write_bytes(payload)
                os.chmod(path, 0o755)
            self._probe(staging)
            metadata = {
                "schemaVersion": 1,
                "version": version,
                "archiveSha256": archive_sha256,
                "files": {
                    name: sha256_file(staging / name)
                    for name in ("easytier-core", "easytier-cli")
                },
            }
            atomic_write_text(
                staging / METADATA_NAME,
                json.dumps(metadata, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
                0o644,
            )
            if destination.exists():
                existing = self._verify_release(destination)
                if existing is None:
                    raise EasyTierInstallError(f"EasyTier 版本目录已存在但校验失败：{destination}")
                return
            os.replace(staging, destination)
        finally:
            shutil.rmtree(staging, ignore_errors=True)

    def _probe(self, root: Path) -> None:
        core = self.runner.run((str(root / "easytier-core"), "--help"), timeout=20)
        required = {"--config-file", "--rpc-portal", "--console-log-level", "--file-log-level"}
        if not core.ok or not required.issubset(set(re.findall(r"--[a-z0-9-]+", core.stdout + core.stderr))):
            raise EasyTierInstallError("EasyTier core 参数能力不符合服务契约")
        cli = self.runner.run((str(root / "easytier-cli"), "--help"), timeout=20)
        if not cli.ok:
            raise EasyTierInstallError("EasyTier cli 无法运行")

    def _verify_release(self, root: Path) -> EasyTierInstallation | None:
        if not root.is_dir() or root.is_symlink():
            return None
        try:
            raw = json.loads((root / METADATA_NAME).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return None
        if not isinstance(raw, dict) or raw.get("schemaVersion") != 1:
            return None
        version = raw.get("version")
        files = raw.get("files")
        if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version) or not isinstance(files, dict):
            return None
        digests: dict[str, str] = {}
        for name in ("easytier-core", "easytier-cli"):
            expected = files.get(name)
            path = root / name
            if (
                not isinstance(expected, str)
                or not re.fullmatch(r"[0-9a-f]{64}", expected)
                or not path.is_file()
                or path.is_symlink()
                or sha256_file(path) != expected
            ):
                return None
            digests[name] = expected
        return EasyTierInstallation(version, root, digests["easytier-core"], digests["easytier-cli"], True)

    def _activate(self, target: Path) -> None:
        self.layout.root.mkdir(parents=True, exist_ok=True)
        temporary = self.layout.root / f".current.{os.getpid()}"
        temporary.unlink(missing_ok=True)
        temporary.symlink_to(target, target_is_directory=True)
        os.replace(temporary, self.layout.current)

    def _prune_releases(self, retained: set[Path]) -> None:
        from .retention import RETAINED_GENERATIONS

        retained_resolved = {path.resolve() for path in retained if path and path.exists()}
        candidates = sorted(
            (path for path in self.layout.releases.iterdir() if path.is_dir() and not path.is_symlink()),
            key=lambda path: tuple(int(part) for part in path.name.split("."))
            if re.fullmatch(r"\d+\.\d+\.\d+", path.name)
            else (-1,),
            reverse=True,
        )
        keep = set(retained_resolved)
        for path in candidates:
            if len(keep) >= RETAINED_GENERATIONS:
                break
            keep.add(path.resolve())
        for path in candidates:
            if path.resolve() not in keep:
                shutil.rmtree(path)


def _release_arch(machine: str) -> str:
    normalized = machine.strip().lower()
    if normalized in {"x86_64", "amd64"}:
        return "x86_64"
    if normalized in {"aarch64", "arm64"}:
        return "aarch64"
    raise EasyTierInstallError(f"EasyTier 不支持当前 CPU 架构：{machine}")


def _safe_zip_name(name: str) -> bool:
    path = PurePosixPath(name)
    return bool(name) and not path.is_absolute() and ".." not in path.parts and "" not in path.parts
