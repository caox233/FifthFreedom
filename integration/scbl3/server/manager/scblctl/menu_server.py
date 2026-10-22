from __future__ import annotations

import sys
from pathlib import Path

from .config import (
    ConfigError,
    ServerConfig,
    dump_toml,
    impact_for,
    load_config,
    save_config,
)
from .diagnostics import run_doctor
from .paths import RuntimePaths
from .services import SystemdManager
from .menu_text import (
    announcement_editor_text as _announcement_editor_text,
    configure_terminal_input as _configure_terminal_input,
    decode_announcement_editor as _decode_announcement_editor,
    edit_announcement_with_nano as _edit_announcement_with_nano,
    parse_announcement_editor as _parse_announcement_editor,
    read_operator_text as _read_operator_text,
)
from .menu_files import select_server_file as _select_server_file
from .menu_client import client_publish_menu as _client_publish_menu


def run_menu(paths: RuntimePaths) -> int:
    _configure_terminal_input()
    while True:
        print("\nSCBL 3.0 服务端管理")
        print("  1. 服务状态")
        print("  2. 服务端管理")
        print("  3. 客户端管理")
        print("  4. 服务器设置")
        print("  5. 外部组件（EasyTier / DDNS）")
        print("  6. 数据与备份")
        print("  7. 日志与诊断")
        print("  8. 账号管理")
        print("  9. 服务控制")
        print("  0. 退出")
        choice = input("请选择：").strip()
        if choice == "0":
            return 0
        if choice == "1":
            _show_status()
        elif choice == "2":
            _deployment_menu(paths)
        elif choice == "3":
            _client_publish_menu(paths)
        elif choice == "4":
            _server_settings_menu(paths)
        elif choice == "5":
            _external_components_menu(paths)
        elif choice == "6":
            _backup_menu(paths)
        elif choice == "7":
            _show_doctor(paths)
        elif choice == "8":
            _account_menu()
        elif choice == "9":
            _service_menu(paths)
        else:
            print("无效选项。")


def _show_status() -> None:
    for item in SystemdManager().all_statuses():
        marker = "✓" if item.active == "active" else "!"
        print(f" {marker} {item.component}: {item.active}/{item.sub}")


def _show_doctor(paths: RuntimePaths) -> None:
    symbols = {"pass": "✓", "warn": "!", "fail": "✗"}
    for check in run_doctor(paths):
        print(f" {symbols[check.level]} {check.name}: {check.message}")


def _external_components_menu(paths: RuntimePaths) -> None:
    while True:
        print("\n外部组件")
        print("  1. EasyTier")
        print("  2. 动态域名（DDNS-Go）")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice == "1":
            _easytier_menu(paths)
        elif choice == "2":
            _ddns_menu(paths)
        else:
            print("无效选项。")


def _easytier_menu(paths: RuntimePaths) -> None:
    from .external_easytier import EasyTierInstallError, EasyTierManager
    from .live_state import LiveStateError, read_live_state

    manager = EasyTierManager()
    while True:
        current = manager.verified_current()
        print("\nEasyTier 外部组件")
        print(f"当前版本：{current.version if current else '未安装或校验失败'}")
        print("  1. 首次安装 / 修复（有效时不联网、不重复下载）")
        print("  2. 检查并更新到官方最新版")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        try:
            if choice == "1":
                result = manager.ensure_installed()
                print(f"EasyTier {result.version} {'已复用' if result.reused else '已安装'}。")
            elif choice == "2":
                config = load_config(paths.config)
                try:
                    live = read_live_state(config)
                except LiveStateError as exc:
                    print(f"无法确认在线人数，已停止：{exc}")
                    continue
                if live.online_count:
                    print(f"当前有 {live.online_count} 名玩家在线，已停止更新。")
                    continue
                result = manager.install_latest()
                if result.previous is not None and result.previous != result.path:
                    try:
                        SystemdManager().restart("tunnel")
                    except Exception:
                        manager.activate(result.previous)
                        SystemdManager().restart("tunnel")
                        raise
                print(f"EasyTier 当前版本：{result.version}。")
            else:
                print("无效选项。")
        except (EasyTierInstallError, OSError, RuntimeError, ValueError) as exc:
            print(f"EasyTier 错误：{exc}")


def _account_menu() -> None:
    from .accounts import AccountRecoveryError, AccountRecoveryManager

    manager = AccountRecoveryManager()
    while True:
        print("\n账号管理")
        print("  1. 重新签发恢复码")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice != "1":
            print("无效选项。")
            continue
        username = input("账号：").strip()
        if input(f"确认让账号 {username} 的旧恢复码立即失效 [y/N]：").strip().lower() not in {"y", "yes"}:
            print("已取消。")
            continue
        try:
            code = manager.issue(username)
            print("新恢复码（仅显示这一次，请安全交给玩家）：")
            print(f"  {code}")
        except AccountRecoveryError as exc:
            print(f"账号操作错误：{exc}")


