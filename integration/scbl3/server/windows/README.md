# SCBL 原生 Windows 服务端

这是 SCBL 3.0 推荐的新服务端形态：Windows 11 Enterprise LTSC 2024 直接运行，不需要 Hyper-V、Ubuntu、WSL、systemd 或 Linux Manager。

## 架构

```text
SCBL.Server.exe（可视化管理）
  ├─ 首次安装 / 设置 / 状态 / 日志 / 备份 / ZIP 更新
  ├─ 注册并管理 SCBLServer Windows 服务
  └─ 调用独立 SCBL.Server.Update.exe 做停服、替换和回滚

SCBLServer Windows 服务（SCBL.Server.Host.exe）
  ├─ EasyTier（首次使用从官方 GitHub 下载，之后复用本地缓存）
  ├─ SCBL.Dedicated.exe
  ├─ 内置 Python 3.12 + Control Plane
  ├─ 内置 Python 3.12 + Update Server
  └─ 可选 DDNS-Go（启用后首次下载，之后复用）
```

SCBL 自有组件随普通 ZIP 分发；EasyTier、DDNS-Go 不塞进安装包。服务端首次启动会下载缺失的外部组件，并把 GitHub 官方 asset digest 与本地 SHA256 精确绑定；已安装版本和正确缓存不会在修复安装时重复下载。管理界面的“检查并更新外部组件”用于主动发现官方新版本。正式服与测试服使用相同程序，只在各自机器保存独立配置、端口和数据。

## 第一次安装

1. 在构建机取得 `release-output/windows-server/SCBL-Server-Windows-vX.Y.Z.zip`。
2. 把整个 ZIP 复制到目标 Windows 服务器并完整解压。
3. 右键或双击 `SCBL.Server.exe`；程序会请求 Windows 管理员权限。
4. 设置服务器 ID、`stable`/`test`、服务器地址以及游戏和更新端口；客户端下载地址由服务器地址和更新端口自动生成。切换到 `test` 时两个对外端口自动加 1，切回 `stable` 自动减 1。控制端口属于内部实现，不在普通设置页展示。需要动态域名时勾选 DDNS-Go，管理端口和配置页入口才会显示。
5. 点击“首次安装”，或者在状态页点击“首次安装并启动”。随包的 full ZIP 会被自动选择，不需要输入命令或 `INSTALL`。单独保存设置只写入配置，不代表已经安装服务。

安装器会：

- 原子安装 Bootstrap 与不可变 Runtime；
- 首次下载并缓存 EasyTier；
- 注册自动启动的 `SCBLServer` Windows 服务、服务 SID 和失败恢复策略；
- 添加游戏 TCP/WSS、游戏 UDP/QUIC、更新 TCP 和虚拟网段控制端口规则；
- EasyTier 虚拟网卡出现后启用其 IPv4 转发；
- 生成本机独立配置和随机运行密钥，并把程序、配置、数据库、备份和日志目录收敛到 SYSTEM、Administrators 与当前管理员 ACL。

路由器仍负责公网 NAT：游戏端口同时映射 TCP 和 UDP，更新端口映射 TCP。控制端口只允许 EasyTier 虚拟网段，不应公网映射。

DDNS-Go 管理页只监听本机 `127.0.0.1`，默认端口 `9876`，不添加公网防火墙规则。勾选后保存并启动服务，再点击“打开配置页”填写 DNS 服务商、域名和凭据；这些敏感配置由 DDNS-Go 自己保存，不写入 SCBL 服务端配置。

## 日常管理

以后只运行 `SCBL.Server.exe`：

- “状态”：查看 Host、EasyTier、Dedicated、Control Plane、Update Server；
- “设置”：切换 stable/test 或修改本机端口，保存后重启服务；
- “安装与维护”：选择更新 ZIP、检查外部组件、生成备份、打开数据目录；
- “卸载服务端”：删除 Windows 服务和防火墙规则，并选择保留数据或彻底删除配置、数据库、日志、缓存和备份；
- “日志”：查看统一运行日志。

更新助手会先备份，再等待服务真正停止、校验 ZIP、原子替换、恢复防火墙并等待服务真正运行。失败时恢复配置、两个 Runtime 指针、Bootstrap 和防火墙；Runtime、备份、外部组件各保留最近 3 个版本，仍被保留备份引用的 Runtime 不会提前清理。更新 ZIP 只需要携带实际变化的组件，并拒绝普通更新降级、零字节组件和目标路径冲突。

