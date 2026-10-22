from __future__ import annotations

import datetime as dt
import hashlib
import importlib.resources
import json
import os
import re
import shutil
import tempfile
import unicodedata
import urllib.error
import urllib.request
import warnings
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

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


SCHEMA_VERSION = 2
PATCH_MANIFEST = "scbl-client-patch.json"
RELEASE_NOTE_LANGUAGES = frozenset({"zh", "en"})
MAX_RELEASE_NOTES_PER_LANGUAGE = 6
MAX_RELEASE_NOTE_CHARACTERS = 240


@dataclass(frozen=True, slots=True)
class ComponentSpec:
    filename: str
    section: str
    update_mode: str


class ClientComponentError(RuntimeError):
    pass


def _load_component_catalog() -> dict[str, ComponentSpec]:
    resource_name = "client_component_catalog.json"
    try:
        resource = importlib.resources.files(__package__).joinpath(resource_name)
        payload = json.loads(resource.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ClientComponentError(f"客户端组件目录无法读取：{resource_name}") from exc
    raw_components = payload.get("components") if isinstance(payload, dict) else None
    if payload.get("schemaVersion") != 1 or not isinstance(raw_components, dict):
        raise ClientComponentError("客户端组件目录结构无效")

    components: dict[str, ComponentSpec] = {}
    for name, raw in raw_components.items():
        if not isinstance(name, str) or not re.fullmatch(r"[a-z][a-z0-9-]{0,31}", name):
            raise ClientComponentError("客户端组件目录包含无效名称")
        if not isinstance(raw, dict):
            raise ClientComponentError(f"客户端组件目录记录无效：{name}")
        filename = raw.get("fileName")
        section = raw.get("section")
        update_mode = raw.get("updateMode")
        if (
            not isinstance(filename, str)
            or not filename
            or Path(filename).name != filename
            or section not in {"components", "files"}
            or not isinstance(update_mode, str)
            or not re.fullmatch(r"[a-z][a-z-]{0,31}", update_mode)
        ):
            raise ClientComponentError(f"客户端组件目录记录无效：{name}")
        components[name] = ComponentSpec(filename, section, update_mode)
    if not components:
        raise ClientComponentError("客户端组件目录为空")
    return components


COMPONENTS = _load_component_catalog()
FILE_BUNDLES = frozenset(
    name for name, spec in COMPONENTS.items() if spec.section == "files"
)


@dataclass(frozen=True, slots=True)
class DownloadedComponent:
    component: str
    version: str
    source: Path
    sha256: str
    size: int


@dataclass(frozen=True, slots=True)
class ComponentPatchArtifact:
    component: str
    version: str
    path: str
    sha256: str
    size: int
    release_notes: dict[str, list[str]] | None


@dataclass(frozen=True, slots=True)
class ClientComponentPatch:
    archive: Path
    artifacts: tuple[ComponentPatchArtifact, ...]
    sha256: str


class ClientComponentPublisher:
    def __init__(self, root: Path | None = None, *, refresh_descriptor: bool = True) -> None:
        self.root = root or Path(DEPLOYMENT_PATHS.data) / "client-updates"
        self.components_root = self.root / "components"
        self.artifacts_root = self.components_root / "artifacts"
        self.channels_root = self.components_root / "channels"
        self.backups_root = self.components_root / "manifest-backups"
        self.refresh_descriptor = refresh_descriptor

    def initialize(self) -> None:
        for channel in ("stable", "test"):
            path = self.manifest_path(channel)
            if not path.exists():
                self._write_manifest(channel, self._empty_manifest(channel), backup=False)

    def verify_patch(self, archive: Path) -> ClientComponentPatch:
        archive = archive.resolve(strict=True)
        if not archive.is_file() or archive.suffix.lower() != ".scblpatch":
            raise ClientComponentError("客户端组件补丁必须使用 .scblpatch 扩展名")
        if archive.stat().st_size <= 0 or archive.stat().st_size > 1024 * 1024 * 1024:
            raise ClientComponentError("客户端组件补丁为空或超过 1 GiB")
        try:
            bundle = zipfile.ZipFile(archive, "r")
        except zipfile.BadZipFile as exc:
            raise ClientComponentError("客户端组件补丁 ZIP 已损坏") from exc
        with bundle:
            infos = [item for item in bundle.infolist() if not item.is_dir()]
            names = [item.filename.replace("\\", "/") for item in infos]
            if len(names) != len(set(names)) or PATCH_MANIFEST not in names:
                raise ClientComponentError("客户端组件补丁文件重复或缺少清单")
            if any(not _safe_patch_path(name) for name in names):
                raise ClientComponentError("客户端组件补丁包含不安全路径")
            try:
                raw = json.loads(bundle.read(PATCH_MANIFEST).decode("utf-8-sig"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise ClientComponentError("客户端组件补丁清单不是有效 UTF-8 JSON") from exc
            if (
                not isinstance(raw, dict)
                or raw.get("schemaVersion") != 1
                or raw.get("packageType") != "scbl-client-components-patch"
                or not isinstance(raw.get("components"), list)
                or not raw["components"]
            ):
                raise ClientComponentError("客户端组件补丁清单无效")
            artifacts: list[ComponentPatchArtifact] = []
            components: set[str] = set()
            declared_names = {PATCH_MANIFEST}
            info_by_name = dict(zip(names, infos, strict=True))
            for item in raw["components"]:
                if not isinstance(item, dict):
                    raise ClientComponentError("客户端组件补丁记录无效")
                component = str(item.get("component", ""))
                spec = self._spec(component)
                if component in components:
                    raise ClientComponentError(f"客户端组件补丁重复包含 {component}")
                components.add(component)
                version = _validate_version(str(item.get("version", "")))
                path = str(item.get("path", "")).replace("\\", "/")
                expected_path = f"artifacts/{component}/{spec.filename}"
                digest = str(item.get("sha256", "")).lower()
                size = item.get("size")
                release_notes = normalize_release_notes(
                    item.get("releaseNotes"),
                    label=f"客户端组件补丁 {component} releaseNotes",
                    required=False,
                )
                if (
                    path != expected_path
                    or item.get("file") != spec.filename
                    or item.get("updateMode") != spec.update_mode
                    or not re.fullmatch(r"[0-9a-f]{64}", digest)
                    or type(size) is not int
                    or not 0 < size <= 512 * 1024 * 1024
                    or path not in info_by_name
                ):
                    raise ClientComponentError(f"客户端组件补丁记录无效：{component}")
                payload = bundle.read(info_by_name[path])
                if len(payload) != size or hashlib.sha256(payload).hexdigest() != digest:
                    raise ClientComponentError(f"客户端组件补丁校验失败：{component}")
                declared_names.add(path)
                artifacts.append(
                    ComponentPatchArtifact(
                        component,
                        version,
                        path,
                        digest,
                        size,
                        release_notes,
                    )
                )
            if set(names) != declared_names:
                raise ClientComponentError("客户端组件补丁包含清单之外的文件")
        return ClientComponentPatch(archive, tuple(artifacts), _sha256(archive))

    def publish_patch(
        self,
        archive: Path,
        *,
        channel: str,
    ) -> dict[str, dict[str, Any]]:
        self._require_root()
        self._validate_channel(channel)
        patch = self.verify_patch(archive)
        manifest = self.load_manifest(channel)
        for artifact in patch.artifacts:
            self._validate_patch_progression(
                artifact.component,
                artifact.version,
                channel,
                manifest=manifest,
            )
        published: dict[str, dict[str, Any]] = {}
        # Client artifacts (especially the self-contained launcher) can be much
        # larger than the server's tmpfs. Stage beside the persistent update
        # store so publishing is limited by the data disk, not /tmp memory.
        staging_parent = self.root.parent
        staging_parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(
            prefix=".scbl-client-patch-", dir=staging_parent
        ) as temporary:
            root = Path(temporary)
            with zipfile.ZipFile(patch.archive, "r") as bundle:
                for artifact in patch.artifacts:
                    target = root / COMPONENTS[artifact.component].filename
                    target.write_bytes(bundle.read(artifact.path))
                    published[artifact.component] = self.publish(
                        artifact.component,
                        artifact.version,
                        target,
                        channel=channel,
                        release_notes=artifact.release_notes,
                    )
        return published

    def publish(
        self,
        component: str,
        version: str,
        source: Path,
        *,
        channel: str,
        release_notes: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        self._require_root()
        spec = self._spec(component)
        version = _validate_version(version)
        self._validate_channel(channel)
        self._validate_patch_progression(component, version, channel)
        source = source.resolve(strict=True)
        if not source.is_file() or source.name.lower() != spec.filename.lower():
            raise ClientComponentError(f"{component} 文件名必须是 {spec.filename}")
        if source.stat().st_size <= 0 or source.stat().st_size > 512 * 1024 * 1024:
            raise ClientComponentError("客户端组件为空或超过 512 MiB")
        digest = _sha256(source)
        normalized_notes = normalize_release_notes(
            release_notes,
            label=f"组件 {component} releaseNotes",
            required=False,
        )
        entry = {
            "version": version,
            "sha256": digest,
            "size": source.stat().st_size,
            "url": f"/components/artifacts/{component}/{version}/{spec.filename}",
            "updateMode": spec.update_mode,
            "required": True,
        }
        if normalized_notes is not None:
            entry["releaseNotes"] = normalized_notes
        target_dir = self.artifacts_root / component / version
        target = target_dir / spec.filename
        metadata = target_dir / "component.json"
        if target_dir.exists():
            old = self._read_json(metadata)
            if not target.is_file() or _sha256(target) != digest or old.get("artifact") != entry:
                raise ClientComponentError(
                    f"不可变组件 {component}/{version} 已存在且内容不同，拒绝覆盖"
                )
        else:
            target_dir.parent.mkdir(parents=True, exist_ok=True)
            temporary = Path(
                tempfile.mkdtemp(prefix=f".{version}.", dir=target_dir.parent)
            )
            try:
                # Component artifacts are public update payloads. The update
                # service runs as scbl-update and must be able to traverse a
                # version directory initially created by root.
                os.chmod(temporary, 0o755)
                _copy_file(source, temporary / spec.filename, 0o640)
                self._write_json(
                    temporary / "component.json",
                    {
                        "schemaVersion": SCHEMA_VERSION,
                        "component": component,
                        "publishedAt": _utc_now(),
                        "artifact": entry,
                    },
                )
                os.replace(temporary, target_dir)
            except Exception:
                shutil.rmtree(temporary, ignore_errors=True)
                raise
        # Repair permissions from releases created before the traversal rule.
        os.chmod(target_dir, 0o755)
        self._set_update_owner(target, metadata)
        manifest = self.load_manifest(channel)
        section = "files" if component in FILE_BUNDLES else "components"
        manifest[section][component] = entry
        self._write_manifest(channel, manifest)
        try:
            self._prune_history()
        except OSError as exc:
            warnings.warn(
                f"组件已发布，但旧组件自动保留清理失败：{exc}",
                RuntimeWarning,
                stacklevel=2,
            )
        return entry

    def _validate_patch_progression(
        self,
        component: str,
        version: str,
        channel: str,
        *,
        manifest: dict[str, Any] | None = None,
    ) -> None:
        """Require a patch to advance the selected channel component."""
        self._spec(component)
        version = _validate_version(version)
        current_manifest = manifest if manifest is not None else self.load_manifest(channel)
        section = "files" if component in FILE_BUNDLES else "components"
        existing = current_manifest[section].get(component)
        if not isinstance(existing, dict):
            return
        old_version = _validate_version(str(existing.get("version", "")))
        if _compare_versions(version, old_version) <= 0:
            raise ClientComponentError(
                f"补丁版本必须高于当前版本：{component} 当前 {old_version}，上传 {version}"
            )

    def status(self) -> dict[str, dict[str, Any]]:
        self.initialize()
        return {
            channel: {
                **self.load_manifest(channel)["components"],
                **self.load_manifest(channel)["files"],
            }
            for channel in ("stable", "test")
        }

    def load_manifest(self, channel: str) -> dict[str, Any]:
        self._validate_channel(channel)
        path = self.manifest_path(channel)
        if not path.exists():
            return self._empty_manifest(channel)
        payload = self._read_json(path)
        if (
            payload.get("schemaVersion") != SCHEMA_VERSION
            or payload.get("channel") != channel
            or not isinstance(payload.get("components"), dict)
        ):
            raise ClientComponentError(f"{channel} 客户端组件清单无效")
        payload.setdefault("files", {})
        if not isinstance(payload["files"], dict):
            raise ClientComponentError(f"{channel} 客户端文件清单无效")
        for component, entry in payload["components"].items():
            if not isinstance(entry, dict):
                raise ClientComponentError(f"组件记录无效：{component}")
            self._validate_entry(component, entry)
        for component, entry in payload["files"].items():
            if component not in FILE_BUNDLES or not isinstance(entry, dict):
                raise ClientComponentError(f"客户端文件记录无效：{component}")
            self._validate_entry(component, entry)
        return payload

    def manifest_path(self, channel: str) -> Path:
        self._validate_channel(channel)
        return self.channels_root / channel / "client_components_v2.json"

    def _verify_entry(self, component: str, entry: dict[str, Any]) -> None:
        self._validate_entry(component, entry)
        spec = self._spec(component)
        path = self.artifacts_root / component / entry["version"] / spec.filename
        if not path.is_file() or path.stat().st_size != entry["size"]:
            raise ClientComponentError(f"组件文件缺失或大小不符：{component}")
        if _sha256(path) != entry["sha256"]:
            raise ClientComponentError(f"组件文件 SHA256 不符：{component}")

    def _validate_entry(self, component: str, entry: dict[str, Any]) -> None:
        allowed_keys = {
            "version",
            "sha256",
            "size",
            "url",
            "updateMode",
            "required",
            "releaseNotes",
        }
        if not set(entry).issubset(allowed_keys):
            raise ClientComponentError(f"组件记录包含未知字段：{component}")
        spec = self._spec(component)
        version = _validate_version(str(entry.get("version", "")))
        digest = entry.get("sha256")
        size = entry.get("size")
        url = entry.get("url")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ClientComponentError(f"组件 SHA256 无效：{component}")
        if type(size) is not int or not 0 < size <= 512 * 1024 * 1024:
            raise ClientComponentError(f"组件大小无效：{component}")
        expected_url = f"/components/artifacts/{component}/{version}/{spec.filename}"
        if (
            url != expected_url
            or entry.get("updateMode") != spec.update_mode
        ):
            raise ClientComponentError(f"组件路径或更新方式无效：{component}")
        if entry.get("required") is not True:
            raise ClientComponentError(f"组件必须标记为 required：{component}")
        normalized_notes = normalize_release_notes(
            entry.get("releaseNotes"),
            label=f"组件 {component} releaseNotes",
            required=False,
        )
        if normalized_notes is not None and normalized_notes != entry.get("releaseNotes"):
            raise ClientComponentError(f"组件说明未规范化：{component}")

    def _write_manifest(
        self, channel: str, manifest: dict[str, Any], *, backup: bool = True
    ) -> None:
        manifest = dict(manifest)
        manifest.pop("networkBootstrap", None)
        manifest.update(
            {"schemaVersion": SCHEMA_VERSION, "channel": channel, "generatedAt": _utc_now()}
        )
        path = self.manifest_path(channel)
        manifest_bytes = (
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
        ).encode("utf-8")

        if backup and path.is_file():
            destination = self.backups_root / channel / (
                "client_components_v2." + dt.datetime.now().strftime("%Y%m%d_%H%M%S_%f") + ".json"
            )
            _copy_file(path, destination, 0o640)

        previous_manifest = path.read_bytes() if path.is_file() else None
        try:
            _atomic_write(path, manifest_bytes, 0o640)
            self._set_update_owner(path)
            from .server_descriptor import refresh_server_descriptor

            if self.refresh_descriptor:
                refresh_server_descriptor(self.root)
            try:
                self._prune_manifest_backups(channel)
            except OSError as exc:
                warnings.warn(
                    f"组件清单已更新，但旧清单备份清理失败：{exc}",
                    RuntimeWarning,
                    stacklevel=2,
                )
        except Exception:
            if previous_manifest is None:
                path.unlink(missing_ok=True)
            else:
                _atomic_write(path, previous_manifest, 0o640)
            raise

    def _prune_manifest_backups(self, channel: str) -> None:
        root = self.backups_root / channel
        if not root.is_dir():
            return
        backups = sorted(
            (
                path
                for path in root.glob("client_components_v2.*.json")
                if path.is_file() and not path.is_symlink()
            ),
            key=lambda path: path.name,
            reverse=True,
        )
        for path in backups[RETAINED_GENERATIONS:]:
            path.unlink()

    def _prune_history(self) -> None:
        protected: dict[str, set[str]] = {name: set() for name in COMPONENTS}
        for channel in ("stable", "test"):
            try:
                manifest = self.load_manifest(channel)
            except ClientComponentError:
                continue
            for section in ("components", "files"):
                for component, entry in manifest[section].items():
                    if component in protected and isinstance(entry, dict):
                        version = entry.get("version")
                        if isinstance(version, str):
                            protected[component].add(version)

        for component in COMPONENTS:
            root = self.artifacts_root / component
            if not root.is_dir():
                continue
            versions = sorted(
                (
                    path
                    for path in root.iterdir()
                    if path.is_dir() and not path.is_symlink()
                ),
                key=lambda path: tuple(int(part) for part in path.name.split("."))
                if re.fullmatch(r"\d+\.\d+\.\d+", path.name)
                else (-1,),
                reverse=True,
            )
            keep = {path.resolve() for path in versions if path.name in protected[component]}
            for path in versions:
                if len(keep) >= RETAINED_GENERATIONS:
                    break
                keep.add(path.resolve())
            for path in versions:
                if path.resolve() not in keep:
                    shutil.rmtree(path)

    @staticmethod
    def _empty_manifest(channel: str) -> dict[str, Any]:
        return {
            "schemaVersion": SCHEMA_VERSION,
            "channel": channel,
            "generatedAt": _utc_now(),
            "components": {},
            "files": {},
        }

    @staticmethod
    def _read_json(path: Path) -> dict[str, Any]:
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ClientComponentError(f"JSON 文件无法读取：{path}") from exc
        return payload if isinstance(payload, dict) else {}

    @staticmethod
    def _write_json(path: Path, payload: dict[str, Any]) -> None:
        _atomic_write(
            path, (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode(), 0o640
        )

    @staticmethod
    def _spec(component: str) -> ComponentSpec:
        try:
            return COMPONENTS[component]
        except KeyError as exc:
            raise ClientComponentError(f"未知客户端组件：{component}") from exc

    @staticmethod
    def _validate_channel(channel: str) -> None:
        if channel not in {"stable", "test"}:
            raise ClientComponentError(f"未知客户端组件通道：{channel}")

    @staticmethod
    def _require_root() -> None:
        if os.name != "posix" or os.geteuid() != 0:
            raise ClientComponentError("客户端组件发布需要在 Linux 服务端使用 root 执行")

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
            # The updater owns published payloads, while the shared SCBL group
            # lets the read-only control-plane service consume the stable
            # component manifest for client-version admission.  Leaving the
            # group as root makes the 0640 manifest unreadable and silently
            # falls back to the older full-package version.
            os.chown(path, uid, gid)


def download_online_component(
    repository: str,
    component: str,
    *,
    channel: str,
    destination: Path,
) -> DownloadedComponent:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ClientComponentError("GitHub 仓库必须是 owner/repository 格式")
    if channel not in {"stable", "test"}:
        raise ClientComponentError(f"未知客户端组件通道：{channel}")
    try:
        spec = COMPONENTS[component]
    except KeyError as exc:
        raise ClientComponentError(f"未知客户端组件：{component}") from exc

    tag = f"client-component-{component}-{channel}"
    base = f"https://github.com/{repository}/releases/download/{tag}"
    metadata_bytes = _download_https(f"{base}/component.json", 64 * 1024)
    try:
        metadata = json.loads(metadata_bytes.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ClientComponentError("GitHub 组件元数据不是有效 UTF-8 JSON") from exc
    if not isinstance(metadata, dict) or metadata.get("schemaVersion") != SCHEMA_VERSION:
        raise ClientComponentError("GitHub 组件元数据版本无效")
    if metadata.get("component") != component or metadata.get("file") != spec.filename:
        raise ClientComponentError("GitHub 组件名称或文件名不匹配")
    if metadata.get("updateMode") != spec.update_mode:
        raise ClientComponentError("GitHub 组件更新方式不匹配")
    version = _validate_version(str(metadata.get("version", "")))
    digest = str(metadata.get("sha256", "")).lower()
    size = metadata.get("size")
    if not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise ClientComponentError("GitHub 组件 SHA256 无效")
    if type(size) is not int or not 0 < size <= 512 * 1024 * 1024:
        raise ClientComponentError("GitHub 组件大小无效")

    destination.mkdir(parents=True, exist_ok=True)
    target = destination / spec.filename
    payload = _download_https(f"{base}/{spec.filename}", size)
    if len(payload) != size:
        raise ClientComponentError(
            f"GitHub 组件大小不符：expected={size}, actual={len(payload)}"
        )
    if hashlib.sha256(payload).hexdigest() != digest:
        raise ClientComponentError("GitHub 组件 SHA256 校验失败")
    _atomic_write(target, payload, 0o600)
    return DownloadedComponent(component, version, target, digest, size)


def _validate_version(value: str) -> str:
    value = value.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise ClientComponentError("组件版本号必须是 X.Y.Z")
    return value


def normalize_release_notes(
    value: Any,
    *,
    label: str,
    required: bool,
) -> dict[str, list[str]] | None:
    if value is None:
        if required:
            raise ClientComponentError(f"{label} 缺失")
        return None
    if not isinstance(value, dict) or set(value) != RELEASE_NOTE_LANGUAGES:
        raise ClientComponentError(f"{label} 必须精确包含 zh/en")

    result: dict[str, list[str]] = {}
    for language in ("zh", "en"):
        entries = value.get(language)
        if (
            not isinstance(entries, list)
            or not 1 <= len(entries) <= MAX_RELEASE_NOTES_PER_LANGUAGE
        ):
            raise ClientComponentError(
                f"{label}.{language} 必须包含 1～{MAX_RELEASE_NOTES_PER_LANGUAGE} 条文本"
            )
        normalized: list[str] = []
        for index, item in enumerate(entries):
            if not isinstance(item, str):
                raise ClientComponentError(f"{label}.{language}[{index}] 不是文本")
            text = unicodedata.normalize("NFC", item.strip())
            if (
                not text
                or len(text) > MAX_RELEASE_NOTE_CHARACTERS
                or any(unicodedata.category(character) == "Cc" for character in text)
            ):
                raise ClientComponentError(
                    f"{label}.{language}[{index}] 为空、过长或包含控制字符"
                )
            normalized.append(text)
        result[language] = normalized
    return result


def _safe_patch_path(value: str) -> bool:
    normalized = value.replace("\\", "/")
    if not normalized or normalized.startswith("/") or ":" in normalized:
        return False
    return all(part not in {"", ".", ".."} for part in normalized.split("/"))


def _version_tuple(value: str) -> tuple[int, ...]:
    numbers = tuple(int(x) for x in re.findall(r"\d+", value))
    if not numbers:
        raise ClientComponentError("组件版本号必须包含数字")
    return numbers


def _compare_versions(left: str, right: str) -> int:
    left_numbers = _version_tuple(left)
    right_numbers = _version_tuple(right)
    length = max(len(left_numbers), len(right_numbers))
    padded_left = left_numbers + (0,) * (length - len(left_numbers))
    padded_right = right_numbers + (0,) * (length - len(right_numbers))
    return (padded_left > padded_right) - (padded_left < padded_right)


def _download_https(url: str, maximum_size: int) -> bytes:
    if not url.startswith("https://github.com/"):
        raise ClientComponentError("只允许从 GitHub HTTPS Release 下载组件")
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "SCBL-Server-Manager/2",
            "Accept": "application/octet-stream",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            final_url = response.geturl()
            if not final_url.startswith("https://"):
                raise ClientComponentError("GitHub 组件下载发生了不安全重定向")
            payload = response.read(maximum_size + 1)
    except (OSError, urllib.error.URLError) as exc:
        raise ClientComponentError(f"GitHub 组件下载失败：{url}") from exc
    if len(payload) > maximum_size:
        raise ClientComponentError("GitHub 组件下载超过允许大小")
    return payload


def _utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def _copy_file(source: Path, destination: Path, mode: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{destination.name}.", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as output, source.open("rb") as input_file:
            shutil.copyfileobj(input_file, output, length=1024 * 1024)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
