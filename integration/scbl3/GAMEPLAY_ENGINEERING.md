# SCBL 游戏工程与多人业务手册

本文集中记录 SCBL 3.0 的 Hooks、Dedicated、Quazal、多人业务和 MOD 逆向工程知识。它描述的是可复用的工程事实，不是聊天记录、版本流水或发布回执。产品总体入口仍是 `SCBL_HANDOFF.md`；本文件专门回答“游戏实际怎样运行、三层如何对应、哪些边界不能随意改、过去哪些逆向路线已经证实或推翻”。

本文件由 SCBL 3.0 主维护者唯一维护。开发专家提供的历史经验和建议必须先与当前源码、测试或实机证据核对，再合并到对应原章节；不追加聊天流水，不为单次任务新增重复文档。

## 0. 阅读规则与证据等级

### 0.1 当前事实的优先级

1. `D:\codex\SCBL3.0` 当前源码与测试是实现事实。
2. 当前真实游戏日志、服务器 journal 和脱敏数据库状态是运行事实。
3. `SCBL_HANDOFF.md`、`CHANGELOG.md`、本文件列出的设计文档和旧 2.0 报告是解释与历史证据。
4. `D:\codex\SCBL` 只用于追溯成熟 2.0 行为，不能覆盖 3.0 当前源码。
5. 自动测试证明代码契约，不等于 DX9/DX11、双机或多人实机已经通过。

### 0.2 证据成熟度

| 标记 | 含义 |
|---|---|
| 多机实测 | 两台或以上真实游戏客户端观察到一致结果，必要时已与服务端对齐 |
| 单机实测 | 一台真实游戏客户端确认；不能代替多人、迁主或跨机证明 |
| 线上日志 | 客户端与服务器时间线、状态或数据库已对齐，但未必完成玩家侧闭环 |
| 自动测试 | 单元、契约或集成测试固定了行为；尚不代表真实游戏 ABI 正确 |
| 静态反编译 | 来自反汇编、对象布局、调用图或固定地址；需要匹配游戏版本和实机验证 |
| 未确认假设 | 有设计价值，但证据不足，不能据此改行为 |
| 已推翻 | 已由实机、日志或调用证据否定；禁止原样重试 |

同一结论可以同时带多个标记。例如“地址和字段由静态反编译确认，功能由多机实测确认”。

## 1. 玩家视角的现行业务全景

### 1.1 Official 与 MOD

| 玩家概念 | 当前行为 | 证据 |
|---|---|---|
| Official / 官方 | Hooks 只提供联机兼容、Overlay、存档路由、FOV、声音、网络与稳定性修复；不加载 `scbl_gameplay.dll` | 自动测试、当前源码 |
| MOD / 模组 | Hooks 加载唯一 `scbl_gameplay.dll`；DLL 本身是玩法总开关，当前已完成玩法全部初始化，不再由逐项 feature flag 决定 | 自动测试、当前源码 |
| 模式来源 | 只读 `scbl.toml` 顶层 `GameplayMode = "Official" / "MOD"`；缺失、未知或旧模式名失败关闭为 Official | 自动测试、当前源码 |
| 多人隔离 | Dedicated 只允许同模式玩家互相发现、邀请或加入；MOD 双端还必须有有效玩法完整性状态 | 自动测试、当前源码 |
| 存档隔离 | Official 使用原路径；MOD 使用同一存档根下的 `MOD/` 命名空间，槽位 ID 和文件格式不变 | 自动测试、当前源码；实机待完整回归 |
| FOV | 1.0–1.3，非法值回到 1.2；它是 Official/MOD 共用显示偏好，不以玩法 DLL 是否加载为条件 | 自动测试、当前源码；实机待双模式回归 |

旧 `Casual/Serious`、欢乐/严肃模式不属于 3.0 运行契约。它们既不能进入 Hooks 配置，也不能被 Dedicated 当作兼容模式接受。

### 1.2 玩家、Party、Lobby 与 Game Session

SCBL 同时维护两类关系，不能把它们合并成一个“房间成员列表”：

| 概念 | 玩家看到什么 | 权威关系 |
|---|---|---|
| 个人大厅 | 通常显示自己 1 人，可继续邀请 | 个人 Party + 原生 lobby Session |
| Party / 队伍 | 谁属于同一社交队伍、谁是队长 | Dedicated `parties` / `party_members`，Hooks 投影给 Uplay Party ABI |
| Lobby Session | 原生游戏用于大厅/准备阶段的传输 Session | Quazal `game_sessions` + `participants`，属性 `113=>1` 表示 lobby |
| Game Session | 私人比赛或快速匹配的比赛 Session | Quazal `game_sessions` + `participants`；creator 是当前传输房主 |
| Uplay GameSession 发布 | 房主当前可被邀请加入的原生 496 字节上下文 | Hooks 捕获并串行发布；Dedicated 只选择仍然有效的当前发布 |
| 邀请 | 对某个目标 Session/Party generation 的一次有期授权 | Dedicated invite 行 + Hooks 本地 generation + 游戏原生 accepted event |

重要结论：Party 成员相同，不代表 native Lobby Session 参与者相同；Game Session 参与者相同，也不代表 Party 已经补齐。多人退出、后加入和迁主问题大多来自这三者在短时间内不同步，而不是简单的“在线/离线”。【线上日志、多机实测】

### 1.3 私人房、快速匹配与退出

- 私人房：通常只有一个 Party，但仍同时存在 Party、lobby Session 和私人 Game Session。私人邀请使用精确 Session；不能把私人房重写成公共容量房。【自动测试、多机实测】
- 快速匹配：一个公开 Game Session 可以同时容纳多个独立 Party。房间房主与每个 Party 的队长不是同一概念；离开时只能恢复离开的那一队，不能把全房玩家合并成一个 Party。【自动测试、多机实测】
- 队员单独离开：应从当前 Game Session 和 Party 脱离，并回到自己的 1 人大厅；不能长期停留在 0 人 Party 投影。【自动测试、多机实测】
- 队长选择“和队伍一起离开”：只带本 Party 成员回到其共享 lobby；快速匹配内其他 Party 保持原比赛状态。【自动测试、多机实测】
- 房主正常退出或掉线：优先让游戏原生迁主；只有原生迁移未闭合时才进入 N-1 恢复。N-1 是保底，不是迁主的替代品。【多机实测、线上日志】

### 1.4 掉线、重连与错误显示

- Launcher/EasyTier 负责虚拟网络；Hooks 和 Dedicated 不应根据“模式名”猜网络是否恢复。
- Hooks 事件轮询第一次失败会展示连接错误并重建缓存连接；首个成功轮询，无论有没有邀请，都必须发送 `Ok(None/Some)` 清除错误并把失败计数归零。
- 游戏 Session 断线不等于账号离线；好友在线、Party、Game Session 和 Uplay 发布有各自的过期与恢复节奏。
- 任何恢复都必须由已验证的成功事件驱动，不能用定时隐藏错误冒充恢复。

证据：`client/hooks/src/uplay_r1_loader.rs::observe_event_poll`、Overlay Receiver 消费路径。【自动测试、线上日志】

### 1.5 存档、FOV、声音、Overlay 与渲染后端

| 能力 | 当前边界 | 证据 |
|---|---|---|
| 存档 | `client/hooks/src/uplay_r1_loader/save.rs` 保持槽位 ID/格式；MOD 只改变目录命名空间；扩展装备另用有界 sidecar | 自动测试、当前源码 |
| FOV | `fusion_compat.rs` 在有限窗口中定位已加载 FusionFix/FusionMod 的 `FOVFactor` 存储并设置目标值；不修改玩家 INI | 自动测试、静态反编译；实机待 3.0 双模式确认 |
| 声音 | 音频代码静态链接进 Hooks，以真实 XAudio2.7 ABI 在 Uplay 启动阶段初始化；不再分发外置 `scbl_audio.dll` | 当前源码、自动测试；3.0 多设备实机仍需回归 |
| Overlay | DX9/DX11 Hudhook 生命周期、Receiver 排空和动作分发归 Hooks runtime；文案、布局和卡片呈现通过 typed contract 与 ImGui 层连接 | 当前源码、自动测试；DX9/DX11 实机待分别确认 |
| 原生弹窗恢复 | 仅 DX11 已验证地址启用；复用游戏默认 action dispatch，不伪造按钮或直接删正常提示 | 自动测试、静态反编译、多机历史实测 |

