# SCBL 3.0 项目交接与操作手册

本文是 SCBL 3.0 唯一强制阅读的维护入口。其他设计资料只在定位相应模块时按需阅读，历史任务报告不作为当前事实。

## 1. 权威位置与状态

- 新项目根：`D:\codex\SCBL3.0`
- 旧项目根：`D:\codex\SCBL`，迁移完成前只作只读参考，不在两处同时开发。
- 默认分支：`main`
- 当前源码候选：Client/Launcher `3.0.6`，Hooks `2.0.148`，Updater `2.0.10`，Client Files `2.0.28`；Server release `3.0.16`，Manager `2.0.33`，Dedicated `2.0.28`，Control Plane `2.0.93`，Update Server `2.0.91`。运行服务器的实际版本以各自配置/描述文件为准；源码版本以 `COMPONENT_VERSIONS.json` 为唯一来源。
- 新部署的服务端默认采用 `server/windows` 原生 Windows Host：GUI 负责安装维护，Windows 服务监督 EasyTier、Dedicated、Control Plane 和 Update Server，普通 ZIP 做组件事务更新；服务状态等待、受控 ACL、配置/Bootstrap/Runtime 同事务回滚、上游外部组件 digest 和备份引用保护均在该入口闭合。真实安装手册见 `server/windows/README.md`。Linux Manager 继续服务既有 Linux 实例，不再是新部署的默认架构。
- Windows Host 已按当前通道的客户端组件、完整包和公告原子生成 `scbl-server.json`；服务端状态固定在 `%ProgramData%\SCBL Server`，与客户端 Route Guard 的 `%ProgramData%\SCBL` 隔离。客户端发布目录的可视化导入尚未移植，首次安装后需先放入一份已验收的 `%ProgramData%\SCBL Server\client\updates` 基线，不能把“服务进程健康”误写成“客户端发布链已就绪”。
- 独立 test 服务端 `scbl-test-01` 已在 Linux 编译机完成 SCBL 3.0 首次安装：同一本地游戏端口 `11011` 承载 WSS/TCP 与 QUIC/UDP，更新端口为 `18081`；IPv4 转发由安装器持久开启，未配置 `public_host`，路由器公网映射由环境侧维护。
- 正式/测试服务器使用同一个 `server-latest` 服务端包流；`server.mode` 只标识该实例服务客户端的 stable/test 通道，版本必须与本实例当前清单精确一致，不再维护两套服务端二进制通道。
- 新目录不携带旧 Git 历史、构建缓存、validation、temp、bin/obj/target、历史完整包、服务端包、诊断证据或本机密钥。

新任务先执行：

```powershell
Set-Location D:\codex\SCBL3.0
git status --short
```

在建立首个已验证基线提交前，不删除旧项目目录。

## 2. 产品是什么

SCBL 为《细胞分裂：黑名单》提供统一启动、更新、虚拟网络、好友与邀请、Party/房间、游戏内 Overlay、兼容修复和自建服务端。

3.0 当前启动链路（客户端与服务端均已迁移到单一描述文件）：

```text
Launcher 读取唯一 scbl-server.json（服务器、网络、当前组件、公告、玩法、Dedicated）
  -> 登录与控制面
  -> EasyTier 建立虚拟网络
  -> Launcher 验证实际协议与地址族
  -> 部署 Hooks 和 Client Files
  -> 启动游戏
  -> Hooks 提供 Overlay、邀请、Party 与兼容功能
  -> 游戏连接 Dedicated
```

## 3. 客户端组件

| 组件 | 职责 |
|---|---|
| Launcher | WPF 界面、登录、公告、更新协调、网络状态、游戏准备与启动 |
| Hooks | 注入游戏的 Rust DLL，承载联机兼容、邀请/Party、Overlay 和玩法能力；音频使用游戏原生 XAudio2.7 |
| Updater | Launcher 退出后的原子替换与重启 |
| EasyTier | 虚拟网络和 QUIC/WSS、IPv4/IPv6 数据通路 |
| Route Guard | Windows 路由保护与恢复 |
| Client Files | 必要的独立分发文件；玩法 TOML 由 Launcher 按服务器快照直接写入游戏目录 |

完整包不是第七个组件，也不重新编译全部组件；它把当前 stable 六组件和必要脚手架装配为一个 ZIP。

## 4. 服务端组件

