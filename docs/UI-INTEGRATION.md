# 3.0 界面整合：已核实入口与下一步

状态：donor 外观已接入保留的 LAN 启动器，Windows hosted 无游戏界面检查已通过；
生产环境适配继续见 [UI-02](tasks/ui-02.md)。Public 尚未启用，实机边界不变。
本文其余源码对照和步骤保留为迁移合同；已执行结果以具体任务收据为准。
本文路径均相对于仓库根；`LAN` 指 `src/launcher/src`，`donor` 指
`integration/scbl3/client/ScblPublicLauncher`。公开文档不包含原始个人路径、账号、服务器地址或运行日志。

## 1. 已核实的源码事实

- 两端都是 .NET 10 WPF、x86、`win-x86`。可复用样式和布局，不需要另造一套 UI 技术栈。
- LAN `MainWindow.xaml` 有 116 个命名控件，donor 有 150 个；交集仅 53 个。
  同名并不代表行为相同：donor 的网络选项是 `Fast/Compatible`，LAN 是加入/创建房主。
- donor 的完整 `MainWindow.xaml.cs` 不是纯呈现层：构造函数创建 `NetworkOrchestrator`、
  `NetworkModeCoordinator`、`GameLaunchCoordinator`，Loaded 会启动公网网络生命周期。
  直接覆盖会引入 EasyTier、控制面、更新服务和路由器启动副作用。
- LAN `Services/GameLaunchPreparation.cs` 已有实际被 UI 调用的 `ILaunchPreparationOperations`
  边界。其顺序为校验玩法、部署、准备受限命名管道、beforeStart、启动；失败释放尚未移交的管道。
- donor `Application/GameLaunchCoordinator.cs` 调 `HookConfigService.WriteAuthFile`，
  而 LAN 使用命名管道交付凭据、不写旧认证文件。不得把 donor 启动流程当作 UI 一并覆盖。
- LAN 固定 DX11，donor 提供 DX9/DX11。当前已接受 gameplay 不能因此宣称支持 DX9；
  新界面保留渲染状态展示，但 DX9 不启用，除非另有明确实现和验收。
- LAN `MainWindow.xaml.cs::RunLaunchFlowAsync` 保留账号注册/恢复码、目标地址变更后的凭据作用域检查、
  主机就绪、Observer 权限检查、拥有者进程收尾、未确认启动状态。以上不能用 donor 同名方法替换。

## 2. 视觉结构如何落到现有行为

目标是 donor 的视觉层（顶部菜单、左侧状态/启动区、右侧登录、底部在线状态、弹层），
不是保留两个启动器供用户选择。按下表迁入**同一个** LAN `MainWindow`；临时 donor 文件只是来源。

| donor 元素/事件 | 保留的 LAN 行为与具体入口 | 必须保留的差异 |
|---|---|---|
| 顶部样式、`SettingsButton_Click`、语言/音乐/窗口按钮 | LAN 同名事件及音乐暂停/恢复 | 不带入 donor 自动联网的 Loaded 事件 |
| `btnLaunch` / `LaunchButton_Click` | LAN `LaunchButton_Click` → `RunLaunchFlowAsync` → `GameLaunchPreparation` | 原包校验、命名管道、启动/运行/结束/未确认状态均保留 |
| `cmbGameplayMode` / `GameplayModePresentation_SelectionChanged` | LAN `GameplayMode_SelectionChanged` | 保留 Official/MOD 校验、FOV 1.0–1.3、缺玩法模块阻止启动 |
| donor MOD/FOV 弹层 | LAN `fovSlider`、`GetSelectedFovScale`、`FovReset_Click` | 保存/取消应先在草稿值上操作，取消不能改变持久值 |
| `cmbNetworkMode` / `NetworkMode_SelectionChanged` | **不能直接复用事件**，见下一节 | 公网快速/兼容不是 LAN 加入/房主 |
| 账号/密码呈现 | LAN `MainWindow.Credentials.cs` 与原输入事件 | 每服务器凭据作用域、DPAPI、取消记住后删除、切服务器不得复用旧密码 |
| `ForgotPasswordButton_Click` 与恢复弹层 | LAN 同名入口、`ResetPassword_Click` | 新恢复码替代旧码的语义保留 |
| `ExportDiagnosticsMenuItem_Click` | 薄适配到 LAN `ExportDiagnostics_Click` | 保留本轮 sanitizer 修复、来源清单、范围缺口、显式导出且不上传 |
| `RefreshPlayersButton_Click`、在线弹层 | 薄适配到 LAN `RefreshPlayers_Click`/`PlayersButton_Click` | LAN 发现、多网卡绑定、房主切换、质量窗口继续可访问 |
| donor 存档菜单 | LAN `RestoreOfficial_Click`、`RestoreMod_Click` | 不恢复 donor 首次启动自动覆盖；显式确认、官方/MOD分离 |
| donor 设置弹层新增 LAN 区 | LAN `cmbBindAdapter`、`txtServer`、`DetectedHostName_Click`、`chkObserverMode` | 主机/加入、绑定IPv4、发现、一次性OB及房主许可不能被视觉重排删掉 |
| donor 对话框/引导 | LAN焦点边界、Tab循环、Escape、遮罩和中文/英文文案 | 屏幕缩放/键盘导航要单独测 |

`MainWindow.DonorShell.cs` 可以仅放缺失事件的薄适配方法，例如
`ExportDiagnosticsMenuItem_Click(sender,e) => ExportDiagnostics_Click(sender,e)`。
已有同名事件不重新定义。不得让薄适配重新实现账号、网络或启动业务。

## 3. 网络环境必须拆成两层

**已实现的类型和事务控制器，生产接入见 UI-02：**