## 2. Hooks、Dedicated 与 Quazal 的职责边界

### 2.1 三层映射

| 同一概念 | Hooks | Dedicated | Quazal / 游戏协议 |
|---|---|---|---|
| 身份 | 从显式 `scbl.toml` 登录，不生成演示凭据 | 验证账号、映射玩家 principal、保存 presence | TicketGranting、SecureConnection、Tracking、已认证 `ClientInfo` |
| 在线 | 轮询好友/事件并投影 | 持久 presence、事件投递、心跳清理 | 安全连接与注册 URL 提供实际端点 |
| Party | 实现 Uplay R1 Party 导出、投影 authoritative roster | `parties`/`party_members`、队长转移、Party generation | 原生 lobby Session 仍是独立传输对象 |
| 邀请 | 展示卡片、F5 边沿、按稳定 ID 接受、生成正确原生 event | 选择可邀请 Session、创建/接受/消费 invite、限制 generation | Friends search、NAT、JoinSession 或 Party accepted event |
| 房间 | 捕获 `UPLAY_USER_SetGameSession`，串行发布最新状态 | 保存当前发布、选择有效房主和目标 Session | Create/Update/Add/Remove/Join/Leave/Abandon/Split Session |
| 迁主 | 观察并在证据完备时桥接原生 Storm 状态 | creator/Party leader 原子迁移、保留或恢复 N-1 | `SplitSession` 调用者是游戏选出的新房主 |
| 错误 | Overlay 显示、原生弹窗默认动作恢复 | 返回稳定 RMC/gRPC 错误，不暴露存储细节 | PRUDP/RMC 解析失败关闭，不 panic |
| MOD | Official 不加载，MOD 加载唯一玩法 DLL | 模式一致性与 MOD 完整性门禁 | 不改变零售协议结构和 Session 枚举 |

### 2.2 绝对边界

- Hooks 不拥有服务器持久 Party/Session 真相；它只投影与驱动游戏 ABI。
- Dedicated 不伪造客户端内存状态或 ImGui 行为；它只提供事务和协议结果。
- Quazal 不承载产品层“邀请谁/恢复谁”的策略；它负责可靠解析、序号、缓存、RMC 调度和错误映射。
- Launcher 负责选择模式、验证文件和写入显式配置；Hooks 不从 DLL 是否存在、FOV、通道或 feature 组合反推模式。
- 任何修复如果需要同时改变 Party 与 Session，必须在同一数据库事务中明确两者的先后关系，不能只改其中一个列表。

## 3. 关键状态机

### 3.1 登录与安全连接

```text
显式用户名/凭据
  -> TicketGranting LoginEx / Login
  -> 取得玩家 principal 与票据
  -> SecureConnection Register/RegisterEx
  -> 服务端只信任 socket 观察地址
  -> Tracking / post-login settings
  -> StateLoggedIn -> StateOnline
```

不变量：

- 新玩家 principal 从 1000+ 分配并跳过基础设施主体；低 PID 不能重新分配给玩家。
- `Tracking` 等名字属于 Quazal 服务主体，不是普通玩家默认账号。
- 通用 `StationURL` 必须包含 `address`；只有 Secure Register/RegisterEx 的不受信请求解码类型允许零售客户端省略 address。handler 仍只返回 `ClientInfo::address()`。
- 登录、票据、Register 任一步解析或身份验证失败都返回协议错误并停止进入 Online；不得用固定账号、固定 PID 或默认密码继续。

证据：`storage/accounts.rs`、`ticket.rs`、`secure.rs`、`sc_bl_protocols/.../secure_connection_protocol.rs`、`quazal/src/rmc/types.rs`。【单机实测、线上日志、自动测试】

#### 3.1.1 玩家 principal 与服务主体分区

Quazal 的 principal ID 不是单纯自增玩家编号。低位和固定编号中存在认证、Tracking 与 Secure Server 等基础设施主体；把普通玩家分配到这些编号会让 `LoginEx` 表面成功、后续 Tracking/票据却按服务身份解释同一 PID，最终在登录后退出。

当前 3.0 约束：

| 主体 | 编号规则 | 不变量 |
|---|---|---|
| 普通玩家 | 从 1000 起分配 | 兼容成熟 2.x 玩家命名空间，不重新使用基础设施低位 |
| Secure Server | 固定保留 `0x1000` | 玩家分配跨过该编号 |
| 认证/Tracking 等服务 | 使用协议既有主体 | 不能由注册玩家覆盖，也不能当默认玩家账号登录 |

`server/dedicated-server/src/storage/accounts.rs::register_user_unsafe_async` 在数据库内原子选择下一个玩家 ID，并显式跳过 Secure Server。迁移只删除可证明未被真实数据引用的演示种子，并恢复服务主体；已有真实玩家、会话或 endpoint 不能因“清理样例”被删除。【单机实测、线上日志、自动测试、当前源码】

### 3.2 Official/MOD 门禁

```text
Launcher 解析服务器能力并验证组件
  -> 写 GameplayMode
  -> Hooks typed parse
       缺失/未知 -> Official
       Official -> feature_bits=0，不加载玩法 DLL
       MOD -> 校验并加载 scbl_gameplay.dll，默认功能位全开
  -> Dedicated 对双方 gameplay mode + integrity 做同模式门禁
```

失败路径：MOD 文件无效、模式状态过期、任一端状态未知或模式不一致，均拒绝多人发现/邀请/加入；不能静默跨模式联机。【自动测试】

#### 3.2.1 模式断言与在线心跳是两种寿命

当前链路同时承载两个不同概念：

```text
Launcher 每 5 秒 heartbeat
  -> Control Plane 维护 active client
  -> 原子发布 gameplay mode snapshot
  -> Dedicated 在发现/邀请/加入前检查双方 mode + integrity
  -> Hooks 把拒绝原因映射为玩家提示
```

- 在线心跳回答“这个 Launcher 最近是否仍活跃”，适合较短的失效时间。
- 模式断言回答“这个已认证游戏进程最后一次通过校验的 Official/MOD 状态是什么”，其可信度来自认证、组件完整性和启动 generation，不应因为一次短暂控制面抖动立即消失。
- 两者都不能由好友在线显示、已加载 DLL、FOV 或 feature 组合反推。

已确认故障模型：邀请目标的游戏与 Dedicated 数据面仍在线，但 Launcher 到 Control Plane 的 heartbeat 约 49 秒没有成功；20 秒 active-client 清理同时删除了目标的模式条目。发送者仍能邀请另一名模式状态新鲜的玩家，而对该目标连续得到 `player mode state unavailable`。heartbeat 恢复、快照重新出现后，同一目标无需重启即可再次被邀请。【多机实测、线上日志、当前源码调用链】

这不是永久模式不一致、邀请 Session 损坏或好友状态错误，而是“在线 TTL 被直接当成模式断言 TTL”的短断误拒绝。当前仍保持 fail-closed；修复时应拆分寿命，而不是绕过门禁：

1. active-client/在线显示可以继续使用短 TTL。
2. 最近一次已认证的模式断言使用独立 90 秒 grace；在线列表仍使用 20 秒 TTL。新上报的非法模式撤销旧断言，明确的 MOD 完整性失败覆盖旧状态并继续失败关闭。
3. 从未见过、签名/完整性失败、明确模式不一致、grace 到期仍然拒绝。
4. Dedicated 日志应区分 missing、stale、snapshot-read-failed 和 mismatch；玩家提示可以保持简洁。
5. heartbeat 恢复后新断言必须立即覆盖 grace 状态，不要求重启游戏或 Launcher。

源码对应关系：Launcher heartbeat 在 `client/ScblPublicLauncher/MainWindow.xaml.cs`；在线 TTL、独立模式断言和快照发布在 `server/control_plane_domain.py`、`server/scbl_control_plane.py`、`server/control_plane_gameplay.py`；Dedicated 门禁在 `server/dedicated-server/src/gameplay_modes.rs`、`api.rs`、`game_session_ex.rs`；Hooks 玩家提示在 `client/hooks/src/uplay_r1_loader/friends.rs`。当前源码已将两种寿命分离，并固定 20 秒在线清理、90 秒已认证模式保留、显式无效状态立即撤销以及 missing/stale/read-failed 分类；自动回归已通过，仍待真实断网 20–90 秒邀请与超过 90 秒失败关闭实测。

