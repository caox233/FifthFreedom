from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import stat
import urllib.request
import warnings
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from .file_io import atomic_copy as _atomic_copy
from .file_io import atomic_write_bytes as _atomic_write
from .file_io import sha256_file as _sha256
from .paths import DEPLOYMENT_PATHS
from .retention import RETAINED_GENERATIONS

try:
    import grp
    import pwd
except ImportError:  # pragma: no cover - Windows build/test host
    grp = None  # type: ignore[assignment]
    pwd = None  # type: ignore[assignment]


CLIENT_MANIFEST = "client_package_manifest.json"
REQUIRED_FILES = frozenset(
    {
        "SCBL.exe",
        "SCBL.Updater.exe",
        "components/network/easytier-core.exe",
        "components/network/easytier-cli.exe",
        "components/network/Packet.dll",
        "components/network/wintun.dll",
        "components/route-guard/scbl-process-router.exe",
        "components/route-guard/WinDivert.dll",
        "components/route-guard/WinDivert64.payload.sys",
        "components/hooks/uplay_r1_loader.dll",
        "components/hooks/uplay_r1_loader.dll.sha256",
        "components/hooks/mod/scbl_gameplay.dll",
        "components/hooks/mod/scbl_gameplay.dll.sha256",
    }
)


class ClientPublishError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class ClientPackage:
    version: str
    archive: Path
    sha256: str
    size: int


