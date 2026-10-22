from __future__ import annotations

import tempfile
from pathlib import Path

from .config import ConfigError, load_config
from .menu_files import select_server_file
from .menu_text import edit_announcement_with_nano, read_operator_text
from .paths import RuntimePaths


def client_publish_menu(paths: RuntimePaths) -> None:
    from .client_publish import ClientPublishError, ClientPublisher

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    publisher = ClientPublisher(channel=config.server.mode)
    full_package_url = config.services.public_update_base_url.rstrip("/") + "/SCBL.zip"
    while True:
        current = publisher.current_version() or "未发布"
        channel_label = "测试" if config.server.mode == "test" else "正式"
        print("\n客户端内容")
        print(f"当前服务器只维护客户端{channel_label}通道，版本：{current}")
        print("  1. 发布完整客户端 ZIP")
        print("  2. 从当前组件封装并发布完整客户端")
        print("  3. 发布组件更新")
        print("  4. 公告管理")
        print("  5. 查看客户端版本")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                _publish_full_package(
                    config.updates.repository,
                    publisher,
                    full_package_url,
                )
            elif choice == "2":
                _assemble_and_publish_current(
                    publisher,
                    config.server.mode,
                    full_package_url,
                )
            elif choice == "3":
                client_component_menu(
                    config.updates.repository,
                    config.server.mode,
                    full_package_url,
                )
            elif choice == "4":
                announcement_menu()
            elif choice == "5":
                show_client_history(publisher)
            else:
                print("无效选项。")
        except (ClientPublishError, OSError, ValueError) as exc:
            print(f"客户端发布错误：{exc}")


def _publish_full_package(
    repository: str,
    publisher: object,
    full_package_url: str,
) -> None:
    from .client_publish import download_online_client

    cache = Path("/var/cache/scbl")
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="client-", dir=cache) as temporary:
        root = Path(temporary)
        print("  1. 从 GitHub 下载")
        print("  2. 使用服务器已有文件")
        source = input("请选择来源：").strip()
        if source == "1":
            package = download_online_client(repository, root)
        elif source == "2":
            package = select_server_file(
                "服务器已有客户端完整包：",
                suffixes=(".zip",),
                prefixes=("SCBL-Client-",),
            )
            if package is None:
                return
        else:
            print("无效来源。")
            return
        verified = publisher.verify(package)
        print(
            f"校验通过：客户端 v{verified.version}，"
            f"{verified.size / 1024 / 1024:.1f} MiB，SHA256={verified.sha256}"
        )
        same = verified.version == publisher.current_version()
        if same:
            print("该版本已存在；继续会用完整包执行修复覆盖。")
        if input("确认发布 [y/N]：").strip().lower() not in {"y", "yes"}:
            print("已取消。")
            return
        note = read_operator_text("中文更新说明（直接回车使用默认值）：")
        note_en = input("English release note (Enter for default): ").strip()
        published = publisher.publish(
            package,
            release_notes=[note] if note else None,
            release_notes_en=[note_en] if note_en else None,
        )
        print(f"客户端 v{published.version} 已发布：{published.archive}")
        print(f"完整包下载：{full_package_url}")


def client_component_menu(
    repository: str,
    channel: str,
    full_package_url: str,
) -> None:
    from .client_components import (
        COMPONENTS,
        ClientComponentError,
        ClientComponentPublisher,
        download_online_component,
    )
    from .client_publish import ClientPublishError, ClientPublisher

    publisher = ClientComponentPublisher()
    label = "测试" if channel == "test" else "正式"
    while True:
        print(f"\n客户端组件（{label}通道）")
        print("  1. 发布组件补丁包")
        print("  2. 发布单个组件")
        print("  3. 查看当前组件版本")
        print("  4. 从当前组件封装并发布完整客户端")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                _publish_component_patch(publisher, channel, full_package_url)
            elif choice == "2":
                _publish_single_component(
                    publisher,
                    repository,
                    channel,
                    COMPONENTS,
                    download_online_component,
                )
            elif choice == "3":
                status = publisher.status()[channel]
                if not status:
                    print("（无）")
                for name, entry in sorted(status.items()):
                    print(f"  {name} = {entry['version']}  {entry['sha256']}")
            elif choice == "4":
                _assemble_and_publish_current(
                    ClientPublisher(root=publisher.root, channel=channel),
                    channel,
                    full_package_url,
                )
            else:
                print("无效选项。")
        except (ClientComponentError, ClientPublishError, OSError, ValueError) as exc:
            print(f"组件发布错误：{exc}")