### 3.3 Uplay GameSession 发布

```text
游戏 SetGameSession / ClearGameSession
  -> Hooks GameSessionPublishState 更新 generation
  -> 单一异步锁串行发送
  -> Set/Clear/Set 合并为最新 desired generation
  -> 重登录仅在本地仍有 session 时强制重发
```

旧的“每次 Set/Clear 各自 `runtime.spawn`”会导致网络完成顺序覆盖调用顺序，现已由 `client/hooks/src/api.rs::synchronize_game_session` 串行化。任何旧请求完成都不能把更高 generation 标成已应用。【自动测试】

### 3.4 邀请生命周期

```text
发送者请求邀请
  -> Dedicated 选择仍有效的当前 Session
  -> 创建/刷新 invite generation
  -> 接收端轮询得到服务器 invite
  -> Hooks 映射为稳定 local invite_id
  -> 展示至少一个明确输入边沿
  -> 接受服务器邀请
  -> 根据 kind 构造唯一原生事件
  -> 加入成功或明确失败后消费/退休 generation
```

当前主要 lane：

| kind | 语义 | 客户端进入路线 |
|---|---|---|
| 1 | 直接私人房/私人比赛邀请 | 精确私人 Session，Friends accepted -> participant search/NAT/Join；服务端先授权成员 |
| 2 | 公共大厅/普通游戏邀请 | 公共搜索 -> NAT -> JoinSession |
| 3 | Party follow | 独立于手动邀请，使用房主的精确发布上下文 |
| 4 | 内部 lobby/Party 恢复 | 不面向玩家展示；只恢复原生大厅与 Party 同步 |

不变量：

- server invite ID 可被 SQLite 复用，因此 Hooks 必须映射为本地 generation；终态后退休，网络临时错误时不退休。
- 同一目标 Session 的重复接受必须合并；一次加入未结束前不能再向游戏投递第二个 Join 事件。
- 30 秒是单条邀请的有效期，不是失败后阻止玩家接受其他邀请的全局锁。
- 私人 accepted event 需要房主精确的 496 字节 `UPLAY_USER_GameSession`；不能把账号指针当 payload，也不能构造假的空上下文。
- 房主和队员都可以邀请，但 Dedicated 必须把邀请投影到当前有效房主发布，不得引用已退出房主的陈旧 Session。

证据：`client/hooks/src/api.rs`、`overlay.rs`、`overlay/invites.rs`、`server/dedicated-server/src/api.rs`、`storage/invites.rs`、`PRIVATE_MATCHMAKING_REPAIR.md`。【多机实测、自动测试、线上日志】

### 3.5 Party 与 Session 加入

```text
接受邀请
  -> invite 对目标 Session 授权
  -> Dedicated apply_party_invite_generation（若该 lane 需要 Party）
  -> native search/NAT/Join/AddParticipants
  -> Hooks 从 authoritative PartyRoster 投影 Uplay Party
  -> 游戏进入 lobby 或 match
```

必须分别回答：

1. 玩家是否属于持久 Party？
2. 玩家是否属于原生 lobby Session？
3. 玩家是否属于目标 Game Session？
4. 当前 Session creator/Party leader 分别是谁？

任何只回答其中一项的“已加入/已离开”判断都不完整。

### 3.6 房主退出、原生迁主与 N-1

```text
房主退出/掉线
  -> Dedicated 标记 host_leave_pending，保留活跃 Game Session
  -> 游戏选出剩余参与者并调用 SplitSession
  -> Dedicated 以已认证调用者作为新 creator
  -> Hooks 让 Storm 原生 generation/participant/platform identity 继续
  -> 成功：比赛继续
  -> 超时/失败：恢复剩余 N-1 到同一共享 lobby 或各自个人大厅（按原 Party 边界）
```

迁主成功条件不是“服务端改 creator”一项。客户端还必须完成：generation 匹配、新房主 participant 存在、平台身份对象 ready、Storm 主机确认和 restart/continue 状态闭合。Hooks 中 `storm.rs` 只在唯一 live owner、已知 generation、host participant ready、平台身份一致等条件全部满足时桥接；否则调用原生路径或失败关闭。

快速匹配额外不变量：一个 match 可含多个 Party；Party 队长“和队伍一起离开”只能带自己的 Party。私人房额外不变量：后邀请成员可能进入 Game Session 但尚未进入原 lobby Session，因此恢复必须使用 Game+Lobby 成员并集，而不是只看某一张表。

证据：`hooks/storm.rs`、`game_session.rs::split_session`、`storage/sessions.rs::migrate_game_session_host`、`storage/parties.rs`。【多机实测、线上日志、自动测试、静态反编译】

### 3.7 成员单独退出与个人大厅恢复

```text
成员 Leave/Remove/Abandon
  -> 从当前 Game Session participant 移除
  -> 从原 Party 脱离或转移 leader
  -> 同事务确保个人 Party
  -> kind=4 内部恢复使原生客户端回到 1 人 lobby Session
```

Party state 不能单独创建游戏原生个人大厅；kind=4 是补齐原生 lobby 的内部桥。恢复过程中可以有极短过渡，但最终 authoritative roster 不能是 0 人。恢复事件必须按接收者和 generation 精确消费，不能用 party_id 一次性清空仍被其他成员需要的事件。

普通邀请与 kind=4 恢复不能同时驱动原生 `AcceptInvite`。快速比赛成员离开后，若本次已接受的普通邀请尚未完成 `JoinSession`，延迟恢复必须绑定该 invite ID 与目标 Session 并继续等待；目标成员关系建立后结束恢复，目标失效或过期后才释放一次个人恢复。不能仅凭“玩家当前属于某个大厅”判断完成，因为旧大厅成员关系可在原生切换期间暂存。该竞态已由三机日志确认，并由 Dedicated 自动回归覆盖成功加入、目标失效、历史大厅和私人房恢复；修复后真实快速匹配邀请切换仍待复测。【多机实测、线上日志、当前源码、自动测试】

### 3.8 Hooks 生命周期

```text
游戏加载 uplay_r1_loader.dll
  -> DllMain PROCESS_ATTACH
  -> 检查 SCBL_CLIENT_DATA_DIR；没有则 retail passthrough
  -> 校验 orig DLL、加载配置、解析地址
  -> 安装功能 Hooks、FOV、音频、可选玩法 DLL
  -> login-thread + updates thread
  -> DX9/DX11 Hudhook 初始化与 Receiver 消费
  -> PROCESS_DETACH 逆序停用/恢复补丁
```

当前 `DllMain` 仍执行文件 I/O、日志、配置、Hooks、插件和线程创建；这是已知 loader-lock 风险，不是可随手整理的普通代码。迁移必须保持导出、原始 DLL 转发、登录时机、DX 初始化和卸载语义，并按阶段实机验证。当前状态只允许记录风险，不允许为消除静态告警大改。【当前源码、静态分析】

Dedicated 每次进程启动执行 `Storage::invalidate_sessions`，清除 Party、Party members、station URLs、user sessions，将仍活跃的 Game Session 标为结束并把用户设为离线。这是当前“服务重启后客户端重新认证并重建临时状态”的业务策略，不是可按零引用清理的缓存代码；改变它会直接影响重启后的 Party/邀请/迁主恢复语义。【当前源码、自动测试；替代策略未确认】

## 4. 当前已完成的通用与 MOD 能力

### 4.1 通用核心（Official 与 MOD）

| 能力 | 当前实现 | 证据 |
|---|---|---|
| Uplay R1 兼容 | 转发原始导出并补充账号、好友、邀请、Party、存档、Overlay | 多机实测、自动测试 |
| FOV | 1.0–1.3，默认/回退 1.2，有限时间监控 Fusion 模块 | 自动测试、静态反编译；3.0 实机待复核 |
| 声音 | 静态内置真实 XAudio2.7 ABI 兼容层；外置 DLL 已废弃 | 当前源码、自动测试；多声卡实机待复核 |
| Overlay | 连接错误、通知、邀请、F5 接受、Official/MOD 标签 | 多机实测、自动测试 |
| 网络错误恢复 | 成功空轮询清错；连接失败有界重连 | 线上日志、自动测试 |
| 房间弹窗恢复 | 仅对已识别的“与主持端连接中断”默认动作做边沿重试 | 多机历史实测、自动测试 |
| 迁主桥 | 保留原生事件，补齐缺失的 non-host continue/platform record，所有写入有精确 guard | 多机实测、静态反编译、自动测试 |