class ClientPublisher:
    def __init__(
        self,
        root: Path | None = None,
        *,
        refresh_descriptor: bool = True,
        channel: str | None = None,
    ) -> None:
        self.root = root or Path(DEPLOYMENT_PATHS.data) / "client-updates"
        self.manifest_path = self.root / "client_update_manifest.json"
        self.refresh_descriptor = refresh_descriptor
        self.channel = channel

    def require_component_baseline(self, package: ClientPackage | None = None) -> None:
        if self.channel is None:
            return
        from .client_components import COMPONENTS, ClientComponentPublisher

        manifest = ClientComponentPublisher(
            self.root,
            refresh_descriptor=False,
        ).load_manifest(self.channel)
        published = set(manifest["components"]) | set(manifest["files"])
        expected = set(COMPONENTS)
        if published != expected:
            missing = sorted(expected - published)
            extra = sorted(published - expected)
            raise ClientPublishError(
                "发布完整客户端前必须先发布同一候选的全部组件补丁；"
                f"缺少={missing or '-'}，多余={extra or '-'}"
            )
        if package is None:
            return
        with zipfile.ZipFile(package.archive, "r") as bundle:
            raw = json.loads(bundle.read(CLIENT_MANIFEST))
        _, declared = _parse_manifest(raw)
        receipts = raw.get("componentReceipts") if isinstance(raw, dict) else None
        versions = raw.get("componentVersions") if isinstance(raw, dict) else None
        if not isinstance(receipts, dict) or set(receipts) != expected:
            raise ClientPublishError("完整包 componentReceipts 与当前六组件不一致")
        if not isinstance(versions, dict) or set(versions) != expected:
            raise ClientPublishError("完整包 componentVersions 与当前六组件不一致")
        current = {**manifest["components"], **manifest["files"]}
        claimed_paths: set[str] = set()
        for component, entry in current.items():
            receipt = receipts.get(component)
            if (
                not isinstance(receipt, dict)
                or receipt.get("version") != entry["version"]
                or receipt.get("sha256") != entry["sha256"]
                or versions.get(component) != entry["version"]
            ):
                raise ClientPublishError(f"完整包与当前组件字节不一致：{component}")
            receipt_files = receipt.get("files")
            if not isinstance(receipt_files, dict) or not receipt_files:
                raise ClientPublishError(f"完整包组件回执缺少文件事实：{component}")
            for path, digest in receipt_files.items():
                if (
                    not isinstance(path, str)
                    or path in claimed_paths
                    or path not in declared
                    or not isinstance(digest, str)
                    or digest.lower() != declared[path][1]
                ):
                    raise ClientPublishError(f"完整包组件文件回执无效：{component}")
                claimed_paths.add(path)
        if versions.get("launcher") != package.version:
            raise ClientPublishError("完整包版本必须与当前 Launcher 版本一致")

    @staticmethod
    def require_root() -> None:
        if os.name != "posix" or not Path("/proc").exists():
            raise ClientPublishError("客户端发布只能在 Linux 服务端执行")
        if os.geteuid() != 0:
            raise ClientPublishError("客户端发布需要 root 权限")

    def verify(self, archive: Path) -> ClientPackage:
        archive = archive.resolve(strict=True)
        if not archive.is_file() or archive.suffix.lower() != ".zip":
            raise ClientPublishError("客户端包必须是 ZIP 文件")
        if archive.stat().st_size > 1024 * 1024 * 1024:
            raise ClientPublishError("客户端 ZIP 不能超过 1 GiB")
        try:
            bundle = zipfile.ZipFile(archive, "r")
        except zipfile.BadZipFile as exc:
            raise ClientPublishError("客户端 ZIP 已损坏") from exc
        with bundle:
            infos = [item for item in bundle.infolist() if not item.is_dir()]
            if len(infos) > 256:
                raise ClientPublishError("客户端 ZIP 文件数量超过限制")
            names: set[str] = set()
            total_size = 0
            for item in infos:
                name = item.filename.replace("\\", "/")
                if not _safe_name(name) or name in names:
                    raise ClientPublishError(f"客户端 ZIP 路径不安全或重复：{name}")
                unix_mode = (item.external_attr >> 16) & 0xFFFF
                if stat.S_ISLNK(unix_mode):
                    raise ClientPublishError(f"客户端 ZIP 不能包含符号链接：{name}")
                names.add(name)
                total_size += item.file_size
                if total_size > 1024 * 1024 * 1024:
                    raise ClientPublishError("客户端 ZIP 解压后超过 1 GiB")
            if CLIENT_MANIFEST not in names:
                raise ClientPublishError(f"客户端 ZIP 缺少 {CLIENT_MANIFEST}")
            try:
                raw: Any = json.loads(bundle.read(CLIENT_MANIFEST))
            except (json.JSONDecodeError, UnicodeDecodeError) as exc:
                raise ClientPublishError("客户端包清单不是有效 UTF-8 JSON") from exc
            version, declared = _parse_manifest(raw)
            expected_names = set(declared) | {CLIENT_MANIFEST}
            if names != expected_names:
                extra = sorted(names - expected_names)
                missing = sorted(expected_names - names)
                raise ClientPublishError(
                    f"客户端 ZIP 与清单不一致；多余={extra or '-'}，缺少={missing or '-'}"
                )
            missing_required = sorted(REQUIRED_FILES - names)
            if missing_required:
                raise ClientPublishError("客户端包缺少必要文件：" + ", ".join(missing_required))
            info_by_name = {item.filename.replace("\\", "/"): item for item in infos}
            for name, (size, digest) in declared.items():
                info = info_by_name[name]
                if info.file_size != size:
                    raise ClientPublishError(f"客户端文件大小不符：{name}")
                actual = hashlib.sha256(bundle.read(info)).hexdigest()
                if actual != digest:
                    raise ClientPublishError(f"客户端文件 SHA256 不符：{name}")
        return ClientPackage(version, archive, _sha256(archive), archive.stat().st_size)

    def publish(
        self,
        archive: Path,
        *,
        release_notes: list[str] | None = None,
        release_notes_en: list[str] | None = None,
    ) -> ClientPackage:
        self.require_root()
        package = self.verify(archive)
        self.require_component_baseline(package)
        notes = release_notes or ["客户端功能与稳定性更新"]
        notes_en = release_notes_en or ["Client features and stability update"]
        # Complete packages are administrator-selected baselines and may
        # intentionally repair, replace, or roll back the current release.
        # The confirmation in the calling workflow is the safety boundary.
        release_dir = self.root / "releases" / package.version
        release_dir_preexisting = release_dir.exists()
        release_dir.mkdir(parents=True, exist_ok=True)
        destination = release_dir / f"SCBL-Client-v{package.version}-win-x86.zip"
        destination_preexisting = destination.exists()
        if destination_preexisting:
            if destination.stat().st_size != package.size or _sha256(destination) != package.sha256:
                raise ClientPublishError(
                    f"不可变完整包 {package.version} 已存在且内容不同，拒绝覆盖"
                )
        else:
            _atomic_copy(package.archive, destination, 0o640)
        immutable_relative = destination.relative_to(self.root).as_posix()
        public_alias = self.root / "SCBL.zip"
        payload = {
            "version": package.version,
            "updateMode": "full-package",
            "fullPackage": "SCBL.zip",
            "immutableFullPackage": immutable_relative,
            "fullPackageSha256": package.sha256,
            "fullPackageSize": package.size,
            "releaseNotes": notes,
            "releaseNotesEn": notes_en,
        }
        from .announcements import AnnouncementManager
        from .server_descriptor import refresh_server_descriptor

        announcements = AnnouncementManager(self.root, refresh_descriptor=False)
        announcement_path = announcements.path_for("update")
        previous_announcement = (
            announcement_path.read_bytes() if announcement_path.is_file() else None
        )
        previous_manifest = (
            self.manifest_path.read_bytes() if self.manifest_path.is_file() else None
        )
        alias_rollback = self.root / f".SCBL.zip.rollback.{os.getpid()}"
        alias_rollback.unlink(missing_ok=True)
        alias_preexisting = public_alias.is_file() and not public_alias.is_symlink()
        if public_alias.exists() and not alias_preexisting:
            raise ClientPublishError("固定完整包路径 SCBL.zip 不是普通文件，拒绝覆盖")
        if alias_preexisting:
            os.link(public_alias, alias_rollback)
        try:
            update_announcement = announcements.update_payload_for_version(package.version)
            if update_announcement.get("enabled") is not True:
                announcements.set(
                    "update",
                    title=f"SCBL {package.version} 更新",
                    body="\n".join(notes),
                    title_en=f"SCBL {package.version} Update",
                    body_en="\n".join(notes_en),
                    version=package.version,
                    enabled=True,
                    sync_client_manifest=False,
                )
                update_announcement = announcements.update_payload_for_version(package.version)
            payload["updateAnnouncement"] = update_announcement
            self._replace_public_alias(destination, public_alias)
            _atomic_write(
                self.manifest_path,
                (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
                0o640,
            )
            self._set_update_owner(
                destination,
                public_alias,
                self.manifest_path,
                release_dir,
            )
            if self.refresh_descriptor:
                refresh_server_descriptor(self.root)
        except Exception:
            _restore_file(self.manifest_path, previous_manifest)
            _restore_file(announcement_path, previous_announcement)
            if alias_rollback.is_file():
                os.replace(alias_rollback, public_alias)
            elif not alias_preexisting:
                public_alias.unlink(missing_ok=True)
            if not destination_preexisting:
                destination.unlink(missing_ok=True)
            if not release_dir_preexisting:
                try:
                    release_dir.rmdir()
                except OSError:
                    pass
            raise
        finally:
            alias_rollback.unlink(missing_ok=True)
        try:
            self._prune_releases(package.version)
        except OSError as exc:
            warnings.warn(
                f"完整包已发布，但旧完整包自动保留清理失败：{exc}",
                RuntimeWarning,
                stacklevel=2,
            )
        return ClientPackage(package.version, destination, package.sha256, package.size)

    def current_version(self) -> str:
        try:
            raw = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return ""
        version = raw.get("version") if isinstance(raw, dict) else None
        return version if isinstance(version, str) and _valid_version(version) else ""

    @staticmethod
    def _set_update_owner(*paths: Path) -> None:
        if pwd is None or grp is None:
            return
        try:
            uid = pwd.getpwnam("scbl-update").pw_uid
            gid = grp.getgrnam("scbl").gr_gid
        except KeyError:
            return
        for path in paths:
            os.chown(path, uid, gid)

    @staticmethod
    def _replace_public_alias(source: Path, destination: Path) -> None:
        temporary = destination.with_name(f".{destination.name}.{os.getpid()}.tmp")
        temporary.unlink(missing_ok=True)
        try:
            os.link(source, temporary)
            os.replace(temporary, destination)
        finally:
            temporary.unlink(missing_ok=True)

    def _prune_releases(self, current_version: str) -> None:
        releases = self.root / "releases"
        if not releases.is_dir():
            return
        candidates = sorted(
            (
                path
                for path in releases.iterdir()
                if path.is_dir()
                and not path.is_symlink()
                and _valid_version(path.name)
            ),
            key=lambda path: tuple(int(part) for part in path.name.split(".")),
            reverse=True,
        )
        keep = {path.resolve() for path in candidates if path.name == current_version}
        for path in candidates:
            if len(keep) >= RETAINED_GENERATIONS:
                break
            keep.add(path.resolve())
        for path in candidates:
            if path.resolve() not in keep:
                shutil.rmtree(path)


def online_client_urls(repository: str, version: str) -> tuple[str, str]:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ClientPublishError("GitHub 仓库格式无效")
    if not _valid_version(version):
        raise ClientPublishError("客户端版本必须是 X.Y.Z")
    filename = f"SCBL-Client-v{version}-win-x86.zip"
    base = f"https://github.com/{repository}/releases/download/client-v{version}"
    return f"{base}/{filename}", f"{base}/{filename}.sha256"


def download_online_client(repository: str, destination: Path) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ClientPublishError("GitHub 仓库格式无效")
    version_url = f"https://raw.githubusercontent.com/{repository}/main/VERSION_CLIENT"
    version = _download_text(version_url, maximum=128).strip()
    if not _valid_version(version):
        raise ClientPublishError("GitHub VERSION_CLIENT 不是有效的 X.Y.Z")
    package_url, checksum_url = online_client_urls(repository, version)
    expected_text = _download_text(checksum_url, maximum=4096)
    match = re.search(r"(?i)\b([0-9a-f]{64})\b", expected_text)
    if not match:
        raise ClientPublishError("GitHub 客户端校验文件无效")
    expected = match.group(1).lower()
    destination.mkdir(parents=True, exist_ok=True)
    target = destination / f"SCBL-Client-v{version}-win-x86.zip"
    _download_file(package_url, target, maximum=1024 * 1024 * 1024)
    actual = _sha256(target)
    if actual != expected:
        target.unlink(missing_ok=True)
        raise ClientPublishError(
            f"GitHub 客户端包 SHA256 不符：expected={expected}, actual={actual}"
        )
    return target


def _download_text(url: str, *, maximum: int) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            content = response.read(maximum + 1)
    except Exception as exc:
        raise ClientPublishError(f"在线下载失败：{url}：{exc}") from exc
    if len(content) > maximum:
        raise ClientPublishError(f"在线文本超过限制：{url}")
    try:
        return content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise ClientPublishError(f"在线文本不是 UTF-8：{url}") from exc


def _download_file(url: str, destination: Path, *, maximum: int) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "SCBL-Server-Manager/2.0"})
    try:
        with urllib.request.urlopen(request, timeout=900) as response:
            length = response.headers.get("Content-Length")
            if length and int(length) > maximum:
                raise ClientPublishError("在线客户端包超过 1 GiB")
            with destination.open("xb") as output:
                copied = 0
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > maximum:
                        raise ClientPublishError("在线客户端包超过 1 GiB")
                    output.write(chunk)
    except Exception as exc:
        destination.unlink(missing_ok=True)
        if isinstance(exc, ClientPublishError):
            raise
        raise ClientPublishError(f"在线下载失败：{url}：{exc}") from exc