| 模块 | 职责 |
|---|---|
| Dedicated | 账号相关游戏业务、会话、邀请、Party、房间及持久化事务 |
| Control Plane | Launcher 使用的控制与状态接口 |
| EasyTier Server | Manager 管理的外部虚拟网络依赖，不进入 SCBL 服务端包 |
| Update Server | 唯一 `scbl-server.json`、组件和完整包下载 |
| Server Manager | 服务端组件完整性校验、部署、备份、回滚和客户端发布事务 |
| Server release | 当前五个 SCBL 自有组件的不可变组合；更新 ZIP 只携带变化组件 |

## 5. 源码边界

```text
client/ScblPublicLauncher
  Presentation -> Application -> Services/Infrastructure

client/hooks
  Runtime/Features -> typed Overlay contract -> Overlay view

server/dedicated-server
  Dedicated 业务与存储

server/manager
  发布和部署事务

scripts
  唯一构建、冻结、发布和清理入口
```

UI 不直接执行网络、文件、进程或服务器副作用；服务层不拼玩家可见文案。Hooks 的渲染与运行时动作通过无 ImGui 类型的状态/命令契约连接。

Hooks、Dedicated、Quazal、多人状态机、MOD 逆向经验、失败路线与实机矩阵统一维护在 `GAMEPLAY_ENGINEERING.md`；日常任务只在涉及这些领域时按需读取，不把内容复制回本手册。

## 6. 日常开发

日常修改不提版、不冻结整个仓库：

1. 修改最小源码范围。
2. 运行变化模块的定向测试。
3. 删除被新路径取代且有引用证据的旧代码、测试、配置和文档残余。
4. 使用持久缓存做增量编译。
5. 用一个 Git 提交保存一个可回退结果。

增量编译缓存统一放在 `D:\codex\SCBL3.0\.work` 或仓库外；不可变冻结和发布证据统一放在 `D:\codex\SCBL3.0\temp\POWERPC\release`。不得把 `bin/obj/target`、完整包、patch、日志或 receipt 提交 Git。
跨机源码归档不得用会忽略通用 `bin` 名称的文件枚举代替完整源码树；传输后先断言 Rust `src/bin` 与工作区成员齐全，再进入 Cargo 检查或构建。

## 7. 本地构建

个人完整操作不在本文复制：Windows 客户端按 `client/README.md`；原生 Windows 服务端按 `server/windows/README.md`；既有 Linux 服务端才按 `server/INSTALLATION.md`。运行服务器只安装二进制 ZIP，不承担源码编译。

Launcher 正式候选：

```powershell
scripts\coordination\scbl-task.cmd freeze --profile launcher --destination temp\POWERPC\release\TASK\source
python scripts\build\build_component.py launcher --source-root temp\POWERPC\release\TASK\source\freeze --output temp\POWERPC\release\TASK\publish --receipt temp\POWERPC\release\TASK\build-receipt.json --version X.Y.Z
```

日常本机增量验证也可运行 `client\ScblPublicLauncher\build_publish.ps1`；默认复用 `bin/obj`，只有明确需要干净重建时才加 `-Clean`。SCBL 3.0 按当前维护决定不使用更新签名密钥；服务端描述中的 SHA256/大小只用于发现下载损坏或字节漂移，不提供发布者身份认证。

Hooks 使用固定 Rust/冻结入口。原生 Windows 服务端只使用 `server/windows/build-windows-server.ps1`，目标机只运行 `SCBL.Server.exe`；既有 Linux 服务端才使用 `scbl-server.py` 与 `SCBL` 菜单。不得为单次任务另建编译或发布脚本。

构建环境按目标分离：原生 Windows 服务端和 Windows 客户端在 Windows 构建；既有 Linux 服务端继续在 Linux 编译机构建。两条路径都复用固定 Cargo/NuGet 缓存、完整 workspace 和 `Cargo.lock`，不得为每项任务创建临时脚本或从零下载依赖。

原生 Windows 首次安装只需解压随包安装器并运行 `SCBL.Server.exe`；已有实例从同一界面选择只含变化组件的 ZIP。Linux 的命令、端口与迁移规则继续只在 `server/INSTALLATION.md` 维护，此处不复制第二份示例。

原生 Windows 服务端只填写一个服务器域名/IP和更新端口，客户端下载地址由二者自动生成，不再维护重复 URL 输入框。勾选 DDNS-Go 后才展开仅监听本机的管理端口和配置页入口，供应商凭据仍由 DDNS-Go 自己保存。既有 Linux 服务端仍按其安装手册配置公开地址。EasyTier 与 DDNS-Go 由服务端管理入口独立下载安装；有效版本和缓存直接复用。

人工操作优先直接运行 `SCBL`，从“服务端管理 -> 更新”选择 ZIP；上述命令行形式只供自动化。服务器配置使用 schema 5，并以 `server.mode` 作为正式/测试身份唯一来源。玩法 DLL 开关与 FOV 是客户端本地启动配置，不再由服务端配置重复维护；Manager 将客户端所需服务器、网络、组件、完整包和公告事实收敛为一个 `scbl-server.json`。

