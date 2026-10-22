from __future__ import annotations

import os
import re
import shutil
import sqlite3
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

try:
    import grp
    import pwd
except ImportError:  # pragma: no cover - exercised by Windows transaction tests
    grp = None  # type: ignore[assignment]
    pwd = None  # type: ignore[assignment]

from . import __version__
from .config import ServerConfig
from .file_io import atomic_write_bytes as _atomic_write_bytes, atomic_write_text
from .firewall import ufw_commands
from .external_easytier import EasyTierInstallError, EasyTierManager
from .paths import DEPLOYMENT_PATHS
from .release import RuntimeManifest, activate_release, stage_release
from .service_lifecycle import restart_runtime_stack
from .services import SERVICES, SystemdManager
from .server_descriptor import refresh_server_descriptor
from .system import CommandRunner
from .templates import (
    WAIT_SCBL0,
    load_control_auth_policy,
    load_or_create_control_auth_key,
    render_dedicated_config,
    render_easytier_config,
    render_runtime_env,
    render_systemd_units,
)


class ProvisionError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class ProvisionLayout:
    current: Path = Path(DEPLOYMENT_PATHS.current)
    releases: Path = Path(DEPLOYMENT_PATHS.releases)
    cache: Path = Path(DEPLOYMENT_PATHS.cache)
    data: Path = Path(DEPLOYMENT_PATHS.data)
    config_root: Path = Path("/etc/scbl")
    unit_root: Path = Path("/etc/systemd/system")
    wait_script: Path = Path("/usr/local/lib/scbl/wait-scbl0")
    ipv4_forwarding_file: Path = Path("/etc/sysctl.d/90-scbl-ipv4-forwarding.conf")


@dataclass(frozen=True, slots=True)
class ManagedFileSnapshot:
    content: bytes | None
    mode: int | None


@dataclass(frozen=True, slots=True)
class RuntimeTransactionSnapshot:
    previous_runtime: Path | None
    units: dict[str, ManagedFileSnapshot]
    migrations: dict[int, str]


RUNTIME_UNIT_NAMES = (
    "scbl-tunnel.service",
    "scbl-dedicated.service",
    "scbl-control-plane.service",
    "scbl-update.service",
)