def _server_settings_menu(paths: RuntimePaths) -> None:
    while True:
        try:
            config = load_config(paths.config)
        except ConfigError as exc:
            print(f"错误：{exc}")
            return
        print("\n服务器设置")
        print(f"当前身份：{'测试服务器' if config.server.mode == 'test' else '正式服务器'}")
        print(f"游戏端口：{config.network.public_port}（TCP/WSS + UDP/QUIC）")
        print(f"更新端口：{config.services.update_port}")
        print(f"客户端游戏域名：{config.server.public_host or '由客户端现有配置提供'}")
        print(f"公网下载基址：{config.services.public_update_base_url or '未设置'}")
        print("  1. 查看完整配置")
        print("  2. 校验配置")
        print("  3. 切换服务器身份（正式 / 测试）")
        print("  4. 设置客户端游戏域名")
        print("  5. 设置公网下载基址")
        print("  6. 设置本地端口")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice == "1":
            print(dump_toml(config, redact=True), end="")
        elif choice == "2":
            errors = config.validate()
            if errors:
                print("配置无效：\n- " + "\n- ".join(errors))
            else:
                print("配置有效。")
        elif choice == "3":
            target = "test" if config.server.mode == "stable" else "stable"
            label = "测试服务器" if target == "test" else "正式服务器"
            if input(f"确认切换为{label} [y/N]：").strip().lower() not in {
                "y",
                "yes",
            }:
                continue
            _save_menu_settings(paths, config, {"server.mode": target})
        elif choice == "4":
            value = input(
                "客户端连接游戏使用的域名；留空表示继续由客户端配置提供："
            ).strip()
            _save_menu_settings(paths, config, {"server.public_host": value})
        elif choice == "5":
            value = input(
                "公网下载基址，例如 http://example.com:18080；留空清除："
            ).strip()
            _save_menu_settings(paths, config, {"services.public_update_base_url": value})
        elif choice == "6":
            game_port = input(f"游戏端口 [{config.network.public_port}]：").strip()
            update_port = input(f"更新端口 [{config.services.update_port}]：").strip()
            control_port = input(f"控制端口 [{config.services.control_port}]：").strip()
            values = {
                "network.public_port": game_port or str(config.network.public_port),
                "services.update_port": update_port or str(config.services.update_port),
                "services.control_port": control_port or str(config.services.control_port),
            }
            _save_menu_settings(paths, config, values)
        else:
            print("无效选项。")


def _save_menu_settings(
    paths: RuntimePaths,
    config: ServerConfig,
    values: dict[str, str],
) -> None:
    for key, value in values.items():
        config.set(key, value)
    errors = config.validate()
    if errors:
        print("设置未保存：\n- " + "\n- ".join(errors))
        return
    save_config(config, paths.config)
    impacted = impact_for(set(values))
    print("设置已原子保存；影响：" + "、".join(impacted))
    print("需要立即生效时，返回主菜单选择“服务控制 -> 应用当前配置”。")


def _service_menu(paths: RuntimePaths) -> None:
    components = {
        "3": "tunnel",
        "4": "dedicated",
        "5": "control",
        "6": "update",
    }
    while True:
        print("\n服务控制")
        print("  1. 查看服务状态")
        print("  2. 应用当前配置（会重启服务）")
        print("  3. 重启 EasyTier")
        print("  4. 重启 Dedicated")
        print("  5. 重启 Control Plane")
        print("  6. 重启 Update Server")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice == "1":
            _show_status()
            continue
        if choice == "2":
            _apply_current_config(paths)
            continue
        component = components.get(choice)
        if component is None:
            print("无效选项。")
            continue
        if input(f"确认重启 {component} [y/N]：").strip().lower() not in {
            "y",
            "yes",
        }:
            continue
        try:
            SystemdManager().restart(component)
            print(f"{component} 已重启。")
        except (OSError, RuntimeError, ValueError) as exc:
            print(f"服务操作失败：{exc}")


def _apply_current_config(paths: RuntimePaths) -> None:
    from .live_state import LiveStateError, read_live_state
    from .provision import ProvisionError, Provisioner

    try:
        config = load_config(paths.config)
        live = read_live_state(config)
    except (ConfigError, LiveStateError) as exc:
        print(f"无法确认在线状态，已停止：{exc}")
        return
    if live.online_count:
        print(f"当前有 {live.online_count} 名玩家在线，已停止应用配置。")
        return
    if input("确认应用配置并重启受管服务；请输入 APPLY：").strip() != "APPLY":
        print("已取消。")
        return
    try:
        target = Provisioner().repair(config)
        print(f"配置已应用，当前运行目录：{target}")
    except (OSError, ProvisionError, RuntimeError, ValueError) as exc:
        print(f"应用配置失败：{exc}")