## 8. 客户端个人维护速查

### 8.1 从哪里改

| 需求 | 首选位置 | 不应放入 |
|---|---|---|
| 窗口、布局、文案、可访问性 | `client/ScblPublicLauncher/MainWindow*.cs`、`MainWindow.xaml`、资源 | 网络探测、进程和文件事务 |
| 启动、更新、模式切换流程 | `Application/` | WPF 控件细节 |
| HTTP、EasyTier、Hooks 部署、配置与存储 | `Services/`、`Infrastructure/` | 玩家文案和窗口状态 |
| 服务端下发契约 | `Models/`、`Contracts/` | 旧通道兼容字段 |
| 启动器退出后的替换/重启 | `client/SCBL.Updater` | Launcher 主进程内自覆盖 |
| 启动器与 Updater 共用的安装布局 | `client/Shared` | 两边各复制一份路径规则 |
| 游戏内行为、邀请/Party、Overlay | `client/hooks` | Launcher UI |
| Route Guard 进程 | `client/scbl-process-router` | Launcher 组合根 |

先搜索调用者再改；同一事实只能有一个状态源。新路径接管后，同一提交删除旧调用、旧测试夹具和旧配置字段。Hooks/Dedicated 的 ABI、导出、游戏线程和网络协议只有在行为证据齐全时修改。

### 8.2 玩家目录和运行数据

```text
SCBL.exe
SCBL.Updater.exe
components/
  hooks/
  network/
  route-guard/
data/<计算机名>/<Windows用户名>/
  config/       设置和账号侧非秘密状态
  logs/         Launcher、Updater、Hooks 游戏日志
  runtime/      本次运行状态
  network/      EasyTier 运行配置与观察
  components/   已安装组件事实
  updates/      下载、断点和事务暂存
  diagnostics/  脱敏诊断包
```

路径规则只由 `client/Shared/ClientDataLayout.cs` 定义。Launcher 日志为 `logs/scbl-public.log`；正常由 Launcher 启动游戏时，Hooks 使用 `SCBL_CLIENT_DATA_DIR` 写入 `logs/game/bl-tracing.log` 和 `logs/game/hooks-party-trace.log`。WinDivert 包内文件只是安装载荷，实际 Windows 服务仍使用 `%ProgramData%\SCBL\RouteGuard` 中受 ACL 保护的副本。

### 8.3 最小验证

```powershell
# Launcher（包含共享布局、更新、网络和 UI 契约）
dotnet test client\SCBL.Client.Tests\SCBL.Client.Tests.csproj -c Release -p:SCBLTestBuild=true

# Updater
dotnet test client\SCBL.Updater.Tests\SCBL.Updater.Tests.csproj -c Release

# Hooks；脚本会解析仓库固定工具链，不要裸找 cargo
scripts\build-rust-components.ps1 -HooksOnly

# Route Guard（本机安装 Go 后）
Push-Location client\scbl-process-router
go test ./...
go vet ./...
Pop-Location
```

只改某一行为时先使用测试过滤器跑变化点，再在候选冻结上跑一次组件全量。环境缺解释器、缓存不可写或 DPAPI/SSH 不可用时先修入口，不要用完整测试探测环境，也不要为任务临时生成编译脚本。

### 8.4 构建、补丁和完整包

`VERSION_CLIENT` 是 Launcher/完整客户端的唯一版本源；`COMPONENT_VERSIONS.json` 中的 `clientVersion`、`launcherVersion` 必须与其完全一致，固定构建与装包入口会拒绝错位。其他独立组件版本仍在 `COMPONENT_VERSIONS.json`。只有准备不可变候选时提版，日常验证不提版。

```powershell
# 固定入口查看与冻结
scripts\coordination\scbl-task.cmd --help
scripts\coordination\scbl-task.cmd freeze --profile launcher --destination .work\launcher\source

# Launcher/Updater 正式候选（只构建变化组件）
python scripts\build\build_component.py launcher `
  --source-root .work\launcher\source `
  --output .work\launcher\publish `
  --receipt .work\launcher\build-receipt.json `
  --version X.Y.Z
```

Windows 完整候选统一由 `client/build_all_windows.ps1 -Fast -Package` 构建并生成完整 ZIP；`scripts/release/assemble-client-components.ps1` 生成组件目录，`scripts/release/build-client-component-patch.ps1` 再装入选择的变化组件。新候选必须先让目标服务器拥有与完整包一致的六组件基线，再发布同一候选完整 ZIP；Manager 会拒绝组件清单缺项或字节不一致的完整包。Updater 更新 Launcher 的顺序固定为：Launcher 退出 → 校验暂存字节 → 原子替换 → 明确显示进度/错误 → 重启 Launcher。任何失败必须保留旧可运行字节或回滚点。精确命令只在 `client/README.md` 维护。