class Provisioner:
    def __init__(
        self,
        runner: CommandRunner | None = None,
        *,
        layout: ProvisionLayout | None = None,
        sleep: Callable[[float], None] = time.sleep,
        easytier_manager: EasyTierManager | None = None,
    ) -> None:
        self.runner = runner or CommandRunner()
        self.systemd = SystemdManager(self.runner)
        self.layout = layout or ProvisionLayout()
        self.sleep = sleep
        self.easytier_manager = easytier_manager or EasyTierManager(runner=self.runner)

    def verify(self, package_dir: Path) -> RuntimeManifest:
        manifest = RuntimeManifest.load(package_dir)
        manifest.verify(package_dir)
        return manifest

    def install(
        self,
        config: ServerConfig,
        package_dir: Path,
        *,
        restart_units: set[str] | None = None,
    ) -> Path:
        self._require_linux_root()
        errors = config.validate()
        if errors:
            raise ProvisionError("配置无效：\n- " + "\n- ".join(errors))
        manifest = self.verify(package_dir)
        self._create_accounts()
        self._create_directories(config)
        try:
            self.easytier_manager.ensure_installed()
        except EasyTierInstallError as exc:
            raise ProvisionError(f"EasyTier 外部组件不可用：{exc}") from exc
        target, _manifest = stage_release(package_dir, self.layout.releases)
        snapshot = self._snapshot_transaction()
        if not manifest.supports_database_checkpoint(snapshot.migrations):
            raise ProvisionError(
                "候选 Runtime 的 Dedicated migration 集不兼容当前数据库 checkpoint"
            )
        activate_release(target, self.layout.current)
        try:
            self._install_runtime_files(config, target)
            self._write_systemd_units(config, manifest)
            self._configure_ipv4_forwarding()
            self._configure_firewall(config)
            self._start_and_verify(config, restart_units=restart_units)
        except Exception as install_error:
            try:
                recovery = self._recover_failed_install(snapshot, target, manifest)
            except Exception as recovery_error:
                raise ProvisionError(
                    "安装失败且 Runtime 事务恢复失败，需要人工检查："
                    f"install={install_error}; recovery={recovery_error}"
                ) from install_error
            raise ProvisionError(
                f"安装失败；Runtime 已按 {recovery} 恢复：{install_error}"
            ) from install_error
        return target

    def repair(self, config: ServerConfig) -> Path:
        """Rebuild managed files from the active, already verified release."""

        self._require_linux_root()
        errors = config.validate()
        if errors:
            raise ProvisionError("配置无效：\n- " + "\n- ".join(errors))
        current = self.layout.current
        if not current.is_symlink():
            raise ProvisionError(f"当前运行时链接不存在或无效：{current}")
        target = current.resolve(strict=True)
        manifest = self.verify(target)
        self._create_accounts()
        self._create_directories(config)
        try:
            self.easytier_manager.ensure_installed()
        except EasyTierInstallError as exc:
            raise ProvisionError(f"EasyTier 外部组件不可用：{exc}") from exc
        self._install_runtime_files(config, target)
        self._write_systemd_units(config, manifest)
        self._configure_ipv4_forwarding()
        self._configure_firewall(config)
        self._start_and_verify(config)
        return target

    def _require_linux_root(self) -> None:
        if os.name != "posix" or not Path("/proc").exists():
            raise ProvisionError("服务端安装只能在 Linux 上运行")
        if os.geteuid() != 0:
            raise ProvisionError("服务端安装需要 root 权限")
        for command in ("systemctl", "useradd", "groupadd", "ip", "sysctl", "ufw"):
            if not self.runner.available(command):
                raise ProvisionError(f"系统缺少必要命令：{command}")

    def _run_checked(self, args: tuple[str, ...], *, timeout: int = 30) -> None:
        result = self.runner.run(args, timeout=timeout)
        if not result.ok:
            detail = result.stderr or result.stdout or f"退出码 {result.returncode}"
            raise ProvisionError(f"命令失败：{' '.join(args)}：{detail}")

    def _create_accounts(self) -> None:
        if grp is None or pwd is None:
            raise ProvisionError("Linux 账号管理模块不可用")
        try:
            grp.getgrnam("scbl")
        except KeyError:
            self._run_checked(("groupadd", "--system", "scbl"))
        for user in ("scbl-game", "scbl-control", "scbl-update"):
            try:
                pwd.getpwnam(user)
            except KeyError:
                self._run_checked(
                    (
                        "useradd",
                        "--system",
                        "--gid",
                        "scbl",
                        "--no-create-home",
                        "--shell",
                        "/usr/sbin/nologin",
                        user,
                    )
                )

    def _create_directories(self, config: ServerConfig) -> None:
        if grp is None or pwd is None:
            raise ProvisionError("Linux 账号管理模块不可用")
        group = grp.getgrnam("scbl").gr_gid
        game = pwd.getpwnam("scbl-game").pw_uid
        control = pwd.getpwnam("scbl-control").pw_uid
        update = pwd.getpwnam("scbl-update").pw_uid
        definitions = (
            # Service-specific files remain private, while non-root services can
            # traverse the directory to files explicitly owned by them.
            (Path("/etc/scbl"), 0, 0, 0o711),
            (Path("/usr/local/lib/scbl"), 0, 0, 0o755),
            (Path(DEPLOYMENT_PATHS.releases), 0, 0, 0o755),
            (Path(DEPLOYMENT_PATHS.component_store), 0, 0, 0o755),
            (Path(DEPLOYMENT_PATHS.cache), 0, group, 0o750),
            (Path(DEPLOYMENT_PATHS.backups), 0, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data), 0, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "manager", 0, 0, 0o700),
            (Path(DEPLOYMENT_PATHS.data) / "dedicated", game, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "dedicated" / "data", game, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "runtime", control, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "client-updates", update, group, 0o750),
            (Path(DEPLOYMENT_PATHS.data) / "client-bundle-cache", update, group, 0o750),
        )
        for path, user_id, group_id, mode in definitions:
            path.mkdir(parents=True, exist_ok=True)
            os.chown(path, user_id, group_id)
            os.chmod(path, mode)

    def _install_runtime_files(self, config: ServerConfig, target: Path) -> None:
        if grp is None or pwd is None:
            raise ProvisionError("Linux 账号管理模块不可用")
        group = grp.getgrnam("scbl").gr_gid
        game = pwd.getpwnam("scbl-game").pw_uid
        config_root = self.layout.config_root
        ticket_path = self.layout.data / "manager" / "dedicated-ticket.key"
        if ticket_path.exists():
            ticket_key = ticket_path.read_bytes()
            if len(ticket_key) != 32:
                raise ProvisionError(f"Dedicated ticket key 长度错误：{ticket_path}")
        else:
            ticket_key = os.urandom(32)
            _atomic_write_bytes(ticket_path, ticket_key, 0o600)
        control_auth_policy = self._control_auth_policy(config_root / "runtime.env")
        auth_token_key = self._control_auth_key()

        atomic_write_text(
            config_root / "runtime.env",
            render_runtime_env(
                config,
                version=__version__,
                auth_token_key=auth_token_key,
                control_auth_policy=control_auth_policy,
            ),
            0o600,
        )
        atomic_write_text(config_root / "easytier.toml", render_easytier_config(config), 0o600)
        dedicated_config = config_root / "dedicated.toml"
        atomic_write_text(
            dedicated_config,
            render_dedicated_config(config, ticket_key=ticket_key),
            0o400,
        )
        os.chown(dedicated_config, game, 0)

        balancing_source = target / "data" / "mp_balancing.ini"
        balancing_target = self.layout.data / "dedicated" / "data" / "mp_balancing.ini"
        _atomic_write_bytes(balancing_target, balancing_source.read_bytes(), 0o640)
        os.chown(balancing_target, game, group)

        client_updates = self.layout.data / "client-updates"
        manifest = client_updates / "client_update_manifest.json"
        if not manifest.exists():
            atomic_write_text(
                manifest,
                '{"version":"0.0.0","updateMode":"components","components":[]}\n',
                0o640,
            )
        descriptor = refresh_server_descriptor(
            client_updates, config=config, required=True
        )
        update_user = pwd.getpwnam("scbl-update").pw_uid
        os.chown(manifest, update_user, group)
        if descriptor is not None:
            os.chown(descriptor, update_user, group)

        atomic_write_text(self.layout.wait_script, WAIT_SCBL0, 0o755)

    def _control_auth_key(self) -> bytes:
        path = self.layout.data / "manager" / "control-auth.key"
        try:
            return load_or_create_control_auth_key(path)
        except ValueError as exc:
            raise ProvisionError(str(exc)) from exc

    @staticmethod
    def _control_auth_policy(runtime_env_path: Path) -> str:
        try:
            return load_control_auth_policy(runtime_env_path)
        except ValueError as exc:
            raise ProvisionError(str(exc)) from exc

    def _write_systemd_units(
        self, config: ServerConfig, runtime_manifest: RuntimeManifest
    ) -> None:
        for name, content in render_systemd_units(config, runtime_manifest).items():
            dropin = self.layout.unit_root / f"{name}.d"
            if dropin.exists() and any(dropin.glob("*.conf")):
                raise ProvisionError(f"发现未知 systemd drop-in，请先处理：{dropin}")
            atomic_write_text(self.layout.unit_root / name, content, 0o644)
        self._run_checked(("systemctl", "daemon-reload"))

    def _configure_firewall(self, config: ServerConfig) -> None:
        for command in ufw_commands(config):
            self._run_checked(command)

    def _configure_ipv4_forwarding(self) -> None:
        """Enable only the kernel forwarding setting owned by SCBL."""

        target = self.layout.ipv4_forwarding_file
        atomic_write_text(
            target,
            "# Managed by SCBL. Required for routing overlay client traffic.\n"
            "net.ipv4.ip_forward = 1\n",
            0o644,
        )
        self._run_checked(("sysctl", "--load", str(target)))
        observed = self.runner.run(("sysctl", "-n", "net.ipv4.ip_forward"), timeout=10)
        if not observed.ok or observed.stdout.strip() != "1":
            raise ProvisionError("IPv4 转发未能收敛为 net.ipv4.ip_forward=1")

    def _start_and_verify(
        self,
        config: ServerConfig,
        *,
        restart_units: set[str] | None = None,
    ) -> None:
        units = tuple(service.unit for service in SERVICES if service.required)
        self._run_checked(("systemctl", "enable", *units))
        try:
            if restart_units is None or "scbl-tunnel.service" in restart_units:
                restart_runtime_stack(self.runner)
            else:
                known = set(units)
                unknown = sorted(restart_units - known)
                if unknown:
                    raise ProvisionError(
                        "候选组件声明了未知服务：" + ", ".join(unknown)
                    )
                for unit in (
                    "scbl-update.service",
                    "scbl-dedicated.service",
                    "scbl-control-plane.service",
                ):
                    if unit in restart_units:
                        self._run_checked(("systemctl", "restart", unit), timeout=45)
        except RuntimeError as exc:
            raise ProvisionError(str(exc)) from exc
        self._assert_required_services_active()

    def _assert_required_services_active(self) -> None:
        previous_restarts: dict[str, int] | None = None
        for observation in range(2):
            failed = []
            restarts: dict[str, int] = {}
            for service in SERVICES:
                if not service.required:
                    continue
                status = self.systemd.status(service)
                if (
                    status.active != "active"
                    or status.sub != "running"
                    or status.exit_status != "0"
                    or status.result != "success"
                    or status.restarts is None
                ):
                    failed.append(
                        f"{service.unit}={status.active}/{status.sub}/"
                        f"result={status.result}/exit={status.exit_status}/"
                        f"restarts={status.restarts}"
                    )
                    continue
                restarts[service.unit] = status.restarts
            if failed:
                raise ProvisionError("安装后健康检查失败：" + ", ".join(failed))
            if previous_restarts is not None and restarts != previous_restarts:
                raise ProvisionError("安装后健康检查失败：服务 NRestarts 仍在增长")
            previous_restarts = restarts
            if observation == 0:
                self.sleep(1)

    def _snapshot_transaction(self) -> RuntimeTransactionSnapshot:
        previous_runtime = None
        if self.layout.current.is_symlink():
            previous_runtime = self.layout.current.resolve(strict=True)
        elif self.layout.current.exists():
            raise ProvisionError(f"current 必须是符号链接：{self.layout.current}")
        units: dict[str, ManagedFileSnapshot] = {}
        for name in RUNTIME_UNIT_NAMES:
            path = self.layout.unit_root / name
            if path.is_symlink():
                raise ProvisionError(f"systemd unit 不能是符号链接：{path}")
            if path.exists() and not path.is_file():
                raise ProvisionError(f"systemd unit 不是普通文件：{path}")
            if path.is_file():
                units[name] = ManagedFileSnapshot(
                    path.read_bytes(), path.stat().st_mode & 0o777
                )
            else:
                units[name] = ManagedFileSnapshot(None, None)
        return RuntimeTransactionSnapshot(
            previous_runtime,
            units,
            self._read_migration_checkpoint(),
        )

    def _read_migration_checkpoint(self) -> dict[int, str]:
        database = self.layout.data / "dedicated" / "5th-echelon.db"
        if not database.exists():
            return {}
        if database.is_symlink() or not database.is_file():
            raise ProvisionError(f"Dedicated 数据库不是普通文件：{database}")
        try:
            connection = sqlite3.connect(f"file:{database}?mode=ro", uri=True)
            try:
                exists = connection.execute(
                    "SELECT 1 FROM sqlite_master WHERE type='table' "
                    "AND name='_sqlx_migrations'"
                ).fetchone()
                if exists is None:
                    return {}
                rows = connection.execute(
                    "SELECT version, lower(hex(checksum)) FROM _sqlx_migrations "
                    "WHERE success = 1 ORDER BY version"
                ).fetchall()
            finally:
                connection.close()
        except sqlite3.Error as exc:
            raise ProvisionError("无法读取 Dedicated migration checkpoint") from exc
        checkpoint: dict[int, str] = {}
        for version, digest in rows:
            if (
                type(version) is not int
                or not isinstance(digest, str)
                or not re.fullmatch(r"[0-9a-f]{96}", digest)
                or version in checkpoint
            ):
                raise ProvisionError("Dedicated migration checkpoint 结构无效")
            checkpoint[version] = digest
        return checkpoint

    def _recover_failed_install(
        self,
        snapshot: RuntimeTransactionSnapshot,
        target: Path,
        target_manifest: RuntimeManifest,
    ) -> str:
        after = self._read_migration_checkpoint()
        advanced = _advanced_migrations(snapshot.migrations, after)
        if advanced and not target_manifest.supports_database_checkpoint(after):
            raise ProvisionError(
                "数据库 migration 已前进，但候选 Dedicated 不兼容当前 checkpoint"
            )

        if snapshot.previous_runtime is None:
            if advanced:
                raise ProvisionError(
                    "首次安装已提交 forward-only migration，不能自动移除候选 Runtime"
                )
            self.layout.current.unlink(missing_ok=True)
            self._restore_units(snapshot.units)
            self._run_checked(("systemctl", "daemon-reload"))
            for unit in RUNTIME_UNIT_NAMES:
                self._run_checked(("systemctl", "disable", "--now", unit), timeout=45)
            return "首次安装前停用态"

        activate_release(snapshot.previous_runtime, self.layout.current)
        dedicated_override = target / "dedicated_server" if advanced else None
        self._restore_units(snapshot.units, dedicated_override=dedicated_override)
        self._run_checked(("systemctl", "daemon-reload"))
        try:
            restart_runtime_stack(self.runner)
        except RuntimeError as exc:
            raise ProvisionError(f"恢复后的运行栈重启失败：{exc}") from exc
        self._assert_required_services_active()
        return "旧 Runtime 完整态" if not advanced else "旧 Runtime + 新 Dedicated 兼容拆分态"

    def _restore_units(
        self,
        snapshots: dict[str, ManagedFileSnapshot],
        *,
        dedicated_override: Path | None = None,
    ) -> None:
        if set(snapshots) != set(RUNTIME_UNIT_NAMES):
            raise ProvisionError("Runtime unit 快照集合不完整")
        for name in RUNTIME_UNIT_NAMES:
            snapshot = snapshots[name]
            path = self.layout.unit_root / name
            if snapshot.content is None:
                path.unlink(missing_ok=True)
                continue
            content = snapshot.content
            if name == "scbl-dedicated.service" and dedicated_override is not None:
                content = _override_dedicated_binary(content, dedicated_override)
            _atomic_write_bytes(path, content, snapshot.mode or 0o644)