def _deployment_menu(paths: RuntimePaths) -> None:
    from pathlib import Path

    from .client_publish import ClientPublishError
    from .deployment import (
        DeploymentError,
        DeploymentManager,
        RUNTIME_COMPONENTS,
        download_online_package,
        installed_versions,
        online_package_url,
    )

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    while True:
        print("\n服务端部署")
        print("  1. 更新变化组件")
        print("  2. 使用完整包修复")
        print("  3. 查看已安装组件")
        print("  4. 查看发布历史")
        print("  5. 回滚到上一运行版本")
        print("  0. 返回")
        print(f"服务器模式：{config.server.mode}；在线源：{config.updates.repository}")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice in {"1", "2"}:
                import tempfile

                operation = {"1": "update", "2": "repair"}[choice]
                kind = "patch" if operation == "update" else "full"
                package = None
                temporary = tempfile.TemporaryDirectory(
                    prefix=f"{operation}-", dir="/var/cache/scbl"
                )
                try:
                    root = Path(temporary.name)
                    print("  1. 在线下载")
                    print("  2. 使用服务端已有文件路径")
                    source = input("请选择来源：").strip()
                    if source == "1":
                        print("下载地址：" + online_package_url(config, kind=kind))
                        package = download_online_package(
                            config, kind=kind, destination=root / "online.zip"
                        )
                    elif source == "2":
                        package = _select_server_file(
                            "服务器已有部署包：",
                            suffixes=(".zip",),
                            prefixes=("SCBL-Server-",),
                        )
                        if package is None:
                            continue
                    else:
                        print("无效来源。")
                        continue
                    manager = DeploymentManager(config, paths)
                    verified = manager.verify(package, expected_kind=kind)
                    print(
                        f"包校验通过：{verified.package_type} v{verified.version}，"
                        f"组件={', '.join(item.component + '@' + item.version for item in verified.artifacts)}"
                    )
                    allow_online = False
                    changes_runtime = any(
                        item.component in RUNTIME_COMPONENTS for item in verified.artifacts
                    )
                    if changes_runtime:
                        proceed, allow_online = _confirm_online_override(
                            config, action="安装此更新"
                        )
                        if not proceed:
                            continue
                    answer = input(
                        f"确认执行{ {'repair':'修复','update':'更新'}[operation] } [y/N]："
                    ).strip().lower()
                    if answer not in {"y", "yes"}:
                        print("已取消。")
                        continue
                    result = manager.apply(
                        package, operation=operation, allow_online=allow_online
                    )
                    print("已应用组件：" + ", ".join(result.applied))
                    if result.backup:
                        print(f"操作前备份：{result.backup}")
                finally:
                    temporary.cleanup()
            elif choice == "3":
                versions = installed_versions()
                for component in sorted(versions):
                    print(f"{component} = {versions[component]}")
                print(f"ddns = {'启用' if config.ddns.enabled else '禁用'}")
            elif choice == "4":
                _show_release_history()
            elif choice == "5":
                if input("确认回滚到 previous 版本 [y/N]：").strip().lower() not in {"y", "yes"}:
                    continue
                proceed, allow_online = _confirm_online_override(
                    config, action="回滚服务端"
                )
                if not proceed:
                    continue
                result = DeploymentManager(config, paths).rollback(
                    allow_online=allow_online
                )
                print(f"已回滚到：{result.previous}")
            else:
                print("无效选项。")
        except (
            DeploymentError,
            ClientPublishError,
            OSError,
            ValueError,
        ) as exc:
            print(f"部署错误：{exc}")


def _confirm_online_override(
    config: ServerConfig, *, action: str
) -> tuple[bool, bool]:
    """Return ``(proceed, allow_online)`` for a disruptive server operation."""

    from .live_state import LiveStateError, read_live_state

    try:
        live = read_live_state(config)
    except LiveStateError as exc:
        print(f"警告：{exc}")
        forced = input(
            f"无法确认在线状态；强制{action}可能中断玩家。请输入 FORCE 继续："
        ).strip() == "FORCE"
    else:
        if live.online_count == 0:
            return True, False
        print(
            f"警告：当前有 {live.online_count} 名玩家在线；"
            f"强制{action}会中断他们的游戏。"
        )
        forced = input("请输入 FORCE 强制继续，其他输入取消：").strip() == "FORCE"
    if not forced:
        print("已取消；请等待玩家离线后再操作。")
        return False, False
    return True, True


