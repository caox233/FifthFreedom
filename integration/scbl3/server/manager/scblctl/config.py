from __future__ import annotations

import ipaddress
import re
import secrets
import tomllib
from dataclasses import asdict, dataclass, fields
from pathlib import Path
from typing import Any, ClassVar, get_type_hints
from urllib.parse import urlsplit

from .file_io import atomic_write_text


CONFIG_SCHEMA_VERSION = 5
LEGACY_DEFAULT_SECRET = "CHANGE_ME_SCBL_PUBLIC_SECRET_2026"


class ConfigError(ValueError):
    pass


class ConfigNotFoundError(ConfigError):
    pass


@dataclass(slots=True)
class ServerSection:
    server_id: str = "scbl-server"
    mode: str = "stable"
    public_host: str = ""


@dataclass(slots=True)
class NetworkSection:
    public_port: int = 11010
    secret: str = ""
    virtual_ip: str = "10.66.0.1"
    virtual_cidr: str = "10.66.0.1/24"
    virtual_network: str = "10.66.0.0/24"
    mtu: int = 1380
    enable_ipv6: bool = True


@dataclass(slots=True)
class ServicesSection:
    update_port: int = 18080
    control_port: int = 19080
    heartbeat_ttl: int = 20
    public_update_base_url: str = ""


@dataclass(slots=True)
class EasyTierSection:
    network_name: str = "scbl-public"
    instance_name: str = "scbl-public-server"
    instance_id: str = "00000000-0000-0000-0000-000000000001"
    rpc_port: int = 15966


@dataclass(slots=True)
class UpdatesSection:
    repository: str = "caox233/SCBL"


@dataclass(slots=True)
class DdnsSection:
    enabled: bool = False
    interval_seconds: int = 300
    config_path: str = "/opt/ddns-go/.ddns_go_config.yaml"


SECTION_TYPES = {
    "server": ServerSection,
    "network": NetworkSection,
    "services": ServicesSection,
    "easytier": EasyTierSection,
    "updates": UpdatesSection,
    "ddns": DdnsSection,
}