def _advanced_migrations(
    before: dict[int, str], after: dict[int, str]
) -> dict[int, str]:
    for version, digest in before.items():
        if after.get(version) != digest:
            raise ProvisionError(
                f"Dedicated migration checkpoint 被删除或改写：{version}"
            )
    return {version: digest for version, digest in after.items() if version not in before}


def _override_dedicated_binary(content: bytes, binary: Path) -> bytes:
    if (
        not binary.is_absolute()
        or binary.name != "dedicated_server"
        or binary.is_symlink()
        or not binary.is_file()
        or any(char.isspace() for char in os.fspath(binary))
    ):
        raise ProvisionError("Dedicated 兼容二进制路径无效")
    try:
        text = content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise ProvisionError("Dedicated unit 不是有效 UTF-8") from exc
    pattern = re.compile(
        r"^ExecStart=(?P<binary>\S*/dedicated_server)"
        r"(?P<arguments>\s+--config\s+\S+[^\r\n]*)$",
        re.MULTILINE,
    )
    matches = list(pattern.finditer(text))
    if len(matches) != 1:
        raise ProvisionError("Dedicated unit 必须且只能包含一个受支持的 ExecStart")
    match = matches[0]
    replacement = f"ExecStart={binary}{match.group('arguments')}"
    return (text[: match.start()] + replacement + text[match.end() :]).encode("utf-8")
