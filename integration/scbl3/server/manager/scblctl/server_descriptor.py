from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import re
from pathlib import Path
from typing import Any

from .config import ConfigError, ConfigNotFoundError, ServerConfig, load_config
from .file_io import atomic_write_bytes, sha256_file
from .paths import DEPLOYMENT_PATHS, RuntimePaths

try:
    import grp
    import pwd
except ImportError:  # pragma: no cover - Windows development host
    grp = None  # type: ignore[assignment]
    pwd = None  # type: ignore[assignment]


DESCRIPTOR_NAME = "scbl-server.json"
DESCRIPTOR_SCHEMA_VERSION = 1
_VERSION = re.compile(r"\d+\.\d+\.\d+")
_DIGEST = re.compile(r"[0-9a-f]{64}")
class ServerDescriptorError(RuntimeError):
    pass


class ServerDescriptorManager:
    """Build the only public bootstrap document consumed by SCBL 3.0 clients."""

    def __init__(self, root: Path | None = None) -> None:
        self.root = root or Path(DEPLOYMENT_PATHS.data) / "client-updates"
        self.path = self.root / DESCRIPTOR_NAME

    def build(self, config: ServerConfig) -> dict[str, Any]:
        errors = config.validate()
        if errors:
            raise ServerDescriptorError("服务端配置无效：\n- " + "\n- ".join(errors))

        channel = config.server.mode
        components, generation = self._component_snapshot(channel)
        full_package = self._full_package_snapshot()
        launcher = components["components"].get("launcher")
        latest = launcher.get("version") if isinstance(launcher, dict) else None
        if not isinstance(latest, str) or not _VERSION.fullmatch(latest):
            latest = full_package.get("version") if full_package else "0.0.0"

        return {
            "schemaVersion": DESCRIPTOR_SCHEMA_VERSION,
            "generatedAt": _utc_now(),
            "server": {
                "id": config.server.server_id,
                "mode": channel,
            },
            "client": {
                "version": latest,
                "updateBaseUrl": config.services.public_update_base_url,
                "componentGeneration": generation,
                "components": components["components"],
                "files": components["files"],
                "fullPackage": full_package,
            },
            "network": {
                "publicHost": config.server.public_host,
                "useOriginHost": not bool(config.server.public_host),
                "publicPort": config.network.public_port,
                "transports": ["quic", "wss"],
                "networkName": config.easytier.network_name,
                "secret": config.network.secret,
                "mtu": config.network.mtu,
                "enableIpv6": config.network.enable_ipv6,
            },
            "controlPlane": {
                "address": config.network.virtual_ip,
                "port": config.services.control_port,
            },
            "dedicated": {
                "address": config.network.virtual_ip,
                "configPort": 80,
                "authPort": 50051,
                "gamePort": 21126,
                "protocol": "prudp",
            },
            "announcements": {
                kind: self._announcement(kind, latest)
                for kind in ("active", "startup", "update")
            },
        }

    def write(self, config: ServerConfig) -> Path:
        payload = self.build(config)
        content = (
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
        ).encode("utf-8")
        atomic_write_bytes(self.path, content, 0o640)
        self._set_update_owner(self.path)
        return self.path

    def _component_snapshot(self, channel: str) -> tuple[dict[str, dict[str, Any]], str]:
        path = self.root / "components" / "channels" / channel / "client_components_v2.json"
        if not path.is_file():
            return {"components": {}, "files": {}}, ""
        raw = path.read_bytes()
        try:
            payload = json.loads(raw)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ServerDescriptorError(f"{channel} 客户端组件清单无效") from exc
        if (
            not isinstance(payload, dict)
            or payload.get("schemaVersion") != 2
            or payload.get("channel") != channel
            or not isinstance(payload.get("components"), dict)
            or not isinstance(payload.get("files", {}), dict)
        ):
            raise ServerDescriptorError(f"{channel} 客户端组件清单结构无效")
        result: dict[str, dict[str, Any]] = {"components": {}, "files": {}}
        for section in result:
            for name, entry in payload.get(section, {}).items():
                if not isinstance(name, str) or not isinstance(entry, dict):
                    raise ServerDescriptorError(f"客户端组件记录无效：{name}")
                self._validate_component_entry(name, entry)
                result[section][name] = dict(entry)
        return result, hashlib.sha256(raw).hexdigest()

    def _validate_component_entry(self, name: str, entry: dict[str, Any]) -> None:
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
            raise ServerDescriptorError(f"客户端组件记录包含未知字段：{name}")
        version = entry.get("version")
        digest = entry.get("sha256")
        size = entry.get("size")
        url = entry.get("url")
        if (
            not isinstance(version, str)
            or not _VERSION.fullmatch(version)
            or not isinstance(digest, str)
            or not _DIGEST.fullmatch(digest)
            or type(size) is not int
            or size <= 0
            or not isinstance(url, str)
            or not url.startswith("/components/artifacts/")
            or ".." in url
            or "\\" in url
            or entry.get("required") is not True
        ):
            raise ServerDescriptorError(f"客户端组件记录无效：{name}")
        assert isinstance(url, str) and isinstance(size, int) and isinstance(digest, str)
        candidate = self.root / url.lstrip("/")
        source = candidate.resolve()
        artifacts = (self.root / "components" / "artifacts").resolve()
        if (
            not source.is_relative_to(artifacts)
            or candidate.is_symlink()
            or not source.is_file()
            or source.stat().st_size != size
            or sha256_file(source) != digest
        ):
            raise ServerDescriptorError(f"客户端组件文件缺失或漂移：{name}")

    def _full_package_snapshot(self) -> dict[str, Any] | None:
        manifest_path = self.root / "client_update_manifest.json"
        payload = self._read_optional_json(manifest_path, "客户端完整包清单")
        if payload is None:
            return None
        version = payload.get("version")
        relative = payload.get("fullPackage")
        digest = payload.get("fullPackageSha256")
        if not all(isinstance(value, str) for value in (version, relative, digest)):
            return None
        assert isinstance(version, str) and isinstance(relative, str) and isinstance(digest, str)
        if not _VERSION.fullmatch(version) or not _DIGEST.fullmatch(digest):
            raise ServerDescriptorError("客户端完整包版本或 SHA256 无效")
        if relative.startswith(("/", "\\")) or ".." in relative or "\\" in relative:
            raise ServerDescriptorError("客户端完整包路径无效")
        candidate = self.root / relative
        path = candidate.resolve()
        if (
            not path.is_relative_to(self.root.resolve())
            or candidate.is_symlink()
            or not path.is_file()
        ):
            raise ServerDescriptorError("客户端完整包文件不存在")
        declared_size = payload.get("fullPackageSize")
        size = path.stat().st_size
        if declared_size is not None and declared_size != size:
            raise ServerDescriptorError("客户端完整包大小与清单不一致")
        if sha256_file(path) != digest:
            raise ServerDescriptorError("客户端完整包 SHA256 与实际文件不一致")
        return {
            "version": version,
            "url": "/" + relative,
            "sha256": digest,
            "size": size,
        }

    def _announcement(self, kind: str, current_client_version: str) -> dict[str, Any] | None:
        name = {
            "active": "active_announcement.json",
            "startup": "startup_announcement.json",
            "update": "update_announcement.json",
        }[kind]
        payload = self._read_optional_json(self.root / name, f"{kind} 公告")
        if payload is None or payload.get("enabled") is not True:
            return self._default_update_announcement(current_client_version) if kind == "update" else None
        title = payload.get("title")
        body = payload.get("body")
        if not isinstance(title, str) or not title.strip() or not isinstance(body, str) or not body.strip():
            raise ServerDescriptorError(f"{kind} 公告缺少标题或正文")
        allowed = {
            "id", "title", "body", "title_en", "body_en", "level", "showOnce", "version"
        }
        announcement = {key: payload[key] for key in allowed if key in payload}
        if kind == "update" and announcement.get("version") != current_client_version:
            return self._default_update_announcement(current_client_version)
        return announcement

    @staticmethod
    def _default_update_announcement(version: str) -> dict[str, Any] | None:
        if not _VERSION.fullmatch(version) or version == "0.0.0":
            return None
        return {
            "id": f"update-{version}",
            "version": version,
            "title": f"客户端更新 {version}",
            "body": "发现新的客户端组件。确认后将下载、验证并安装本次更新。",
            "title_en": f"Client Update {version}",
            "body_en": "New client components are available. Confirm to download, verify, and install the update.",
            "level": "info",
            "showOnce": True,
        }

    @staticmethod
    def _read_optional_json(path: Path, label: str) -> dict[str, Any] | None:
        if not path.exists():
            return None
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ServerDescriptorError(f"{label}不是有效 JSON") from exc
        if not isinstance(payload, dict):
            raise ServerDescriptorError(f"{label}必须是 JSON 对象")
        return payload

    @staticmethod
    def _set_update_owner(path: Path) -> None:
        if pwd is None or grp is None or not hasattr(os, "chown"):
            return
        try:
            uid = pwd.getpwnam("scbl-update").pw_uid
            gid = grp.getgrnam("scbl").gr_gid
        except KeyError:
            return
        os.chown(path, uid, gid)


def refresh_server_descriptor(
    root: Path | None = None,
    *,
    config: ServerConfig | None = None,
    config_path: Path | None = None,
    required: bool = False,
) -> Path | None:
    if config is None:
        path = config_path or RuntimePaths.defaults().config
        try:
            config = load_config(path)
        except ConfigNotFoundError:
            if required:
                raise ServerDescriptorError(f"服务端配置不存在：{path}")
            return None
        except ConfigError as exc:
            raise ServerDescriptorError(str(exc)) from exc
    return ServerDescriptorManager(root).write(config)


def _utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