def _parse_manifest(raw: Any) -> tuple[str, dict[str, tuple[int, str]]]:
    if not isinstance(raw, dict) or raw.get("schemaVersion") != 1:
        raise ClientPublishError("不支持的客户端包清单版本")
    version = raw.get("clientVersion")
    if not isinstance(version, str) or not _valid_version(version):
        raise ClientPublishError("客户端版本必须是 X.Y.Z")
    files = raw.get("files")
    if not isinstance(files, list) or not files:
        raise ClientPublishError("客户端包清单 files 不能为空")
    declared: dict[str, tuple[int, str]] = {}
    for item in files:
        if not isinstance(item, dict) or set(item) != {"path", "size", "sha256"}:
            raise ClientPublishError("客户端文件清单字段无效")
        name, size, digest = item.get("path"), item.get("size"), item.get("sha256")
        if not isinstance(name, str) or not _safe_name(name) or name in declared:
            raise ClientPublishError(f"客户端文件路径无效或重复：{name!r}")
        if type(size) is not int or size < 0 or size > 512 * 1024 * 1024:
            raise ClientPublishError(f"客户端文件大小无效：{name}")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
            raise ClientPublishError(f"客户端文件 SHA256 无效：{name}")
        declared[name] = (size, digest.lower())
    return version, declared


def _safe_name(name: str) -> bool:
    path = PurePosixPath(name)
    return (
        bool(name)
        and "\x00" not in name
        and ":" not in name
        and not path.is_absolute()
        and ".." not in path.parts
        and all(part not in {"", "."} for part in path.parts)
    )


def _valid_version(value: str) -> bool:
    return re.fullmatch(r"\d+\.\d+\.\d+", value) is not None


def _restore_file(path: Path, snapshot: bytes | None) -> None:
    if snapshot is None:
        path.unlink(missing_ok=True)
    else:
        _atomic_write(path, snapshot, 0o640)