### 4.2 MOD 玩法 DLL 当前实际接线

`client/hooks/gameplay/src/lib.rs` 在 ABI 校验通过后初始化以下四组能力；任一组有自己的地址/字节校验和恢复路径。

| 能力 | 行为 | 证据 |
|---|---|---|
| 情报衣 | 已有标记不被破坏清除；技能释放仍受破坏压制；同一已有标记不重复刷新 | 多机实测、自动测试、静态反编译 |
| 超载衣 | `UMPArmorAbilitySaboteur` 启动/蓄力中不因破坏取消；已处于压制时仍不能起手；原版命中破坏者的清空/短限制效果不改 | 多机实测、自动测试、静态反编译 |
| 佣兵奔跑视角 | 奔跑时解除特定相机校正限制 | 单机/多机历史实测、静态反编译 |
| 扩展装备 | 两类装备各六套投影；额外数据写有界、版本化、原子 sidecar | 自动测试、历史实机；3.0 双模式存档待复核 |
| 12 人容量 | lobby 与 Party 容量补丁改为 12，签名不匹配时整组失败关闭 | 自动测试；12 人实机未完成 |
| 枪口遮挡 | 间谍武器射线由真实枪口出发并按遮挡裁剪，不能借相机越墙 | 静态反编译、自动测试、历史实机 |
| 分层网络复制 | MOD 下玩家运动 20 Hz、武器状态 10 Hz；移动物体目标 10 Hz 仅作明确设计值，实际只补丁两处生产间隔 | 自动测试、静态反编译；实机频率探针待 3.0 复核 |

未在当前玩法 DLL 接线的设计，不得写成已实现：体力条 UI、静音鞋、速度鞋动作恢复、BMS/RFD/ATS 重写、侦测头盔 EMP 视觉免疫、分身阻挡裤新能力、枪声系统扩展等仍属于设计或失败实验。

## 5. MOD 逆向工程知识账本

### 5.1 版本与渲染边界

- 以下绝对地址/偏移来自当前支持的 32 位零售 `Blacklist_DX11_game.exe`。首要版本锚点是地址解析结果中的 `func_world_raycast == 0x00589860`，不是“文件名看起来相同”。【静态反编译】
- Gameplay 功能大量使用 DX11 固定地址；DX9 即使核心 Hooks/Overlay 可工作，也不能推定 MOD 玩法地址相同。未知版本应拒绝补丁并保持原版行为。【当前源码、自动测试】
- 每个内存补丁都必须先比较期望字节、全部写入后验证；中途失败按逆序回滚。禁止“地址大概对”就写。

### 5.2 已确认类、函数、字段与 Hook 点

#### 情报衣与标记

| 对象/符号 | 地址或偏移 | 已知语义 | 成熟度 |
|---|---:|---|---|
| Intel ability activate | `0x00ED9FB0` | 情报衣窗口开始 | 静态反编译、多机实测 |
| Intel ability deactivate | `0x00E6DB00` | 窗口结束；最后一个实例结束才恢复补丁 | 静态反编译、自动测试 |
| player mark add / clear | `0x00B5EA00` / `0x00B5ED10` | 标记写入与清除 | 静态反编译、多机实测 |
| mark entries/count/capacity | `+0x1A90/+0x1A94/+0x1A98` | 标记记录向量与有界遍历 | 静态反编译、自动测试 |
| mark flags/presentation | `+0x1A08/+0x1A9C` | active 标志与通知上下文 | 静态反编译 |
| eligibility/apply/clear branches | `0x00AD8607`、`0x00B930B2`、`0x00B92F60` | 仅在 Intel 窗口内绕过破坏清理和重复应用分支 | 静态反编译、多机实测 |

多实例情报衣必须按活动对象集合计数；不能让一个对象结束就恢复全局分支，否则另一名玩家的能力会中途失效。

#### 超载衣

| 对象/符号 | 地址或偏移 | 已知语义 | 成熟度 |
|---|---:|---|---|
| 类 | `UMPArmorAbilitySaboteur` | 游戏内 Overcharge 实际能力类 | 静态反编译、多机实测 |
| vtable | `0x02A3F3A4` | 排除相邻护甲能力 | 静态反编译 |
| activate/deactivate | `0x00E6CC40/0x00E6CD60` | 护甲能力生命周期 | 静态反编译 |
| update | `0x00E99DB0` | 蓄力条、状态与取消判断所在更新 | 静态反编译、多机实测 |
| disruption cancel branch | `0x00E99DDF` | 只绕过“蓄力中被破坏取消” | 静态反编译、多机实测 |
| flags/owner/charge | `+0x40/+0x6C/+0x7C` | active/casting、持有者、技能条 | 静态反编译 |
| phase timer/state | `+0xC0/+0xC4` | 一次性技能蓄力阶段 | 静态反编译 |
| owner disruption flags | owner `+0x78` | 已处于压制状态仍禁止起手 | 静态反编译、多机实测 |

成功方案是初始化时一次修改精确取消分支并 Hook 生命周期/更新用于状态保护，不是每帧持续改写全部技能字段。

#### 佣兵奔跑视角

| 符号 | 地址/偏移 | 语义 | 成熟度 |
|---|---:|---|---|
| sprint enter | `0x015A1640` | 进入奔跑状态 | 静态反编译 |
| camera correction | `0x00DAFD90` | 原版奔跑相机锁定/校正 | 静态反编译、实机 |
| sprint pawn / net role | `+0x48/+0x4D` | 只对佣兵角色应用 | 静态反编译 |

#### 枪口遮挡

| 符号 | 地址/偏移 | 语义 | 成熟度 |
|---|---:|---|---|
| weapon trace dispatch | `0x00E2B940` | 武器射线入口 | 静态反编译 |
| muzzle position | `0x00DFFDA0` | 骨骼与武器偏移后的真实枪口位置 | 静态反编译 |
| filtered raycast | `0x005ABC80` | 排除持有者层级的同步世界查询 | 静态反编译 |
| pawn gameplay flags | `+0x374` | Spy bit 18、Merc bit 19 | 静态反编译、自动测试 |

只对间谍武器射线应用。相机射线先决定瞄准目标，枪口射线再判断是否被近处墙体截断；不能把所有世界射线统一改成枪口起点。

#### 扩展装备

| 符号 | 地址/偏移 | 语义 | 成熟度 |
|---|---:|---|---|
| manager serialize/query | `0x00F03CF0/0x00EE56F0` | 原生保存视图与按类型查询 | 静态反编译、历史实机 |
| loadout vector | `+0x20/+0x24/+0x28` | entries/count/capacity | 静态反编译 |
| ID-index cache | `+0x64/+0x68/+0x6C` | 零售 Serialize 使用的缓存；`+0x10` 不是该缓存 | 静态反编译、已推翻旧偏移 |
| gear | entry `+0x50` | 装备内容 | 静态反编译 |
| sidecar | magic `SCBLLDO1`、version 1、6 records、上限 64 KiB | 与原生存档格式解耦的附加数据 | 自动测试、历史实机 |

#### 分层网络复制

| 层 | 当前目标 | 补丁点 | 成熟度 |
|---|---:|---:|---|
| Player Motion | 20 Hz / 50 ms | `0x00FD9B51` 的 interval operand | 静态反编译、自动测试 |
| Weapon Status | 10 Hz / 100 ms | `0x00FD9A4C` 的 interval operand | 静态反编译、自动测试 |
| 开火/停火/命中等 | 原生事件路径 | 不由上述两个轮询间隔控制 | 静态调用链、历史探针 |

“即时事件”表示状态变化发生时入队，不等待 100/200 ms 快照生产周期；它仍受游戏帧、队列、传输和对端处理延迟影响，但不存在一个可安全统一翻倍的全局 tick。`ProductImplNetworkStep` 每帧多调用几次不能保证生产器生成新状态，反而可能重复泵送和改变时序。【静态反编译、已推翻】

#### 迁主 Storm 状态

当前 DX11 证据模型：