### 8.5 首查故障

- 启动器无法启动游戏：先看 `scbl-public.log` 的服务器描述、组件回执、Hooks 部署和游戏进程阶段，不先重装。
- 游戏无 Overlay/邀请：再看 `logs/game` 两份 Hooks 日志，核对实际部署 DLL SHA；自动测试不等于 DX9/DX11 实机。
- 更新后未重启：先看 Updater 可见窗口与退出码、目标/暂存 SHA、父进程是否退出，不重复下载或覆盖。
- Route Guard 失败：先核对服务注册、受控驱动路径和文件是否存在；不从玩家目录直接长期加载驱动。
- 网络显示错误：只信同一 runtime 的 Connected 快照和实际 transport/address family，不根据模式名猜测。

## 9. test 与 stable

标准客户端路径：

```text
源码修改 -> 定向测试 -> 变化组件正式构建一次 -> 组件 patch
-> 发布到独立 test 服务器并实机验收
-> 将完全相同的组件字节发布到独立 stable 服务器
-> stable 服务器发布同版本完整包 -> 返回公网链接
```

- test 和 stable 使用原始字节 SHA256、公开完整下载、Range 206 和回滚点；当前 SCBL 3.0 不使用更新签名密钥。
- test/stable 是两台独立服务器，不执行服务器内部“通道提升”；正式发布复用已验收 test 的完全相同字节，不重新编译。
- 每次客户端 stable 完成必须同时生成并发布同版本完整包。
- 发布组件后可由服务器从当前六组件直接封装完整包；手动上传完整包也走同一事务。公网固定地址为 `/SCBL.zip`，内部带版本号不可变包只作三代回滚。
- SCBL 管理的 Runtime、EasyTier、完整包、组件、清单备份和数据备份统一保留最近三代；当前、previous 和仍被当前通道引用的制品不得清除。
- 服务端运行组件使用独立的在线人数、migration、备份、包内 SHA 和服务双采样门禁。
- 客户端 patch/组件发布回执必须给出公开下载 URL；stable 完整包回执必须给出 `fullPackageUrl`。

固定入口只有：Windows 个人完整构建使用 `client/build_all_windows.ps1`，快速开发任务可由 `scripts/coordination/scbl-task.cmd` 路由，组件/补丁装配使用 `scripts/release/assemble-client-components.ps1` 和 `scripts/release/build-client-component-patch.ps1`，服务器发布直接运行 `SCBL` 菜单。3.0 不再保留旧的签名 release plan、远端通道提升或任务级发布脚本。

## 10. GitHub 与开源

- GitHub 默认只运行无秘密的源码检查、测试和在线构建。
- push/PR 不得自动写 test、stable、生产服务器或 GitHub Release。
- SSH 身份、数据库、令牌和运行配置永不进入仓库、Actions 日志或 artifact。
- GitHub Actions 只生成开发候选；任何通道或服务器写入都要求显式人工触发和受保护环境。
- 首次公开前必须完成许可证、第三方二进制、商标、游戏 SDK/头文件、抓包/逆向资料和个人数据审计。
- 历史 `research-artifacts`、完整包、服务端包、下载缓存和本机构建目录未迁入新仓库。

## 11. 记录与恢复

- Git commit/tag：代码和正式版本历史。
- `CHANGELOG.md`：玩家可见变化。
- 机器 receipt：构建、发布、回滚和公开验证证据，存于受保护发布存储，不进入 Git。
- 本文：当前架构、版本、风险、操作入口与下一步。

每次 stable、完整包、服务端生产或重大架构变化后更新本文；普通局部修复只更新 Git 和必要的 CHANGELOG。

## 12. 当前待完成

当前待发布候选包括：客户端默认 test 地址改为 `test.example.invalid:11011` / `18081`；服务端新增当前组件一键封装完整包、固定 `/SCBL.zip` 和统一三代保留策略。既有多人实机仍需复测三机“接受邀请离开快速比赛”、房主迁移，以及控制面 heartbeat 中断 20–90 秒时同模式邀请仍成功、超过 90 秒失败关闭、恢复后立即成功。