- `NetworkEnvironment = Lan | Public`：用户确定的两个部署场景。
- LAN 内部 `LanRole = Join | Host`；公网内部 `PublicTransport = Fast | Compatible`。
- 一个只含非敏感呈现数据的快照：环境、角色、Ready、状态文字键、延迟、可切换性。
- 连接适配器输出本次会话的 endpoint/bind/capabilities，账号密码不放在可序列化状态快照中。

LAN 适配器复用 `LanConnection`、`HostRuntimeService`、`LanPresenceService`、
`NetworkAdapterService`。Public 适配器封装 donor `NetworkOrchestrator` 与
`NetworkSessionCoordinator`，但把控制面认证和本次启动配置接到保留的安全启动路径。

donor `NetworkModeCoordinator.ChangeAsync` 有并发锁、游戏中拒绝切换、失败回退；这些是
可移植的事务规则，不是把枚举改名就完成适配。LAN 模式不得启动 EasyTier、route guard、
更新监听或公网控制面；公网不得启动本机 LAN 房主服务。关闭只处理本会话拥有的进程。

## 4. 下一条可直接实施的 seam（UI-01）

1. 在复制后的候选上固定现有 `MainWindow.xaml` 的命名控件/事件清单；给已有 LAN 业务入口加
   UI路由契约测试，特别是启动、诊断导出、恢复码、存档和OB。不启动游戏。
2. 迁入 donor 的 Window.Resources、背景和三区布局；仍由 LAN `Window_Loaded` 初始化。
   保留 LAN 后台会访问的命名控件和事件；不要拷 donor `MainWindow.xaml.cs`、默认服务器或 updater。
3. 为 donor 新菜单名增加上述薄适配 partial，将 LAN 专属设置放入 donor 风格设置弹层。
   改所有可见品牌为 Fifth Freedom，保留 wire product、嵌入资源 logical name、数据目录身份。
4. 添加显式 LAN/Public 呈现契约和未接线的 Public 状态；未完成前不得伪装 Ready 或自动回落 LAN。
   UI-02 再接 Public adapter。UI-01 合格只证明新外观在 LAN 合同上可运行，不证明整合完成。
5. Windows 上编译和无游戏 UI smoke，检查事件解析、所有保留功能可达和不同 DPI；保留原布局
   回滚补丁。随后在云端继续 UI-02/服务器工作，不以剩余实机BUG阻塞迁移。

## 5. 回归与接受边界

| 检查 | 最低证据 |
|---|---|
| 静态接线 | XAML事件存在且唯一；所有控件引用可解析；未启用 donor TestChannel/自动更新/网络重建 |
| LAN业务 | build-entry、PortPlan、BackgroundTaskRegression、ObserverSession、包验证原有测试保留 |
| 启动生命周期 | canceled/auth失败/pipe准备失败/启动未确认/关闭中完成回调：不泄漏拥有者、不终止无关进程 |
| 数据安全 | official/MOD存档分离、不自动覆盖；密码作用域；导出24项修复不退回 donor 实现 |
| 网络隔离 | LAN不调用公网依赖；公网不创建LAN监听；切换时禁止进行中的会话；失败回退可观测 |
| Windows呈现 | 中文/英文、最小窗口、100/150/200%DPI、焦点/候选框、覆盖层、托盘/音乐 |
| 游戏回归 | 保留已有F3、MAC-11 HUD、中文、稳定世界相机、VSS EMF通过记录；不要求用户全量重复采集 |

Linux云环境可检查源码/路由合同和纯逻辑；WPF UI、DPAPI、命名管道的Windows实现、DX11实际
绘制及多人游戏不能以Linux单测代替。新界面由Windows runner出包，用户集中验收。

## 6. 本轮只读审计边界

本轮没有改产品源码、编译、启动服务/游戏或采集。两端服务器同路径文件的字节级对照为
2个相同、113个不同（含格式差异，**不是113个语义缺陷**），LAN独有37个，donor独有153个；
完整私有索引及输入SHA保存在 `.local/ui-integration-agent/SOURCE-AUDIT.json`。
九个初始品牌文件另存差异；发现 `LanConnection.RefreshAsync` 的能力协议 product 不应改为
显示名，已交主代理修正。不要把整合计划或文件搬运记作功能通过。

## 7. 云端任务交接（现在不在本机实现）

用户要求后续代码修改尽量放到云端。本轮只交文档；以下任务由拿到同一提交的云端执行：

- **UI-01A / 无副作用壳层契约**：在 `tests/` 新建不依赖游戏的命名控件/事件路由测试，
  输入为当前两份XAML与保留事件集合；断言LAN启动/导出/OB/恢复码/存档可达，不能出现donor
  `TestChannelAwareWindow_Loaded`、自动控制面或更新流程接线。先记录红测试，再移植视觉层。
- **UI-01B / 视觉移植**：只改 `src/launcher/src/MainWindow.xaml`、必要的
  `MainWindow.DonorShell.cs` 及本页列出的菜单适配；业务服务暂不改。交Windows runner编译。
  若编译无法在Linux进行，提交源码与测试后明确标记Windows待验，不伪造通过。
- **UI-02 / 显式环境模型**：新增纯C#模式/状态模型与fake adapter测试；Lan/Public和
  Host/Join、Fast/Compatible分开。验证切换事务、游戏活跃时拒绝、关闭期间迟到回调。
- **UI-03 / Public接线**：在SERVER-01协议差异闭合后，把donor网络服务放进Public adapter，
  继续调用保留的启动管道。此时才可称两个模式均已接线；公网实机另验。

每任务交付改动清单、执行命令/退出码、源码版本、未完成限制和回滚补丁。
本地只进行交接完整性/安全审查/必要构建验证；不要因文档中列出了代码任务就立刻本地实现。