- HostMigration update：flags `+0x04/+0x05/+0x06`，generation `+0x34`，local participant `+0x3C`，migration host `+0x1B0`，retry `+0x278/+0x27C`，terminal `+0x284`。
- Game state：workflow `+0x610`，generation `+0x550`，local participant `+0x55C`，platform role/index `+0x5F0`，pending operation `+0x618`。
- workflow：phase `+0x04`，participants `+0x20/+0x24/+0x30/+0x34`，flags `+0x28/+0x29/+0x2C`。
- participant：ID `+0xBC`，状态 `+0xB4`，ack `+0xC1`。
- platform interface：session key `+0x40C`，状态 `+0x418`，flags `+0x428..+0x42C`，record `+0x430`。

这些偏移只用于有界读取和精确 guard；不能当作稳定 SDK。若对象为空、generation 不唯一、participant 不 ready 或身份不匹配，必须走原生或失败关闭。【静态反编译、多机实测】

#### 原生断线弹窗

- controller message list/count：`+0x350/+0x354`，entry size `0x84`。
- message kind/buttons/callback/token：`+0x1C/+0x64/+0x68/+0x7C`。
- DX11 scene update/add/remove/action：`0x019D1D10/0x019C5370/0x019C53E0/0x01910220`。
- 只识别零售 lost-connection vtable `0x029A0364` 且复用默认 action；controller transition active 时不派发。

该修复的目标是让正常提示可确认，不是屏蔽“你连接到主持端的连线已中断”。【静态反编译、自动测试、多机历史实测】

### 5.3 成功实验的共同条件

1. 先闭合“玩家名称 -> 实际对象/类 -> 调用点 -> 字段/分支 -> 排除相邻对象”。
2. 用小窗口反汇编确认调用约定和邻近控制流，再写 Hook；不要从字符串直接跳到行为修改。
3. 地址和 expected bytes 同时匹配才写；任一不匹配整组失败关闭。
4. 先证明目标效果，再逐步加角色、模式、对象类型和版本 guard；每一步都保留可回滚点。
5. 诊断只记录类型、阶段、计数和脱敏 ID，不记录 payload、真实 IP、账号或凭据。
6. 单机对象字段确认后，涉及 Party、迁主、标记共享或远端效果必须做双机/三机实测。

## 6. 已失败、无效或禁止原样重试的路线

| 尝试 | 失败表现 | 已排除原因 | 结论 |
|---|---|---|---|
| 把 Enhanced Detection 头盔当作 `MPArmorHelmetTech` | EMP 噪点不消失、UI 阶段反转、间谍也被影响 | 对象身份错误；`MPArmorHelmetTech` 是相邻护甲头盔，不是游戏内 Enhanced Detection | 已推翻；必须重新闭合真实装备对象后才能继续 |
| 在 EMP 状态、UI 或普通后处理分支逐个归零 | 电流首段变化但暗点/噪点持续，或禁用 UI 一并消失 | 目标视觉是独立屏幕材质/后处理层，未找到真实 owner；状态与表现不是同一开关 | 已推翻；禁止继续在旧候选地址叠加补丁 |
| 对 EMP 视觉做大范围持续写 | 多次无效并引入双机闪退 | 写入生命周期和对象归属不明确 | 已推翻；只有定位真实材质实例/生命周期后才能重启 |
| 把所有网络刷新归结为一个统一 tick 并把循环 2 次改 4 次 | 不能让 100/200 ms 生产器自动产生新快照，可能重复泵送 | 状态生产与网络泵是分层时钟 | 已推翻；使用按生产器分层目标和实际频率探针 |
| 以 32 Hz 作为全部状态统一目标 | 只能解释部分 motion，武器与事件路径不同 | 老推论把多个层混在一起 | 已推翻；当前配置是 20 Hz motion + 10 Hz weapon |
| 在同一进程同时保留内置与独立音频路径 | 启动/切换设备时访问冲突或重复生命周期 | ABI、初始化和卸载所有者重复 | 已推翻；3.0 只保留静态内置 XAudio2.7 路径 |
| 用错误 XAudio2.7 vtable/系统接口声明 | `XAudio2_7.dll` 固定位置访问冲突，或修后无声 | 2.7 与 2.8/2.9 ABI、COM 创建和 mastering voice 签名不同 | 已推翻；必须使用仓库内 June 2010 2.7 声明与真实 ABI |
| 热重载子插件从 Desktop/ambient 环境自动发现 | 核心 DLL 被版本门禁拒绝后，子 runtime 仍从旧目录加载，形成跨代混装 | core 与 child 没有原子 generation | 已废弃；3.0 删除玩法子 runtime/热重载/诊断桥，只加载同目录唯一 gameplay DLL |
| 为迁主构造邀请式 15 秒回大厅 | 迁主循环、0 人大厅、会话不存在、多人分裂 | 邀请不能代替 Storm 原生迁主和 Party+Session 原子关系 | 已推翻；原生迁主优先，N-1 只做服务端保底 |
| 把 Party 列表简化成“本机+最近邀请对象” | 三人以上成员丢失、队长/同队判断错误 | 兼容投影不是 authoritative roster | 已推翻；始终投影 Dedicated 完整 roster，最大成员数有界 |
| 把在线 heartbeat TTL 直接当模式断言 TTL | 控制面短断时游戏和 Dedicated 仍在线，但邀请暂时提示无法确认好友模式；恢复 heartbeat 后自行正常 | 在线活跃度与已认证模式断言是两种寿命，短 TTL 同时删除二者造成误拒绝 | 已确认缺陷模型；保持模式门禁，拆分短在线 TTL 与有限模式 grace |
| 仅凭仓库内零引用删除 Hook、导出、回调或协议 handler | 静态搜索看似无调用，但真实游戏可能通过导出、vtable、宏注册或网络 protocol/method 进入 | 游戏加载入口和协议调度不一定表现为普通 Rust 调用引用 | 禁止作为单一删除证据；必须同时证明导出/注册/宏展开/实机可达性 |
| 把诊断 Hook 当成功能必需 Hook | 不支持的游戏 build 会因纯诊断地址失败而影响正常启动，正式局还承担无关日志成本 | Goal/State/NetResult/RMC 观察点不参与产品动作语义 | 已收敛为 optional/opt-in；核心 settings 与动作路径不得只为取证而 detour |
| 全量物化大区段反汇编生成器 | Python/Capstone 提交约 55 GB，整机资源耗尽 | `list(md.disasm(...))` 保存了全量指令对象 | 已推翻；必须流式/分块、固定 deque、内存和时间上限，命中后只读小窗口 |

“已推翻”只否定原样方案，不否定目标需求。重新启动相同目标前必须先得到新的对象或调用链证据。

## 7. 2.0 到 3.0 的行为迁移

### 7.1 原样继承的成熟路径

| 路径 | 3.0 状态 | 说明 |
|---|---|---|
| Uplay R1 导出和原始 DLL 转发 | 保留 | ABI 不能因重构改名或改变调用约定 |
| 邀请 kind、Party/Session 双关系 | 保留 | 2.0 多轮多人修复的核心业务 |
| Uplay GameSession 精确 496 字节上下文 | 保留 | 私人邀请的原生 accepted event 依赖 |
| Set/Clear generation 串行 | 保留 | 最新状态胜出，重登录不回退 |
| 原生迁主 + N-1 恢复 | 保留 | 3.0 当前仍在实机复核复杂后加入场景 |
| 网络复制按生产器分层 | 保留并调整为当前 20/10 Hz | 不恢复旧“统一 32 Hz”推论 |
| Gameplay 反编译地址和字段 | 有选择保留 | 仅匹配当前 DX11 build 时启用 |

### 7.2 主动重设计

| 项目 | 3.0 设计 | 业务原因 |
|---|---|---|
| 身份 | 删除演示账号/默认凭据；玩家 1000+，基础设施主体独立 | 避免玩家 PID 与认证/Tracking 服务冲突 |
| 模式 | 只接受 Official/MOD | 3.0 不背负旧 Casual/Serious 兼容歧义 |
| MOD 开关 | DLL 加载即总开关，已完成能力默认全开 | 降低逐项配置和跨端组合爆炸 |
| 存档 | Official/MOD 分目录，槽位和格式不变 | 防止扩展装备写入覆盖官方进度 |
| 音频 | 唯一静态内置 XAudio2.7 | 消除外置 DLL 和双生命周期 |
| Quazal | 未知类、Route/Raw、非法长度统一错误返回 | 网络输入不能触发 panic |
| 协议生成 | 删除 21 份未使用客户端半边和无 transport `ClientProtocol` | Dedicated 只需要服务端 dispatcher；服务端 method 逐项保留 |