def _publish_component_patch(
    publisher: object,
    channel: str,
    full_package_url: str,
) -> None:
    patch_file = select_server_file(
        "服务器已有客户端补丁包：",
        suffixes=(".scblpatch",),
        prefixes=("SCBL-Client-",),
    )
    if patch_file is None:
        return
    verified = publisher.verify_patch(patch_file)
    summary = ", ".join(
        f"{item.component}@{item.version}" for item in verified.artifacts
    )
    print(f"补丁校验通过：{summary}，SHA256={verified.sha256}")
    if input("确认发布 [y/N]：").strip().lower() not in {"y", "yes"}:
        print("已取消。")
        return
    entries = publisher.publish_patch(patch_file, channel=channel)
    print(
        "组件补丁已发布："
        + ", ".join(f"{name}@{entry['version']}" for name, entry in entries.items())
    )
    if input("立即封装并发布最新完整客户端 [Y/n]：").strip().lower() not in {
        "n",
        "no",
    }:
        from .client_publish import ClientPublisher

        _assemble_and_publish_current(
            ClientPublisher(root=publisher.root, channel=channel),
            channel,
            full_package_url,
        )


def _publish_single_component(
    publisher: object,
    repository: str,
    channel: str,
    components: dict,
    download_online_component,
) -> None:
    print("可用组件：")
    for name, spec in components.items():
        print(f"  {name}: {spec.filename}")
    component = input("组件名称：").strip().lower()
    if component not in components:
        print("无效组件。")
        return
    cache = Path("/var/cache/scbl")
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="component-", dir=cache) as temporary:
        root = Path(temporary)
        print("  1. 从 GitHub 下载该组件")
        print("  2. 使用服务器已有文件")
        source_choice = input("请选择来源：").strip()
        if source_choice == "1":
            downloaded = download_online_component(
                repository, component, channel=channel, destination=root
            )
            version = downloaded.version
            source = downloaded.source
            print(
                f"GitHub 组件校验通过：{component}@{version}，"
                f"SHA256={downloaded.sha256}"
            )
        elif source_choice == "2":
            source = select_server_file(
                "服务器已有组件文件：",
                filenames=(components[component].filename,),
            )
            if source is None:
                return
            version = input("组件版本：").strip()
        else:
            print("无效来源。")
            return
        if input("确认发布到当前服务器通道 [y/N]：").strip().lower() not in {
            "y",
            "yes",
        }:
            print("已取消。")
            return
        entry = publisher.publish(component, version, source, channel=channel)
        print(
            f"组件已发布：{component}@{entry['version']}，SHA256={entry['sha256']}"
        )


def _assemble_and_publish_current(
    publisher: object,
    channel: str,
    full_package_url: str,
) -> None:
    from .client_package_assembler import (
        ClientPackageAssembler,
        ClientPackageAssemblyError,
    )
    from .client_publish import ClientPublishError

    cache = Path("/var/cache/scbl")
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="client-full-", dir=cache) as temporary:
        try:
            package = ClientPackageAssembler(publisher.root, channel=channel).build(
                Path(temporary)
            )
        except ClientPackageAssemblyError as exc:
            raise ClientPublishError(str(exc)) from exc
        published = publisher.publish(package.archive)
        print(
            f"完整客户端 v{published.version} 已发布；固定下载文件："
            f"{publisher.root / 'SCBL.zip'}"
        )
        print(f"完整包下载：{full_package_url}")


def show_client_history(publisher: object) -> None:
    print(f"当前版本：{publisher.current_version() or '未发布'}")
    releases = publisher.root / "releases"
    versions = [
        path.name for path in sorted(releases.glob("*"), reverse=True) if path.is_dir()
    ]
    if not versions:
        print("暂无客户端发布记录。")
        return
    print("服务器保留版本：")
    for version in versions:
        print(f"  {version}")


def announcement_menu() -> None:
    from .announcements import AnnouncementError, AnnouncementManager

    manager = AnnouncementManager()
    labels = {"active": "滚动公告", "startup": "启动公告", "update": "更新公告"}
    while True:
        print("\n公告管理")
        for index, kind in enumerate(("active", "startup", "update"), start=1):
            status = manager.status(kind)
            marker = "启用" if status.enabled else "停用"
            suffix = f" / v{status.version}" if kind == "update" and status.version else ""
            print(f"  {index}. {labels[kind]}：{marker}{suffix} / {status.title or '无内容'}")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice not in {"1", "2", "3"}:
            print("无效选项。")
            continue
        kind = {"1": "active", "2": "startup", "3": "update"}[choice]
        while True:
            status = manager.status(kind)
            print(f"\n{labels[kind]}：{'启用' if status.enabled else '停用'}")
            print(f"标题：{status.title or '-'}")
            if kind == "update":
                print(f"对应客户端：{status.version or '-'}")
            print("  1. 设置内容并启用")
            print("  2. 启用 / 停用")
            print("  3. 清空")
            print("  0. 返回")
            action = input("请选择：").strip()
            try:
                if action == "0":
                    break
                if action == "1":
                    values = edit_announcement_with_nano(manager, kind, status)
                    manager.set(kind, **values)
                    print("公告已原子发布。")
                elif action == "2":
                    target = not status.enabled
                    manager.set_enabled(kind, target)
                    print("公告已启用。" if target else "公告已停用。")
                elif action == "3":
                    if input("确认清空 [y/N]：").strip().lower() in {"y", "yes"}:
                        manager.clear(kind)
                        print("公告已清空并停用。")
                else:
                    print("无效选项。")
            except (AnnouncementError, OSError, ValueError) as exc:
                print(f"公告错误：{exc}")