def _ddns_menu(paths: RuntimePaths) -> None:
    import getpass

    from .ddns import DdnsError, DdnsManager

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    manager = DdnsManager(config.ddns)
    while True:
        print("\nIPv6 动态域名（脚本管理，无 Web 设置页）")
        print("  1. 安装 / 更新 DDNS-Go")
        print("  2. 配置阿里云 DNS（A/AAAA）")
        print("  3. 启用并立即更新")
        print("  4. 查看同步状态")
        print("  5. 立即重新检测")
        print("  6. 查看最近日志")
        print("  7. 停止并禁用")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                print(f"已安装 {manager.install()}（官方 SHA256 校验通过）。")
            elif choice == "2":
                default_domain = config.server.public_host if "." in config.server.public_host else ""
                prompt = f"AAAA 域名 [{default_domain}]：" if default_domain else "AAAA 域名："
                domain = input(prompt).strip() or default_domain
                detected = manager.detect_interface()
                interface = input(f"IPv6 网卡 [{detected}]：").strip() or detected
                access_key_id = input("阿里云 AccessKey ID：").strip()
                secret = getpass.getpass("阿里云 AccessKey Secret（输入不回显）：").strip()
                ipv4_answer = input("是否同时更新 IPv4 A 记录 [y/N]：").strip().lower()
                address = manager.configure_alidns(
                    domain=domain,
                    access_key_id=access_key_id,
                    access_key_secret=secret,
                    interface=interface,
                    enable_ipv4=ipv4_answer in {"y", "yes"},
                )
                print(f"配置已保存；当前公网 IPv6：{address}")
            elif choice == "3":
                manager.enable()
                config.ddns.enabled = True
                save_config(config, paths.config)
                print("DDNS 已启用并立即执行。")
            elif choice == "4":
                status = manager.status()
                print(f"服务：{status.active}/{status.enabled}")
                print(f"域名：{status.domain or '-'}")
                print(f"网卡公网 IPv6：{status.local_ipv6 or '-'}")
                print(f"DNS AAAA：{', '.join(status.dns_ipv6) or '-'}")
                print(f"记录模式：{'A + AAAA' if status.ipv4_enabled else '仅 AAAA'}")
                print(f"同步：{'一致' if status.synchronized else '尚未一致'}")
            elif choice == "5":
                manager.restart()
                print("已重新检测并执行更新。")
            elif choice == "6":
                print(manager.recent_log() or "（无日志）")
            elif choice == "7":
                manager.stop()
                config.ddns.enabled = False
                save_config(config, paths.config)
                print("已停止；配置和凭据仍保留。")
            else:
                print("无效选项。")
        except (DdnsError, OSError) as exc:
            print(f"DDNS 错误：{exc}")


def _show_release_history() -> None:
    from pathlib import Path

    runtime = Path("/opt/scbl/releases")
    print("服务端运行时：")
    for path in sorted(runtime.glob("*"), reverse=True):
        if path.is_dir():
            print(f"  {path.name}")


def _backup_menu(paths: RuntimePaths) -> None:
    from .backup import BackupError, BackupManager

    manager = BackupManager(paths)
    while True:
        print("\n数据与备份")
        print("  1. 创建核心数据备份")
        print("  2. 创建完整备份（包含客户端发布包）")
        print("  3. 列出备份")
        print("  4. 清理旧备份（保留最新 3 份）")
        print("  5. 清理旧 Runtime 与无引用组件缓存")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice in {"1", "2"}:
                info = manager.create(include_client_packages=choice == "2")
                print(f"备份完成：{info.path}（{info.size / 1024 / 1024:.1f} MiB）")
            elif choice == "3":
                items = manager.list()
                if not items:
                    print("暂无备份。")
                for item in items:
                    print(f"{item.modified_at:%Y-%m-%d %H:%M:%SZ}  {item.size / 1024 / 1024:.1f} MiB  {item.path}")
            elif choice == "4":
                answer = input("确认删除第 4 份及更早的备份 [y/N]：").strip().lower()
                if answer in {"y", "yes"}:
                    print(f"已删除 {len(manager.prune())} 份旧备份。")
            elif choice == "5":
                from .maintenance import prune_obsolete_runtime_storage

                preview = prune_obsolete_runtime_storage(apply=False)
                print(
                    f"可清理 release {len(preview.releases)} 个、组件缓存 "
                    f"{len(preview.components)} 个，共 {preview.bytes_removed / 1024 / 1024:.1f} MiB。"
                )
                if input("确认清理 [y/N]：").strip().lower() in {"y", "yes"}:
                    prune_obsolete_runtime_storage(apply=True)
                    print("清理完成；当前、直接回滚和最近第三个版本均保留。")
            else:
                print("无效选项。")
        except (BackupError, OSError, ValueError) as exc:
            print(f"备份错误：{exc}")