Host 因 EasyTier 虚拟网卡与转发操作仍运行在 Windows 服务特权边界；运行密钥按进程最小化传递，Update Server 不接收密钥，Dedicated 不接收 EasyTier/Control 密钥。未来若拆分独立低权限 Worker，必须先完成干净 LTSC 上的 EasyTier 驱动与进程监督实机，不得用未经验证的账户切换破坏联网。

## 目录

```text
%ProgramFiles%\SCBL Server\bootstrap\   Manager、Host、Updater
%ProgramData%\SCBL Server\config\      本机配置和运行密钥
%ProgramData%\SCBL Server\releases\    不可变 Runtime（保留 3 个）
%ProgramData%\SCBL Server\external\    EasyTier / DDNS-Go（各保留 3 个）
%ProgramData%\SCBL Server\cache\       可复用下载缓存
%ProgramData%\SCBL Server\data\        数据库和运行状态
%ProgramData%\SCBL Server\client\      客户端组件、公告与完整包
%ProgramData%\SCBL Server\backups\     备份（保留 3 个）
%ProgramData%\SCBL Server\logs\        统一日志
```

## Windows 构建

前置：Windows 10/11 x64、.NET 8 SDK、Visual Studio 2022 C++ Build Tools、Windows SDK。项目固定脚本会自动使用仓库旁的 Cargo/protoc 工具链和持久缓存。

```powershell
Set-Location D:\codex\SCBL3.0
.\server\windows\build-windows-server.ps1
```

脚本固定执行：Windows 事务测试、Dedicated `fmt/test/release`、三个 .NET 单文件正式发布、Python runtime 装配、清单大小/SHA 和确定性 ZIP 生成。不要为单次构建另写脚本。

只更新一个组件时仍使用同一入口，例如：

```powershell
.\server\windows\build-windows-server.ps1 -Component dedicated
.\server\windows\build-windows-server.ps1 -Component control-plane
.\server\windows\build-windows-server.ps1 -Component update-server
.\server\windows\build-windows-server.ps1 -Component bootstrap
```

`bootstrap` 会一起重编 Manager、Host 和 Updater。三者都嵌入共享 Core，Core 有变化时不得只更新其中一个，否则同一台服务器会运行不一致的基础逻辑。Python/资源组件只重新装配对应文件。输出为 `SCBL-Windows-Server-vX.Y.Z-<component>-update.zip`，在管理界面选择该 ZIP 即可。更新器会以当前 Runtime 为基线，只覆盖变化组件，未变化组件不会重编、重复下载或丢失。

输出：

```text
release-output/windows-server/SCBL-Server-Windows-vX.Y.Z.zip
release-output/windows-server/SCBL-Server-Windows-vX.Y.Z.zip.sha256
release-output/windows-server/SCBL-Windows-Server-full.zip
release-output/windows-server/SCBL-Windows-Server-full.zip.sha256
```

首次 Release 会建立 Rust 和 NuGet 缓存，耗时较长；后续输入未变时复用缓存。Python embeddable 下载使用固定版本和 SHA，已有正确缓存不会重复下载。EasyTier/DDNS-Go 属运行时外部组件，不参与编译。

## 当前验证边界

本目标已完成源码编译、33 项 Windows 事务/安全回归、Dedicated 152 项测试、内嵌 Python 语法/CLI、8 组件清单大小/SHA 与外层安装包布局验证。服务端数据固定使用 `%ProgramData%\SCBL Server`，不得与客户端 Route Guard 的 `%ProgramData%\SCBL` 共用。首次安装会显示外部组件下载进度，30 秒无数据自动续传重试；重复使用相同完整包会复用已安装 Runtime。Host 会从本机当前通道的客户端组件清单、完整包和公告生成唯一公开 `scbl-server.json`；依赖缺失时不伪造空配置，依赖漂移时拒绝覆盖上一份有效文档。Windows 管理器的“客户端发布”页只需选择一个普通 ZIP：完整包会自动拆出六组件，补丁会应用到当前客户端快照；两种输入都在同一事务中重新生成最新固定 `SCBL.zip`、组件清单和 `scbl-server.json`。仍需在干净 Windows LTSC 目标机完成一次真实管理员安装、服务开机自启、服务状态等待、ACL、双协议连接、真实客户端发布、DDNS-Go 配置页、卸载两种模式、实际玩家联机和更新失败回滚演练；这些不能由构建测试替代。