@dataclass(slots=True)
class ServerConfig:
    schema_version: int
    server: ServerSection
    network: NetworkSection
    services: ServicesSection
    easytier: EasyTierSection
    updates: UpdatesSection
    ddns: DdnsSection

    SECRET_FIELDS: ClassVar[set[str]] = {"network.secret"}

    @classmethod
    def new(
        cls,
        *,
        public_host: str = "",
        mode: str = "stable",
        server_id: str = "scbl-server",
    ) -> "ServerConfig":
        return cls(
            schema_version=CONFIG_SCHEMA_VERSION,
            server=ServerSection(
                server_id=server_id,
                mode=mode,
                public_host=public_host,
            ),
            network=NetworkSection(secret=secrets.token_urlsafe(32)),
            services=ServicesSection(),
            easytier=EasyTierSection(),
            updates=UpdatesSection(),
            ddns=DdnsSection(),
        )

    @classmethod
    def from_mapping(cls, raw: dict[str, Any]) -> "ServerConfig":
        allowed = {"schema_version", *SECTION_TYPES}
        unknown = sorted(set(raw) - allowed)
        if unknown:
            raise ConfigError(f"未知的顶层配置项：{', '.join(unknown)}")
        version = raw.get("schema_version")
        if type(version) is not int:
            raise ConfigError("schema_version 必须是整数")
        if version != CONFIG_SCHEMA_VERSION:
            raise ConfigError(
                f"不支持配置版本 {version}；当前支持 {CONFIG_SCHEMA_VERSION}"
            )

        sections: dict[str, Any] = {}
        for name, section_type in SECTION_TYPES.items():
            values = raw.get(name, {})
            if not isinstance(values, dict):
                raise ConfigError(f"[{name}] 必须是 TOML 表")
            values = dict(values)
            known = {field.name for field in fields(section_type)}
            extra = sorted(set(values) - known)
            if extra:
                raise ConfigError(f"[{name}] 中存在未知项：{', '.join(extra)}")
            hints = get_type_hints(section_type)
            for key, value in values.items():
                expected = hints[key]
                if expected is int and type(value) is not int:
                    raise ConfigError(f"{name}.{key} 必须是整数")
                if expected is bool and type(value) is not bool:
                    raise ConfigError(f"{name}.{key} 必须是布尔值")
                if expected is str and not isinstance(value, str):
                    raise ConfigError(f"{name}.{key} 必须是字符串")
            sections[name] = section_type(**values)
        return cls(schema_version=version, **sections)

    def to_mapping(self, *, redact: bool = False) -> dict[str, Any]:
        result = asdict(self)
        if redact and result["network"]["secret"]:
            result["network"]["secret"] = "********"
        return result

    def get(self, dotted_key: str) -> Any:
        section_name, field_name = _split_key(dotted_key)
        section = getattr(self, section_name)
        if not hasattr(section, field_name):
            raise ConfigError(f"未知配置项：{dotted_key}")
        return getattr(section, field_name)

    def set(self, dotted_key: str, raw_value: str) -> Any:
        section_name, field_name = _split_key(dotted_key)
        section_type = SECTION_TYPES.get(section_name)
        if section_type is None or not hasattr(self, section_name):
            raise ConfigError(f"未知配置项：{dotted_key}")
        hints = get_type_hints(section_type)
        if field_name not in hints:
            raise ConfigError(f"未知配置项：{dotted_key}")
        value = _coerce(raw_value, hints[field_name], dotted_key)
        setattr(getattr(self, section_name), field_name, value)
        return value

    def validate(self) -> list[str]:
        errors: list[str] = []
        for section_name, section_type in SECTION_TYPES.items():
            section = getattr(self, section_name)
            for field in fields(section_type):
                value = getattr(section, field.name)
                if isinstance(value, str) and any(char in value for char in ("\x00", "\r", "\n")):
                    errors.append(f"{section_name}.{field.name} 不能包含换行或 NUL 字符")
        if self.server.public_host and not _valid_public_host(self.server.public_host):
            errors.append("server.public_host 不是有效的 IP 地址或域名")
        if (
            self.services.public_update_base_url
            and not _valid_public_update_base_url(self.services.public_update_base_url)
        ):
            errors.append("services.public_update_base_url 必须是 http(s)://主机[:端口]")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}", self.server.server_id):
            errors.append("server.server_id 必须是 1-64 位字母、数字、点、下划线或连字符")
        if self.server.mode not in {"stable", "test"}:
            errors.append("server.mode 只能是 stable 或 test")
        for key in (
            "network.public_port",
            "services.update_port",
            "services.control_port",
            "easytier.rpc_port",
        ):
            value = self.get(key)
            if not 1 <= value <= 65535:
                errors.append(f"{key} 必须在 1-65535 之间")
        configured_ports = {
            self.network.public_port,
            self.services.update_port,
            self.services.control_port,
            self.easytier.rpc_port,
        }
        if len(configured_ports) != 4:
            errors.append("公网、更新、控制面与 EasyTier RPC 端口必须互不相同")
        dedicated_ports = {80, 8000, 50051, 21126, 21127}
        collisions = sorted(configured_ports & dedicated_ports)
        if collisions:
            errors.append(
                "配置端口与 Dedicated 固定端口冲突："
                + ", ".join(str(port) for port in collisions)
            )
        if not 576 <= self.network.mtu <= 9000:
            errors.append("network.mtu 必须在 576-9000 之间")
        if not 5 <= self.services.heartbeat_ttl <= 3600:
            errors.append("services.heartbeat_ttl 必须在 5-3600 之间")
        if not 10 <= self.ddns.interval_seconds <= 86400:
            errors.append("ddns.interval_seconds 必须在 10-86400 之间")
        if len(self.network.secret) < 24:
            errors.append("network.secret 至少需要 24 个字符")
        if self.network.secret == LEGACY_DEFAULT_SECRET:
            errors.append("network.secret 仍是旧版公开默认值，必须更换")

        try:
            virtual_ip = ipaddress.ip_address(self.network.virtual_ip)
            cidr = ipaddress.ip_interface(self.network.virtual_cidr)
            virtual_network = ipaddress.ip_network(
                self.network.virtual_network, strict=False
            )
            if not all(
                value.version == 4
                for value in (virtual_ip, cidr.ip, virtual_network)
            ):
                errors.append("虚拟网络必须使用 IPv4")
            elif virtual_ip != cidr.ip:
                errors.append("network.virtual_ip 与 network.virtual_cidr 的地址不一致")
            if cidr.version == virtual_network.version and cidr.network != virtual_network:
                errors.append("network.virtual_cidr 与 network.virtual_network 不属于同一网段")
            if virtual_ip.version == virtual_network.version and virtual_ip not in virtual_network:
                errors.append("network.virtual_ip 不在 network.virtual_network 中")
            if virtual_network.version == 4 and virtual_ip in {
                virtual_network.network_address,
                virtual_network.broadcast_address,
            }:
                errors.append("服务端虚拟 IP 不能是网络地址或广播地址")
        except (TypeError, ValueError) as exc:
            errors.append(f"虚拟网络配置无效：{exc}")

        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", self.updates.repository):
            errors.append("updates.repository 必须是 owner/repository 格式")

        return errors


def _split_key(dotted_key: str) -> tuple[str, str]:
    parts = dotted_key.split(".")
    if len(parts) != 2 or not all(parts):
        raise ConfigError("配置项必须使用 section.name 格式")
    return parts[0], parts[1]