### 7.2.1 安全整理中确认不改变业务的边界

| 整理项 | 当前运行边界 | 为什么不改变正常游戏行为 |
|---|---|---|
| `Any` 未知多态 class | `ClassRegistry` 找不到类型时转为 `FromStreamError::UnknownClass` | 只改变非法/不受支持输入的失败方式：从 panic 变为当前请求失败；已注册 class 的解析与 handler 不变 |
| 空玩家统计请求 | `requested_player_pid` 对空 `player_pids` 返回 `ParsingError` | 合法请求仍取第一个目标；远端空列表不再触发 `unwrap` panic |
| NAT traversal 发送失败 | `send_request` 的 I/O 失败记录并返回，由当前请求失败关闭 | 不改变成功 NAT 请求的地址、packet 或响应；网络瞬断不能终止 Dedicated |
| PRUDP 重复请求重放抽成 helper | 按原 request sequence 读取有界 response cache，发送原 packet/fragment/response sequence，不再次调用 RMC handler | 保持可靠 UDP 幂等语义；防止重复写 Party、邀请、Session 或统计事务 |
| `InvitedGameSession.sender_id` 删除 | 结果对象只携带 `invite_id`、`kind`、`session`；发送者身份仍由权威 `Invite.sender` 和 accepted event 路径提供 | 被删字段在运行路径零读取，不改变邀请来源、目标 Session 或玩家显示所需身份 |
| 三个观察接口收为 `cfg(test)` | `find_invitable_sessions_async`、`find_active_session_creator_async`、`manual_invitation_search_is_active` 只供测试断言 | 运行时继续走 `find_invitable_session_candidates_async`、`find_active_session_invite_target_async` 和真实 search intent；没有删除产品决策入口 |
| 生成协议客户端半边整体删除 | Dedicated 继续注册全部服务端 protocol/method dispatcher 和 response 编码 | 无真实 transport 的 client stub 从未发送游戏请求；删除时按生成器、类型、import 一次收口，不能连带服务端 handler |

以上“确认不改变”只覆盖静态调用链和自动回归。它不免除本章第 10 节的登录、邀请、Party、Session、迁主与快速匹配实机门禁。

### 7.3 已废弃

- Casual/Serious、欢乐/严肃模式和兼容映射。
- 演示账号、固定密码、固定 account ID、样例会话数据。
- 外置 `scbl_audio.dll`、`scbl_mod_runtime.dll`、诊断热重载 DLL 和 ambient Desktop 插件发现。
- 玩法逐项 feature flags 作为玩家运行开关。
- 生成协议的客户端发送半边、无 transport 的默认 `ClientProtocol::send`。
- 以旧房主陈旧发布、邀请 sender 最近值或本地两人列表作为当前房间真相。

### 7.4 暂留兼容和风险债务

- `uplay_r1_loader.orig.dll`、转发导出、Uplay R1 数据布局必须保留。
- `DllMain` 内完整初始化/清理仍保留，属于待真实游戏门禁后渐进迁移的风险。
- Overlay runtime 与 presentation 已有 typed mode contract，但 `overlay.rs` 仍包含运行时和一部分呈现接线；不能大拆重写。
- 原生弹窗恢复和大量 migration patch 是 DX11 精确地址；DX9 必须单独证明。
- MOD 命名空间不自动复制 Official 存档。旧无命名空间存档归 Official；MOD 首次无文件应显示为空，而不是偷偷回读/写回 Official。

## 8. 不可随意简化的工程边界

### 8.1 ABI 与导出

- `DllMain` 必须保持 `extern "system"` 与 Windows reason 语义。
- Uplay R1 `#[forwardable_export]` 的名称、参数、返回值、所有权和 overlapped 完成方式不能改。
- Gameplay 插件只认 `HostApiV1` / `PluginConfigV1` 的 `abi_version`、`struct_size`；新增字段只能遵守向后兼容布局。
- `scbl_plugin_initialize/shutdown/handle_event` 的调用约定和卸载次序不能改。
- XAudio2.7 必须使用正确 2.7 COM/vtable/voice ABI，不能用 2.8/2.9 头文件近似替代。

### 8.2 线程与 loader lock

- 不在 `DllMain` 卸载中等待可能需要 loader lock 的 worker。
- Receiver 只能在其所有线程按当前时序排空；不能把游戏动作从 render/game thread 任意搬到后台。
- Set/Clear 发布用单一异步锁和 generation；不能恢复多 `spawn` 竞态。
- 音频、Fusion FOV 和登录 worker 都需要显式停止信号或进程生命周期设计。

### 8.3 Hudhook 与 Overlay

- DX9 与 DX11 使用不同 Hudhook backend，但共享 typed state/command 语义。
- runtime 持有 engine 检测、初始化、Receiver、API 动作和 fail-closed；view 持有布局、文案、计时和输入呈现。
- F5 接受使用稳定 invite ID；UI 不得持有 `GameSession` 指针或数组 index。
- 错误卡只有成功 poll 才清除，不能由 UI timeout 自行隐藏。

### 8.4 PRUDP/RMC

- PRUDP 序号、fragment、signature、ACK 和重复请求缓存重放不可在“精简”时改变。
- 重复 Data request 命中缓存后必须重放原响应 packet；不能增加 server sequence，不能再次调用 handler，不能重新执行数据库写事务。
- Route/Raw 即使可解析，也只能返回/丢弃受控错误，不能 `todo!` 或 panic。
- RMC 未知 protocol/method/class、非法长度和 Any 解码失败必须返回稳定错误。
- Secure Register 的宽松 URL 解码只能局限于 method 1/4 请求字段，不能放宽全局 `StationURL`。
- 服务端 method handler 必须保留；删除生成 client half 不等于可以删除 server dispatcher。

### 8.5 邀请、Party 与迁主

- invite generation 与本地显示 ID 是两层，不能直接复用数据库 ID。
- Party 与 Lobby/Game Session 是双关系；任何退出/迁主事务必须同时说明两边结果。
- 私人、公共、Party follow、内部恢复是独立 lane，不能互相消费。
- kind=4 恢复必须按 receiver/generation 精确消费；party_id 级批量清除可能误删其他成员。
- 迁主中的 generation、participant、平台身份和 session creator 必须一致；服务端“改 creator”不能替代客户端 Storm 完成。
- N-1 恢复必须保留原 Party 边界。快速匹配中不能把不相关 Party 合并。
- Dedicated 启动期 `invalidate_sessions` 当前把 transient Party/Session/presence 一次失效；除非另有持久恢复协议和实机证据，不能改成保留半套旧状态。

## 9. 已确认故障模型：后邀请成员重返 Game Session、未重返 Party Session

### 9.1 场景

用角色 A/B/C 表示三名玩家：

1. A 是私人比赛原房主。
2. C 曾离开，原生 Party/Lobby Session 只剩 A+B。
3. C 经私人邀请直接重返同一 Game Session；Dedicated 持久 Party roster 已再次包含 A+B+C，但原生 Party/Lobby Session 仍只有 A+B。
4. A 退出，服务端对 lobby 与 room 成员取并集，准备把 A+B+C 恢复到共享大厅，并为“只在 room、不在 lobby”的 C 排队 kind=4 恢复。
5. B 先完成原生 `SplitSession` 并成为新 creator。
6. 迁主成功清理如果按 `party_id` 消费全部未完成 kind=4，会把 C 仍需要的恢复事件一起删除。
7. B 能继续，C 收不到恢复，原生状态进入错误/清空 native session，最终显示 0 人大厅；Hooks authoritative Party roster 仍可能显示 B+C，形成表面矛盾。

### 9.2 首个确定失败节点

- 生产恢复：`server/dedicated-server/src/storage/parties.rs::restore_published_lobby_party_async` 正确识别 late member 并创建 kind=4。
- 迁主：`server/dedicated-server/src/storage/sessions.rs::migrate_game_session_host` 在成功 Split 后清理旧恢复。
- 故障模型：清理范围若是整个 Party，就会误删其他 receiver 的 kind=4；正确边界只能淘汰新房主自己的陈旧恢复。

当前源码已由 `cancel_private_room_lobby_restore_for_member_async` 将清理范围收窄到迁主调用者，并固定“原房主与继任者已在 Party、晚加入成员仅在私人 Game Session”的自动回归；Dedicated 全量回归通过并已部署独立测试服。该缺陷在源码和自动验证层已修复，但仍缺同一三机拓扑复测，不能写成实机闭环。【线上日志、静态调用链、自动测试；修后多机实测缺失】