当前自动验证基线：Launcher 391/391、Updater 17/17、Hooks 95/95、Hooks loader 1/1、Gameplay 22/22、Dedicated 152/152、Quazal 26/26 加 doc 1/1、Manager 138 项通过（另 2 项按平台跳过）、服务端组件装配 12/12、服务端根测试 36/36、协调工具 20/20、完整包布局与清单逐文件校验通过。变化组件只生成一份有效正式字节；单机真实游戏已完成线上登录和 Party/Game 建立，多机邀请切换、迁主与断线 grace 仍待实测。Route Guard 的 Go 回归由 CI 覆盖；严格 Clippy 的既存 ABI/Hook 样式债务不作为猜测性改写理由。

1. 首次公开前确认 `launcher_bgm.mp3`、最终背景、两份存档模板和 `FusionFix.zip` 的来源与再分发许可；`FusionFix.zip` 当前故意不进入 Git。完成前不创建或推送公开基线提交。
2. 在 GitHub 私有仓库先运行首轮 CI/在线开发构建，确认 Windows、Linux 和 Go Runner 的结果。
3. 客户端迁移已完成：正式/测试服务器各自读取自己的 `scbl-server.json`，旧 `launcher_config_v2`、`launcher_controls`、`networkBootstrap`、独立公告和签名组件清单入口已删除。
4. 组件更新仍保留 SHA256、断点下载、事务安装和 Updater；`ClientComponentUpdateService` 已按协调/Bundle/下载拆为三个 partial，后续只在出现明确职责边界时再继续拆分。
5. 玩家安装根已收敛为 `SCBL.exe`、可见的 `SCBL.Updater.exe`、完整包清单、`components/{hooks,network,route-guard}` 和 `data/计算机名/Windows用户名`。配置、日志、缓存、更新暂存、诊断与存档备份统一位于当前用户的 `data` 子树，完整包更新保留该目录；路径由共享 `ClientDataLayout` 定义。WinDivert 在包内仅作为校验后的安装载荷，实际服务继续从受 ACL 保护的 `%ProgramData%\SCBL\RouteGuard` 运行。
6. `MainWindow.xaml.cs` 已拆出服务器描述与运行时 partial，主文件约 1160 行；下一步只迁移有独立状态所有者的组合根，不为追求文件数量做机械拆分。
7. 客户端构建入口继续收敛为 `scbl test/build/patch/package/clean`；发布由目标服务器的 `SCBL` 菜单完成。正式服与测试服互相独立，不保留同服务器 `promote` 命令；服务端已经统一为一个组件 ZIP builder 和一个 Manager 部署入口。
8. 全仓审查已收口：删除了 21 份生成协议中的 Quazal 客户端半边、无 transport 的 `ClientProtocol` 和三份未进入模块图的旧协议文件；删除 Launcher 两个零调用私有方法及已废弃的 `minLauncherVersion` 全链，服务器对未知组件字段改为失败关闭；修复 Windows `-Auto` 漏掉未跟踪文件、反复重编上一提交、漏判共享 API/Cargo/Updater 版本输入；允许合法的纯删除 Client Files 组件包；CI 现同时运行 Launcher、Updater、Hooks、Dedicated 与 Quazal 回归。
9. 已知保留债务：Hooks `DllMain` 初始化仍受 loader-lock 约束，只能在真实游戏门禁下另行迁移；Overlay 全屏初始化仍使用有历史运行依据的固定等待；`MainWindow.xaml.cs` 仍是较大的组合根；Hooks 严格 Clippy 仍有大量既存 ABI/指针与样式项。不得为清零静态警告机械改写高风险 ABI 或游戏线程代码。
10. 真实联调时重点覆盖：正式/测试服务器切换后缓存不串用、描述缺字段失败关闭、完整包首次建立 Hooks 回执、更新后重启、Fast/Compatible 的 IPv4/IPv6 与 QUIC/WSS、DX9/DX11、Overlay 全屏启动延迟和邀请/Party。

本次迁移已删除旧 Git/角色流程、历史任务报告、旧版本说明、validation/temp、构建缓存、完整包/patch/旧 Runtime 产物与构建脚本、音频构建树、研究材料、零引用背景阶段稿和零调用脚本。旧项目仍保留完整历史，可用于必要取证。

## 13. 不可省略的安全规则

- 不提交或输出密码、私钥、token、数据库、真实账号资料和原始敏感载荷。
- 任何远端写入都先绑定 expected-before 哈希并保留原子回滚点。
- 正式服务器不在玩家在线时试验运行组件、网络协议或 Dedicated 变更；独立测试服按用户约定可直接推送候选，但仍必须保留回滚点与健康检查。
- 删除前验证绝对目标；只删除可再生或已有明确备份的对象。
- 自动测试、正式构建、通道回读和真实游戏验证必须分别表述。