def _coerce(raw: str, expected: type, key: str) -> Any:
    if expected is str:
        return raw
    if expected is int:
        try:
            return int(raw)
        except ValueError as exc:
            raise ConfigError(f"{key} 必须是整数") from exc
    if expected is bool:
        normalized = raw.strip().lower()
        if normalized in {"true", "yes", "y", "1", "on"}:
            return True
        if normalized in {"false", "no", "n", "0", "off"}:
            return False
        raise ConfigError(f"{key} 必须是 true 或 false")
    raise ConfigError(f"{key} 使用了不支持的类型")


def _valid_public_host(value: str) -> bool:
    if not value or len(value) > 253 or "://" in value or "/" in value:
        return False
    try:
        ipaddress.ip_address(value.strip("[]"))
        return True
    except ValueError:
        pass
    labels = value.rstrip(".").split(".")
    label = re.compile(r"(?!-)[A-Za-z0-9-]{1,63}(?<!-)$")
    return bool(labels) and all(label.fullmatch(part) for part in labels)


def _valid_public_update_base_url(value: str) -> bool:
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except ValueError:
        return False
    return (
        parsed.scheme in {"http", "https"}
        and bool(parsed.hostname)
        and _valid_public_host(parsed.hostname or "")
        and parsed.username is None
        and parsed.password is None
        and parsed.path == ""
        and not parsed.query
        and not parsed.fragment
        and (port is None or 1 <= port <= 65535)
    )


def load_config(path: Path) -> ServerConfig:
    try:
        with path.open("rb") as handle:
            raw = tomllib.load(handle)
    except FileNotFoundError as exc:
        raise ConfigNotFoundError(f"配置文件不存在：{path}") from exc
    except tomllib.TOMLDecodeError as exc:
        raise ConfigError(f"TOML 解析失败：{exc}") from exc
    return ServerConfig.from_mapping(raw)


def save_config(config: ServerConfig, path: Path, *, overwrite: bool = True) -> None:
    errors = config.validate()
    if errors:
        raise ConfigError("配置校验失败：\n- " + "\n- ".join(errors))
    if path.exists() and not overwrite:
        raise ConfigError(f"配置文件已存在：{path}")
    atomic_write_text(path, dump_toml(config), 0o600)


def dump_toml(config: ServerConfig, *, redact: bool = False) -> str:
    mapping = config.to_mapping(redact=redact)
    lines = [f"schema_version = {CONFIG_SCHEMA_VERSION}", ""]
    for section_name in SECTION_TYPES:
        lines.append(f"[{section_name}]")
        for key, value in mapping[section_name].items():
            lines.append(f"{key} = {_toml_value(value)}")
        lines.append("")
    return "\n".join(lines)


def _toml_value(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, str):
        escaped = (
            value.replace("\\", "\\\\")
            .replace('"', '\\"')
            .replace("\n", "\\n")
            .replace("\r", "\\r")
            .replace("\t", "\\t")
        )
        return f'"{escaped}"'
    raise TypeError(f"无法序列化 TOML 类型：{type(value).__name__}")


IMPACT_MAP: dict[str, frozenset[str]] = {
    "server.server_id": frozenset({"control", "update", "client-metadata"}),
    "server.mode": frozenset({"control", "update", "client-metadata"}),
    "server.public_host": frozenset({"client-metadata", "ddns"}),
    "updates.repository": frozenset({"updater"}),
    "network.public_port": frozenset({"tunnel", "firewall", "client-metadata"}),
    "network.secret": frozenset({"tunnel", "client-metadata"}),
    "network.virtual_ip": frozenset({"tunnel", "dedicated", "control", "update"}),
    "network.virtual_cidr": frozenset({"tunnel", "firewall"}),
    "network.virtual_network": frozenset({"tunnel", "firewall"}),
    "network.mtu": frozenset({"tunnel", "client-metadata"}),
    "network.enable_ipv6": frozenset({"tunnel", "update", "firewall"}),
    "services.update_port": frozenset({"update", "firewall", "client-metadata"}),
    "services.public_update_base_url": frozenset({"update", "client-metadata"}),
    "services.control_port": frozenset({"control", "dedicated"}),
    "services.heartbeat_ttl": frozenset({"control"}),
    "easytier.network_name": frozenset({"tunnel", "client-metadata"}),
    "easytier.instance_name": frozenset({"tunnel"}),
    "easytier.instance_id": frozenset({"tunnel"}),
    "easytier.rpc_port": frozenset({"tunnel"}),
    "ddns.enabled": frozenset({"ddns"}),
    "ddns.interval_seconds": frozenset({"ddns"}),
    "ddns.config_path": frozenset({"ddns"}),
}


def impact_for(changed_keys: list[str] | set[str]) -> list[str]:
    impacted: set[str] = set()
    for key in changed_keys:
        impacted.update(IMPACT_MAP.get(key, {"manager"}))
    return sorted(impacted)