### 9.3 必须固定的回归

1. A+B 在 lobby，C 仅经私人邀请加入 Game Session；A 退出后 B 成功 Split，C 的 kind=4 仍可投递。
2. B 自己的陈旧 kind=4 被消费，不能重复加入自己。
3. Party 其他成员的 kind=4 不被新 host 清理。
4. C 接受恢复后进入与 B 相同 lobby、Party roster 与 native participants 都是 B+C。
5. 重复投递、超时或 C 已离线时幂等，不创建第二个 Party/Session generation。
6. 快速匹配多个 Party 的恢复不受影响。

## 10. 可重复实机验证矩阵

### 10.1 每次候选的最小矩阵

| 维度 | 场景 | 成功条件 |
|---|---|---|
| 启动 | DX11 Official | 启动、声音、Overlay、Online、无 gameplay DLL |
| 启动 | DX11 MOD | gameplay DLL 加载、标签 MOD、所有当前功能初始化 |
| 启动 | DX9 Official | 注入、Overlay、Online、卸载正常 |
| 模式 | 缺失/未知 GameplayMode | Hooks 失败关闭 Official，Dedicated 不跨模式 |
| 存档 | Official -> MOD -> Official | 两边各回到自己的最后存档，槽位不覆盖 |
| FOV | 1.0/1.2/1.3/非法 | 生效或回到 1.2，不写玩家 Fusion INI |
| 音频 | 双声卡切换、拔插、失焦/回焦 | 不闪退、不绑死旧设备、失焦策略一致、声音恢复 |

### 10.2 双机邀请与在线

1. A 先上线，B 后上线；A 刷新后能看到并邀请 B。
2. B 强退再上线；在线状态收敛后能收到新邀请，不重放过期邀请。
3. 房主邀请、队员邀请分别加入同一房间；邀请卡显示实际发送者，但目标指向当前有效房主 Session。
4. 同一发送者重复邀请、同队两人同时邀请同一接收者、两个不同队伍同时邀请；F5 快按只能提交一个 Join transaction。
5. 加入失败后立即可接受其他邀请；不等待上一条 30 秒自然过期。
6. 加入成功后清除同一 generation 的等价邀请，不清除无关发送者的有效邀请。

### 10.3 三机及以上 Party/Session

1. A+B 先组队进入私人房，C 后邀请加入；房主“和队伍一起离开”时三人回同一 Party lobby。
2. C 单独退出私人房/比赛，回个人 1 人大厅；原房间成员邀请 C 可再次加入。
3. A 正常退出与 A 强退各做一次；B/C 原生迁主成功则比赛继续，失败才进入同一 N-1 恢复。
4. 执行本文件第 9 章 late member 场景，确认 kind=4 不误删。
5. 快速匹配中放入两个独立 Party；某 Party 队长“和队伍一起离开”只带本队，其他 Party 留在比赛。
6. 快速匹配中 Party 队长单独离开，队长身份只在该 Party 内转移；房间 creator 不被无关 Party 覆盖。
7. 12 人容量需分别验证 4、8、12 人的邀请、Party roster、Session participants、迁主和退出，不能只看大厅显示数。

### 10.4 故障与恢复

1. Event poll 连续失败 -> 显示错误；首个 `Ok(None)` -> 立即清错。
2. EasyTier 短断恢复时账号、Party 和 Session 不重复创建。
3. 房主断线期间 invitation generation 不指向陈旧发布。
4. lost-connection 原生弹窗在首次和第二次出现都能确认；确认不闪退、不吞正常错误语义。
5. Dedicated 必需服务线程退出时进程失败退出并由 systemd 重启；不能只记日志后保持 active。
6. 仅阻断 Launcher -> Control Plane heartbeat，游戏到 Dedicated 保持在线：短在线 TTL 到期后好友在线显示可变化，但最近一次已认证模式断言在有限 grace 内仍允许同模式邀请；超过 grace 后明确失败关闭。
7. heartbeat 在 grace 内恢复：新模式断言立即覆盖旧状态，同一目标无需重启即可邀请；明确模式不一致、完整性失败或从未有过有效断言始终拒绝。
8. 对齐三条时间线：发送者邀请结果、目标 Launcher heartbeat 失败/恢复、服务端 mode snapshot 年龄；不能只凭最终“又能邀请了”判断修复完成。

### 10.5 MOD 玩法

1. 情报衣：标记存活、重复不刷新、技能起手仍受压制。
2. 超载衣：正常蓄力、蓄力中进入破坏、已在破坏内起手、命中破坏者四组对照。
3. 枪口遮挡：左右墙角、烟雾、相机过墙、佣兵武器对照。
4. 扩展装备：六套保存/重启/切模式/删档/损坏 sidecar。
5. 网络复制：按类型统计产生次数、序列化大小、排队时间和无变化跳过；不要用感受推导 Hz。

### 10.6 3.0 安全整理的业务回归

1. 在全新数据库连续注册多个玩家：首个玩家从 1000 起，跨过保留服务主体；每名玩家均完成 LoginEx、Tracking、票据、Register、Online 和 Party/Game Session 建立。
2. 已有真实玩家数据库执行迁移：真实账号、Party/Session 引用和 endpoint 不因演示数据清理被删除；仅无真实引用的样例种子消失。
3. 发送未知 `Any` class、非法 collection length、空 player list、Route/Raw，并模拟 NAT send failure：单请求失败或丢弃，Dedicated 进程继续服务，下一条合法登录/邀请仍成功。
4. 重发相同 PRUDP Data request：收到原响应 sequence/fragment；handler 只执行一次，下一条新请求使用连续的新 response sequence。
5. 房主和队员分别发起邀请：玩家显示的发送者、目标 Session 与当前 host 均正确，证明删除结果对象冗余 sender 字段没有改变权威身份来源。
6. 私人房、快速匹配和迁主分别触发候选选择、active target 与 manual search intent，证明产品运行路径没有依赖三个 `cfg(test)` 观察接口。
7. Dedicated 重启后旧 transient Session 不被误发现或邀请；客户端重新认证后重建个人 Party/lobby，再完成双机邀请和加入。
8. 运行一次本文件第 9 章 late member 回归和第 10.3 节快速匹配多 Party 回归，确认安全整理未改变 Party+Session 双关系。

## 11. 诊断入口与取证方法

### 11.1 客户端

- Launcher：`data/<计算机名>/<Windows用户名>/logs/scbl-public.log`
- 游戏：`data/<计算机名>/<Windows用户名>/logs/game/bl-tracing.log`
- Hooks Party：`data/<计算机名>/<Windows用户名>/logs/game/hooks-party-trace.log`
- 脱敏包：同一用户数据树的 `diagnostics/`

优先关键词：

- 登录：`AcquireTicket`、`Register`、`StateLoggedIn`、`StateOnline`、`StateOffline`
- 邀请：`InviteEventDelivered`、`InviteAccept`、`FriendsGameInviteAccepted`、`PartyGameInviteAccepted`
- Party：`authoritative_roster`、`PartyMemberListChanged`、`PartyPresenceProof`
- Session：`CreateSession`、`JoinSession`、`LeaveSession`、`AbandonSession`、`SplitSession`
- 迁主：`SCBL_MIGRATION_`、`HostLeaveAwaitingNativeMigration`
- 弹窗：`NativeDialogRecovery`
- 模式/心跳：`POST /v1/heartbeat`、`Control plane request recovered`、`player mode state unavailable`
- 声音/FOV/玩法：`SCBL_AUDIO_`、`SCBL_FUSION_COMPAT`、`SCBL_GAMEPLAY_`

### 11.2 服务端

- 先以客户端本地时间换算 UTC，限定 journal 窗口；不要全量导出长期日志。
- 对齐 Dedicated protocol log、Control Plane、EasyTier 服务状态，再查同窗口 SQLite 的脱敏 Party/Session/participants/invites。
- 模式误拒绝需额外对齐目标 Launcher 最后一次成功 heartbeat、Control Plane active-client 清理、mode snapshot 的条目年龄和 Dedicated 的拒绝分类。报告只写相对时间、模式和年龄，不写用户名、真实地址或原始心跳 payload。
- 数据库只记录数值 ID、kind、generation、creator/leader 和 consumed/accepted 状态；报告中不要输出用户名、token、密码、真实 IP、原始 payload。
- 迁主重点关键词：`HostLeavePreservedForMigration`、`PrivateRoomPartyReturnedWithHost`、`NativePrivatePartyLeadershipTransferred`、`SplitSessionRestoredAbandonedLobby`、`DepartedMemberLobbyRestoreQueued`。

### 11.3 逆向工具安全

- 大范围搜索用于先定位模块、字符串、引用和调用簇；命中后只反汇编地址附近小窗口。
- Capstone 等生成器必须流式/分块，只保留固定大小 deque；为进程设置内存和时间上限。
- 不对整个 EXE 或大 section 使用 `list(disasm_generator)`。
- 固定地址只能作为当前 build 证据；后续游戏更新应通过签名、调用者、字段一致性和实机行为重新定位。

## 12. 游戏更新后的重新定位流程

1. 先识别新 EXE 的 PE 架构、渲染后端和关键稳定函数签名。
2. 从稳定业务锚点重新找调用簇：能力类 vtable、原生事件名称、序列化入口、world raycast、LobbySettings、SceneMessageBox、Storm migration。
3. 比较旧/新函数的控制流与调用约定，不直接平移 RVA。
4. 对字段做至少两个独立访问者交叉验证；一个函数中的常量偏移不足以定义字段。
5. 先启用只读探针，确认对象身份、生命周期和调用次数；再做一次性、可回滚补丁。
6. DX9/DX11 分别验证。DX11 地址成功不能证明 DX9。
7. 自动测试固定 expected bytes、回滚和 fail-closed；实机矩阵再证明玩家行为。
8. 旧地址不匹配时保持原版，不搜索“最近的相似地址”后盲写。

## 13. 术语表

| 术语 | 本文含义 |
|---|---|
| Party | 持久社交队伍；有 leader 和 members |
| Lobby Session | 原生大厅传输 Session，常见属性 `113=>1` |
| Game Session | 私人或快速比赛 Session |
| creator / host | 当前 Session 传输房主，不必等于所有 Party 的 leader |
| Party leader | 某一 Party 的队长；快速匹配可同时存在多个 |
| Uplay GameSession | 游戏通过 Uplay R1 发布的原生邀请上下文，不是数据库 Session 行本身 |
| generation | 一次邀请、发布或迁主状态的代际，用于拒绝陈旧事件 |
| kind=4 | SCBL 内部 lobby/Party 恢复邀请，不显示给玩家 |
| SplitSession | 游戏原生迁主/拆分调用；已认证调用者通常是新 host |
| N-1 | 房主离开后剩余玩家的保底恢复；不替代原生迁主 |
| authoritative roster | Dedicated 当前 Party 真相，由 Hooks 投影给游戏 |
| native participants | Quazal Session 参与者；可能暂时不同于 Party roster |
| fail-closed | 证据、身份、地址或状态不完整时拒绝修改/加入，而不是猜测继续 |
| Official | 联机核心和通用技术修复，不加载玩法 DLL |
| MOD | 加载唯一玩法 DLL并启用当前已完成玩法 |

## 14. 当前已知风险与未完成项

| 项目 | 当前状态 | 下一证据 |
|---|---|---|
| late member kind=4 被迁主清理 | 源码与自动回归已修复并部署测试服；尚未实机闭环 | 三机重复第 9 章场景并对齐 server/client |
| DllMain loader lock | 风险确认，尚未迁移 | 分阶段 attach/detach 设计 + DX9/DX11 启停实机 |
| MOD DX9 | 大量 gameplay 地址为 DX11 固定值 | DX9 地址图、失败关闭和实机 |
| 12 人容量 | 自动测试通过 | 12 人 Party/Session/迁主实机 |
| Official/MOD 存档隔离 | 源码和测试已接线 | 真实存档往返、旧存档归属、损坏恢复 |
| FOV | 核心线程和签名定位已接线 | Official/MOD、FusionFix/FusionMod、无模块四种实机 |
| 3.0 音频 | 当前静态 XAudio2.7 路径已恢复 | 双声卡、拔插、失焦/回焦、长局稳定性 |
| 原生弹窗 | DX11 精确链已接线 | 连续第二次弹窗、多人同时弹窗、确认不闪退 |
| 网络复制 | 当前 20/10 Hz 静态契约明确 | 3.0 实际频率与队列探针、掉包/高延迟场景 |
| 模式状态与在线 TTL 耦合 | 源码与自动回归已拆分为在线 20 秒、已认证模式 90 秒；尚未实机闭环 | 真实短断 20–90 秒仍可邀请、超过 90 秒失败关闭、恢复后立即成功 |
| EMP 视觉头盔 | 旧方案已推翻，当前未实现 | 找到真实 Enhanced Detection 对象和视觉材质 owner 后重新立项 |
| 体力条/静音鞋/速度鞋/ATS-BMS-RFD 等 | 设计存在，当前未实现 | 各自独立对象/调用链和玩法验收标准 |
| 快速匹配多 Party | 自动测试与历史多人场景较多 | 4+ 人、两队同时退出/迁主的重复回归 |
| Hooks 日志每条重新打开文件 | 当前 `FileWriter` 每次记录执行 append/open；属于性能与故障传播风险，尚无实机首因 | 在不改变日志路径和线程边界的前提下设计有界 writer，并做长局/磁盘异常回归 |
| Dedicated 启动清 transient 状态 | 当前业务明确，不能作为“缓存清理”删除 | 若未来需要热重启保会话，先设计持久 generation/租约与客户端重连协议 |

## 15. 直接源码与历史证据入口

当前源码：

- Hooks 启动与生命周期：`client/hooks/src/lib.rs`
- Hooks 地址与功能安装：`client/hooks/src/addresses.rs`、`hooks.rs`、`hooks/storm.rs`、`hooks/network_replication.rs`、`hooks/native_dialog.rs`
- 模式与 Overlay：`client/hooks/hooks-config/src/lib.rs`、`client/hooks/src/overlay.rs`、`overlay/contracts.rs`、`overlay/invites.rs`、`overlay/presentation.rs`
- Uplay、邀请、Party、存档：`client/hooks/src/uplay_r1_loader.rs`、`uplay_r1_loader/party.rs`、`uplay_r1_loader/save.rs`、`client/hooks/src/api.rs`
- Gameplay：`client/hooks/gameplay/src/gameplay_features.rs`、`extended_loadouts.rs`、`player_capacity.rs`、`weapon_probe.rs`
- 音频：`client/hooks/audio/src/`、`client/hooks/build.rs`、`client/hooks/src/plugin_loader.rs`
- Dedicated：`server/dedicated-server/src/api.rs`、`game_session.rs`、`storage/parties.rs`、`storage/sessions.rs`、`storage/invites.rs`、`gameplay_modes.rs`
- 身份与启动策略：`server/dedicated-server/src/storage/accounts.rs`、`storage/mod.rs::invalidate_sessions`
- 远端失败关闭：`server/dedicated-server/src/player_stats.rs`、`nat_traversal.rs`
- Control Plane 模式快照：`server/control_plane_domain.py`、`server/scbl_control_plane.py`、`server/control_plane_gameplay.py`
- Quazal：`server/quazal/src/prudp.rs`、`prudp/packet.rs`、`rmc.rs`、`rmc/basic.rs`、`rmc/types.rs`
- 生成协议服务端：`server/dedicated-server/sc_bl_protocols/src/protocols/`
- 私人房不变量：`server/dedicated-server/PRIVATE_MATCHMAKING_REPAIR.md`

旧 2.0 只读证据入口（按需核对，不复制版本流水）：

- `D:\codex\SCBL\docs\coordination\reports\DEV-20260819-HOST-MIGRATION-ANALYSIS.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260818-HOST-MIGRATION-CONVERGENCE.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260820-SAME-SESSION-INVITE-RACE.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260820-PRIVATE-ROOM-PARTY-COHORT.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260821-QUICK-MATCH-PARTY-BOUNDARIES.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260812-GAMEPLAY-DLL-SPLIT.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260814-AUDIO-BUILTIN-HOOKS.md`
- `D:\codex\SCBL\docs\coordination\reports\DEV-20260815-AUDIO-STABLE-CRASH.md`

本文件不承担发布、版本和通道记录；这些事实只由 `SCBL_HANDOFF.md`、`CHANGELOG.md` 和不可变机器 receipt 维护。
