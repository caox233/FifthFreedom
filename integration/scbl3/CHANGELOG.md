# 更新记录

## Unreleased

### Server 3.0.16

- Windows 更新助手先完整校验并暂存更新 ZIP，再停止服务和创建备份；无效包不再造成不必要停服。

### Server 3.0.15

- Windows 服务端客户端发布入口合并为一个普通 ZIP 入口。
- 上传完整客户端会自动生成六组件更新并重新生成固定 `SCBL.zip`。
- 上传组件补丁 ZIP 会应用到当前客户端快照，并在同一事务内重新生成 `SCBL.zip`；失败保持原发布状态。

### Server 3.0.14

- 原生 Windows Manager `2.0.32` 增加“客户端发布”页：先导入同一候选的六组件 `.scblpatch`，再导入完整客户端 ZIP；校验版本、大小、SHA256 与组件回执后，原子生成固定 `SCBL.zip` 和启动器所需 `scbl-server.json`。
- EasyTier Windows 服务端移除重复的显式 `[::]` WSS/QUIC 监听；启用 IPv6 时由 `0.0.0.0` 监听提供双栈，避免首次安装预检触发 WinSock 10022。

### SCBL Client 3.0.6 candidate

- 默认测试服务器改为 `test.example.invalid:11011`，测试更新端口保持 `18081`；已有用户自定义设置不被覆盖。

### Server 3.0.13

- Manager `2.0.31` 可从当前六组件直接封装并发布完整客户端，不在服务器重新编译 Windows 组件；手动上传完整 ZIP 也走同一事务。
- Update Server `2.0.91` 提供固定完整包地址 `SCBL.zip`，发布时原子切换到最新不可变版本并继续支持 Range 下载。
- 完整包、组件、组件清单备份、数据备份、Runtime 和 EasyTier 历史统一保留最近三代，同时保护当前与直接回滚版本。

### SCBL Client 3.0.5 candidate

- Client/Launcher `3.0.5`：服务器描述与组件下载固定按 IPv6 优先连接；单个 IPv6 地址连接失败或超时后，在同一请求内继续尝试 IPv4，不再因其中一个地址族暂时不可用而误报“服务器配置不可用”。
- `VERSION_CLIENT`、完整客户端版本和 Launcher 组件版本改为强制一致；构建、组件装配和完整包入口遇到错位会直接停止，避免完整包号与界面版本再次分离。

### Server 3.0.12

- Manager `2.0.30`：首次安装器可随包携带同一客户端候选的完整 ZIP 与全组件补丁，安装后自动建立 5 个程序组件和 Client Files 基线，再发布完整包，避免新服务器生成 `client.version=0.0.0` 的不可用描述。
- 首次安装完成后可直接选择安装并配置 DDNS-Go；修复安装继续保留已有 DDNS-Go、服务端身份、网络密钥、配置和数据库。

### Server 3.0.11

- Dedicated `2.0.28`：修复玩家接受普通邀请并离开原快速比赛时，延迟的个人大厅恢复又触发第二次原生接受、拆掉刚加入目标房间的问题。恢复现在绑定本次已接受邀请：目标加入完成即结束恢复，目标失效才释放且只释放一次；历史大厅邀请不参与本次判断。

### Server 3.0.10

- Control Plane `2.0.93`：在线列表继续使用 20 秒心跳 TTL；最近一次已认证的 Official/MOD 断言改用独立 90 秒保留期，短暂控制面断线不再立即让仍在游戏中的好友变成“无法确认模式”。新上报的非法模式会撤销旧断言；明确模式不一致、MOD 完整性失败、从未上报及断言超时仍严格拒绝。
- Dedicated `2.0.27`：模式快照不可读、无效、发送者/目标缺失或过期使用可检索的细分原因；玩家提示仍保持简洁，邀请、Party、Session 与模式隔离语义不变。
- 服务端组件入口支持把多个变化组件装入同一个普通 ZIP，本批只构建 Dedicated 与 Control Plane，并在一次 Runtime 事务中切换。

### Server 3.0.9

- Dedicated `2.0.26`：修复私人房晚加入成员在房主退出、原生迁主成功后被误删大厅恢复事件的问题；迁主现在只取消新房主自己的过期恢复目标，其他成员仍可加入同一迁移后 Party 大厅。
- 增加“原房主与继任者已在 Party、第三名玩家仅后加入私人 Game Session”的固定回归，覆盖实机出现的 0 人大厅边界。

### Server 3.0.8

- Dedicated `2.0.25`：全新数据库的玩家账号从已验证的 1000+ 主体区间分配，避开 Quazal 的认证 PID 2、Tracking PID 105 和安全服务 PID 4096；修复低 ID 玩家登录会跳过票据流程、随后退出线上模式的问题。
- Quazal：未知多态 RMC 类型不再触发服务进程 panic，改为协议解析错误并失败关闭；重复请求响应重放整理为独立帮助函数，不改变缓存、序号或发送语义。
- 删除 Dedicated release 中三个仅供测试使用的查询包装/返回字段，保留对应行为回归，不改变邀请、Party 或 Session 运行路径。

### SCBL Client 3.0.4 candidate

- Hooks `2.0.148`：移除 3.0.2 新增的 CoreSettings 原生函数诊断 Detour，避免诊断逻辑侵入游戏状态机；后续 A/B 已证明线上登录的实际首因是全新数据库把首个玩家分配为 Quazal 服务 PID 2，而不是该 Detour。
- Updater `2.0.10`：更新窗口改为清晰的 SCBL 卡片样式，提供高对比标题、固定状态、独立计时、可见动画进度和明确失败状态，不再呈现为近乎全黑的系统默认框。

### SCBL Client 3.0.3 / Server 3.0.7 candidate

- Client/Launcher `3.0.3`：首次 FOV 选择从 Launcher 打开阶段移到玩家第一次点击“启动游戏”后、组件检查与游戏进程启动前；保存或关闭一次后不再自动弹出。
- Dedicated `2.0.24` / Server `3.0.7`：根据 3.0.2 实机结果码 `0x806C0022` 和同一时刻服务端 `MissingPort` 证据，Secure Register 兼容零售客户端省略自报 address/port 的 URL；响应仍只使用认证套接字实际地址和端口，非法显式端口继续拒绝。

### SCBL Client 3.0.2 / Server 3.0.6 candidate

- Client/Launcher `3.0.2`：首次使用默认 Official 模式时打开现有 FOV 选择；EasyTier 只接受服务器描述提供的密钥，Fast/Compatible 分别保持 QUIC/WSS，并在同协议内优先可用 IPv6、回退 IPv4。
- Updater `2.0.9`：保持自包含单文件和原子替换语义，启用单文件压缩以显著降低分发体积。
- Hooks `2.0.147`：为当前精确 DX11 字节增加只读 CoreSettings 请求结果探针；严格校验函数前缀，只记录错误码，不修改请求、返回值或控制流。
- Dedicated `2.0.23` / Server `3.0.6`：为 Secure Register、RMC 编码和 PRUDP 发送增加关联的脱敏结果证据，移除会展开客户端自报 URL 的日志；认证、票据、Party、Session 与 Overlord 响应保持不变。

### SCBL Client 3.0.1 / Server 3.0.5 candidate

- Client/Launcher `3.0.1`：默认 DX11；FOV 对 Official 与 MOD 都生效；Official 不加载玩法 DLL，MOD 加载唯一 `scbl_gameplay.dll` 并启用全部已完成功能；两种模式使用隔离存档。
- Hooks `2.0.146`：恢复静态内置、真实 XAudio 2.7 ABI 兼容层并在 Uplay 启动阶段初始化；删除外置音频 DLL、玩法子运行时、热重载与开发诊断桥。
- Dedicated `2.0.21`：恢复零售客户端登录必需的固定 `Tracking` 服务主体，并对 ID/用户名冲突失败关闭。
- Dedicated `2.0.22`：SecureConnection 注册仅在 method 1/4 接受零售客户端省略自报 `address` 的 URL，服务器仍只使用已认证套接字的实际地址；通用 StationURL、票据、邀请、Party 与 Session 继续严格校验。
- Server Manager `2.0.29`：测试服务器部署和回滚不再因在线玩家自动停止；正式服务器继续默认保护在线玩家，仅显式强制时放行。
- EasyTier 路径判定只接受当前默认连接 ID 的精确对象/字符串匹配，不再按瞬时最低延迟猜测 IPv4/IPv6。
- Server `3.0.4` / Manager `2.0.28`：配置升级到 schema 5，服务端描述不再携带玩法功能开关；三类公告统一进入 `scbl-server.json`，缺少自定义更新文案时生成与当前版本绑定的通用公告。
- Client Files `2.0.28`：正式完整包和增量文件组件都携带经 SHA256 绑定的 `components/hooks/mod/scbl_gameplay.dll`，不再遗漏 MOD 运行文件。

### SCBL 3.0.0 initial migration work

- Hooks `2.0.145` 移除会以错误 XAudio2.7 ABI 在 `CreateMasteringVoice` 阶段触发访问冲突的内置音频增强及其未再使用的 C++ 构建树；游戏恢复原生音频，登录、邀请、Party、Overlay、网络复制和玩法路径不变。
- Rust 组件冻结现在保持完整 Cargo workspace 与 `shared/api` crate 的结构，而不再只复制目标源码和 proto 文件，避免 Hooks/Dedicated 候选在 Cargo 加载未构建成员时因缺失清单或目标停止；正式编译仍只选择变化组件。
- 全新、缺失或无效渲染设置统一默认 DX11；只有用户明确保存 DX9 时才保留 DX9。
- Client Files `2.0.27` 增加集中许可说明；Launcher 接受至少含一个受管删除操作的 delete-only 文件包，不再与允许该包形态的组件构建契约冲突。
- Server Manager `2.0.27` 在发布完整客户端时自动复用或生成与目标版本精确匹配的更新公告，并把公告、完整包清单与服务器描述纳入同一失败回滚边界，避免完整包已公开但客户端因公告缺失而拒绝更新。
- Manager 在发布完整客户端前要求当前服务器已具备全部六组件基线，避免首次部署生成客户端必然拒绝的空组件代际；修复交互菜单重复发布完整包时传递已删除参数的问题。
- 客户端正式/测试通道改为各自读取对应服务器的唯一 `scbl-server.json`；网络、控制面、Dedicated、玩法、公告、组件和完整包不再从多份公网配置拼接。
- 删除 Launcher controls、network bootstrap、独立公告、独立玩法 profile、旧签名组件清单和旧客户端回执迁移；服务端描述缺少必需对象、完整玩法键、组件代际或出现同名组件时直接失败关闭。
- 组件更新应用层统一使用 `componentGeneration` 术语，组件服务拆分为协调、Bundle 与下载职责；完整包 Hooks 回执按实际包根建立，不再错误读取 Launcher 安装根。
- Launcher 构建默认复用 `bin/obj` 增量缓存，`-Clean` 才清理；Hooks 音频改用仓库内 XAudio 2.7 兼容声明，删除缺失外部 SDK 头文件依赖与未接线实验探针。
- 收紧认证、Route Guard、回执和适配器边界：旧 token、旧 Route Guard 安装迁移、旧 receipt schema 与旧 SCBLTunnel 适配器不再作为 3.0 运行路径。
- 服务端将网络、客户端版本/制品、控制面、Dedicated、玩法和公告收敛为唯一公开 `scbl-server.json`。
- 移除旧 `networkBootstrap` 复制与 Launcher controls 公开契约；Update Server 隐藏内部元数据。
- 服务端玩法配置进入 `server.toml`；客户端后续直接生成游戏目录 TOML，不再下载 `scbl-features.toml`。
- 公开下载路径改为解析后的严格子目录校验；拒绝编码穿越、符号链接制品和清单/实际字节漂移。
- 组件 bundle 只使用本服务器 `server.mode`，客户端不得指定另一通道；发布或公告任何一步失败都保留上一份完整公开快照。
- 服务端配置现在拒绝互相冲突或占用 Dedicated 固定值的端口，并真正把 `network.enable_ipv6` 和可配置虚拟 IPv4 网段传给 EasyTier/控制面。
- `server.toml` 升级为 schema 4，删除从未生效的 `pool_start` / `pool_end` / `wan_interface` 假配置。
- 删除零引用 Launcher-only 完整包兼容发布、被忽略的 `publish-client --force`、旧 tar Runtime 解包和重复的 Runtime 契约探测入口；服务端只保留普通 ZIP 路径。

## SCBL Client v3.0.0

- 建立全新的 SCBL 3.0 客户端与独立测试服务器基线。
- 首次完整包采用精简后的便携目录、单一服务端描述和组件化更新契约。

## SCBL Launcher v2.2.46 (migration baseline)

- 修复旧安装目录遗留的 WinDivert 服务登记指向已不存在驱动时，Route Guard 连续启动失败并阻止游戏启动的问题；仅清理已停止且目标文件确实不存在的登记，正在运行或路径有效的其他实例保持不变。
- 修复启动失败后的挂起游戏进程回滚再次读取失效进程对象，导致原始错误被覆盖并留下假“游戏正在运行”的问题。
- Launcher 精简冻结入口补齐编译所需的签名组件 catalog 依赖，继续排除无关服务端、Hooks 与缓存源码。

## SCBL Server v3.0.3

- 服务端完整包从七组件缩减为五个 SCBL 自有组件；EasyTier 与 DDNS-Go 改由 Manager 菜单从官方发布源安装，重复安装复用已校验版本和下载缓存，更新不再要求重编服务端包。
- 服务端配置只保留一个游戏端口，WSS 使用 TCP、QUIC 使用同端口 UDP；删除重复 `wss_port`/`quic_port`、无效 DDNS 字段、EasyTier 固定输入目录、EasyTier 组件包和 Xshell `rz` 路径。
- Manager 拒绝同版本不同字节，Manager 与 Runtime 分包更新；`server.assets` 会真正覆盖持久化配置，`previous` 可直接回滚，并可安全清理 current/previous 之外的旧 release 与无引用组件缓存。
- Control Plane 在每次心跳精确验证本实例最新客户端版本并严格要求 JSON 布尔值；Update Server 的 ETag 绑定已打开文件，组合缓存命中会复核 ZIP 与 metadata 的大小和 SHA256。
- Control Plane 源码拆分为 HTTP/进程入口、认证、领域状态、会话与健康采样四层，正式装配时使用 Python 标准 zipapp 合成一个 `scbl_control_plane.pyz`，目标服务器仍只管理一个控制面制品。
- `SCBL` 客户端菜单按本机 `server.mode` 固定唯一目标通道，删除人工选择另一通道和 test→stable 提升入口；公告编辑、文件选择与客户端发布从主菜单组合根拆出。
- 删除组件 `minLauncherVersion` 契约；服务器只公布一个当前客户端版本，通道与版本均需精确匹配。
- 服务端更新、完整包修复和回滚发现在线玩家时显示人数并默认取消；只有操作者明确输入 `FORCE` 才允许强制执行。

## SCBL Server v3.0.2

- 服务端身份收敛为单实例单客户端通道：`server.mode` 只标识 stable/test 入口，不再允许测试服务器自动接纳更高客户端版本；客户端通道与本实例不匹配或版本不精确一致时拒绝接入。
- 控制面玩家模式字段改为 `official` / `mod`，删除 `casual` / `serious` 输入兼容和合作模式跨玩法放行；MOD 文件证明无效时继续失败关闭。
- 服务端 full/update 包收敛为普通 ZIP、组件 manifest 和 SHA256 完整性校验；按当前维护决定移除服务端包 Ed25519、公私钥、keyId 和安装公钥参数，客户端更新信任链本轮不变。
- EasyTier 输入改为由组件版本选择版本目录并绑定批准 SHA256；正常升级使用一个同时携带 core 与 cli 的原子组件 ZIP，首次或重复安装不在线下载未变化的 EasyTier。
- 服务端配置升级为 schema 3，删除已停用的服务端包 trust 段和重复的 testing 开关；`server.mode` 只决定服务器服务哪个客户端通道，不再维护两个可能冲突的状态。
- 直接运行 `SCBL` 即可从菜单修改服务器身份、域名、下载基址与端口，并应用当前配置或重启单项服务；命令行子命令仅保留给自动化，不再作为日常操作前提。
- 正式服务器与测试服务器在线更新统一读取同一个 `server-latest` 服务端包流；mode 只表示实例身份，不再把同一套服务端二进制拆成两条测试/正式下载通道。

## SCBL Server v3.0.1

- 服务端游戏入口统一为同一个端口号：WSS 使用 TCP，QUIC 使用 UDP；不再额外开放或要求 `game-port + 2`。
- 首次安装、修复和运行组件更新会通过受管 `sysctl.d` 配置持久开启 IPv4 转发，并在启动服务前验证运行值已经收敛为 `1`。
- 新增单一服务端构建入口：首次安装生成包含七组件的标准 full ZIP，日常更新只编译并打包指定变化组件。

## SCBL Launcher v2.2.45 (test candidate)

- EasyTier 明确报告连接关闭、PeerNoConnection、RouteError 或路由同步超时时，Launcher 会立即复核真实服务器数据路径；健康路径不重启，确认失效才进入自动恢复。
- 游戏运行中先给原 EasyTier 代际 12 秒原地重连；仍未恢复时只执行一次受控重建，并且必须保持游戏已绑定的同一虚拟 IP，避免重复重启或双连接。
- 修复游戏网络监控后台线程读取 WPF 控件导致每两秒跳过一次监控的问题；用户名和玩家快照现在在 UI 线程启动监控时一次性固定，恢复后的连接状态可正常更新。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.44 (test candidate)

- 修复新解压完整客户端把便携目录中的 Route Guard 误当作已经在受保护运行目录激活，导致跳过签名组件下载并在启动游戏时提示导流组件缺失的问题。
- Route Guard 现在只有受控目录中的真实安装回执能够跳过更新；完整包首次启动会从 required Ed25519 清单下载、校验并原子部署 canonical 组件。
- 游戏进程创建为挂起状态后若导流或其他启动步骤失败，Launcher 会使用保留的原生进程句柄终止并等待退出，避免残留进程导致后续误报“游戏正在运行”。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.43 (test candidate)

- 修复新解压完整客户端首次启动时，组件清单已经通过签名校验、但 Hooks 尚无运行时通道回执，导致启动游戏前错误提示组件不受信任的问题。
- 仅在 required Ed25519 通道清单验证成功后，精确核对完整包回执、Hooks 版本/大小/SHA256、实际 DLL 与 sidecar；完全一致才建立通道回执并继续原子部署，任一漂移仍会安全拒绝。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.42 (test candidate)

- IPv6 连接失败后的 IPv4 回退现在只影响当前 Launcher 启动；下次启动会重新依据真实网卡与服务器地址族选择路径，不再保留跨启动的24小时 IPv4偏好。
- 删除已经被当前运行时“协议、地址族、代际”原子观察取代的旧 transport 缓存/探测入口，以及零引用的服务器 peer 包装路径，避免双状态和过期结果。
- EasyTier 断链确认、游戏运行中同进程/同虚拟 IP 原地恢复、单一服务器 peer、玩家间按需 P2P与中继兜底保持不变。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.41 (test candidate)

- 修复游戏未运行时自动恢复仍复用已失效 EasyTier 进程的问题：服务器链路确认失败后会建立新的运行时代际，清除旧 peer/路由状态；游戏运行中仍不重建虚拟网卡或虚拟 IP。
- 服务端保留固定 QUIC/WSS 数据链所需的普通 peer 能力，但单独关闭 UDP、TCP 与对称 NAT 三种打洞方法；玩家客户端之间的 EasyTier 原生按需 P2P 保持不变。
- IPv6 失败后若 IPv4 回退随后也发生真实服务器数据路径失败，会撤销下次启动的 24 小时 IPv4 优先记录；当前启动仍保持 IPv4，避免来回切换。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Dedicated、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.40 (test candidate)

- 服务器 EasyTier 角色固定为 QUIC/WSS 入口和中继，不再声明 `need_p2p` 或参与动态打洞；玩家到服务器的线路不会再被动态 UDP 路径替换。
- 玩家客户端继续保留 EasyTier 原生按需 P2P：只有玩家间出现真实游戏流量时才尝试局域网或公网直连，失败继续使用服务器中继。
- 提升网络运行时配置代际，测试版重启后会清除旧服务端打洞策略下残留的连接选择；不开放服务器随机 UDP 端口，不修改或重编 EasyTier。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Dedicated、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.39 (test candidate)

- 玩家节点改用 EasyTier 原生延迟 P2P：先使用服务器中继保证可用，只在真实玩家流量出现后再尝试局域网或公网直连，直连失败仍保留中继。
- 普通玩家节点的路由超时、P2P 关闭和 OSPF 重选不再被误判为“服务器不可用”；只有服务器手工连接失败或实际 `10.66.0.1` 健康检查失败才进入全局恢复。
- 局域网直连、NAT 打洞、失败回落和服务器关键节点直连继续由 EasyTier 2.6.4 处理；未修改或重编 EasyTier，未新建平行网络实现。
- 本候选只重新构建和发布 Launcher test；Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包与 stable 均保持不变。

## SCBL Launcher v2.2.38 (test candidate)

- 修复 Windows/Rust 把 IPv4 套接字表示为 IPv4-mapped IPv6（`::ffff:x.x.x.x`）时，Launcher 错误显示为 IPv6 的问题；这类地址现在按真实 IPv4 显示。
- 地址族判断改为标准 IP 解析：原生 IPv4、IPv4-mapped IPv6 与原生 IPv6 分别明确识别，主机名或未知格式失败关闭，不再依靠冒号数量猜测。
- 删除被标准 IP 解析取代的旧冒号计数启发式；默认连接唯一匹配、实际传输检测和 Connected 原子门禁保持不变。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.37 (test candidate)

- 修复 EasyTier CLI 返回结构化远端地址时，Launcher 把 JSON 标点中的多个冒号误判为 IPv6 的问题；关闭 IPv6 的电脑不再把实际 IPv4 QUIC/WSS 路径显示成 IPv6。
- 已识别的嵌套 IPv4/IPv6 地址按真实地址族显示；未知结构失败关闭，不再使用原始 JSON 猜测连接类型。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.36 (test candidate)

- 每个 EasyTier 运行时代际现在只建立一条到服务器节点的手动连接：IPv6 可用时先使用单一 IPv6 地址，启动前未收敛才以同协议重建到单一 IPv4 地址；不再同时加入同一节点的 IPv6/IPv4或“IPv4地址+域名”重复连接，避免路由同步会话互相重置。
- 未启用或不可用 IPv6 的电脑只生成 IPv4 listener 与 IPv4 server peer；运行时配置指纹绑定实际 peer 集合，网卡地址族发生变化后不会静默复用旧的 IPv6 运行时。
- 断链/恢复信号会重新读取当前 EasyTier 默认服务器路径，不再把上一条 IPv6/IPv4 观察值重新显示为已连接；快速仍为 QUIC，兼容仍为 WSS。
- 服务器控制面源码增加 SQLite busy/locked 的有限重试并保持其他数据库错误失败关闭；该服务端变更本轮仅完成源码测试，不随 Launcher test 部署生产。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.35 (test candidate)

- 游戏运行中网络异常时不再停止或重建 EasyTier：Launcher 保留同一个运行进程、虚拟网卡、运行代际和游戏已绑定的虚拟 IP，在约 30 秒内等待原链路自行恢复；超时后明确提示退出并重启游戏，不循环重启隧道。
- IPv6 首选配置现在同时保留同协议 IPv4 备用入口，使 EasyTier 可在同一运行时内切换底层网络而不更换游戏虚拟地址；仅当实际路径确认由 IPv6 恢复为 IPv4 时，才记录 24 小时 IPv4 优先。
- EasyTier 明确断链和成功重连日志都会唤醒同一服务器路径确认；重复信号合并处理，恢复后立即清除“重新连接中/网络失败”状态。
- 游戏退出后仍异常时才允许一次普通运行时重建；本候选只重新构建和发布 Launcher test，不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.34 (test candidate)

- 自动网络恢复不再复用已经确认失效的 EasyTier 运行时；只有恢复后的虚拟 IP 与正在运行的游戏会话地址一致，才会重新显示连接成功，否则明确要求重启游戏，避免 Launcher 已恢复但游戏仍绑定旧地址。
- IPv6 服务器路径失败且同协议 IPv4 路径真实恢复后，Launcher 会在 24 小时内记住本机 IPv6 冷却状态；期间下次启动直接使用 IPv4，过期后自动重新尝试 IPv6。
- 游戏运行期间继续禁止玩家手动切换快速/兼容模式；不修改 TEST3 的服务器登录超时与重新进入线上模式语义。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.33 (test candidate)

- 游戏运行中继续禁止玩家手动切换快速/兼容模式，避免主动操作中断当前房间；后台网络异常恢复不受该手动门禁影响。
- 删除独立的 15→30→60 秒循环检查；EasyTier 明确断链会立即触发确认，游戏内服务器路径最近 5 次样本达到 40% 丢失时也会触发确认。
- 所有异常信号只唤醒同一实际服务器路径检查；路径仍健康时不切换，确认失败后才即时显示重连并按现有 IPv6 优先、当前启动内 IPv4 熔断策略恢复。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.32 (test candidate)

- 快速模式和兼容模式不再仅凭取得虚拟 IP 就确认 IPv6 服务器入口；只有实际传输与 IPv6 地址族收敛后才保留该路径。
- IPv6 入口已建立但真实服务器数据路径不可用时，游戏启动前会重启同一传输并使用本次 DNS 解析得到的显式 IPv4 地址；快速模式仍为 QUIC，兼容模式仍为 WSS。
- 游戏运行中继续禁止强制重建 EasyTier，避免破坏现有房间；本候选只重新构建和发布 Launcher test，不修改 EasyTier 本体、Hooks、Client Files、Runtime、stable、完整包或 GitHub。

## SCBL Launcher v2.2.31 (test candidate)

- 检测到可用 IPv6 与服务器 AAAA 地址时，快速模式和兼容模式会先仅使用实际 IPv6 地址连接，避免双栈域名再次被解析为 IPv4 并抢占默认链路。
- IPv6 在有限窗口内未建立时，Launcher 会重启同一 EasyTier 传输并回退到 IPv4/域名候选；快速模式始终使用 QUIC，兼容模式始终使用 WSS，不进行跨协议兜底。
- 提升网络运行时配置代际，确保旧的双栈候选不会被静默复用；本候选只重新构建和发布 Launcher test，不修改 EasyTier 本体、Hooks、Client Files、Runtime、stable、完整包或 GitHub。

## SCBL Launcher v2.2.30 (test candidate)

- 统一 Launcher 的签名服务器配置入口：新版优先读取版本化配置，只有主配置文档不存在时才兼容旧入口，签名缺失、内容无效或过期不会降级放行。
- 记住玩家明确选择的测试通道；正式版启动不再因公网配置检查阻塞窗口，测试版可使用最后一份已签名且未过期的可信缓存。
- 移除平台辅助 PowerShell 调用中的 ExecutionPolicy Bypass；本候选只重新构建和发布 Launcher test，保留 Hooks 2.0.141 与其他组件原字节，不修改 stable、完整包、Runtime 或 GitHub。

## SCBL Launcher v2.2.29 (test candidate)

- 修复 Client Files 已完成安装但 Launcher 未重启的问题：即使文件包在更新事务中已经即时应用，也会完成一次可靠的重启接管。
- 重启后的 Launcher 会重新校验刚安装的 MOD 组件并刷新服务器可用性，避免文件已经齐全但“模组”仍保持灰色、启动仍写入 Official。
- 本候选只重新构建和发布 Launcher test；保留 Client Files 2.0.26、Hooks 2.0.139 与其他组件原字节，不修改 stable、完整包、Runtime 或 GitHub。

## SCBL Launcher v2.2.28 (test candidate)

- Launcher 将经过服务器开关和本地运行时门禁确认后的最终“官方 / 模组”模式，以顶层 `GameplayMode = "Official" / "MOD"` 明确写入 Hooks 配置。
- 写入后会回读并严格校验同一模式；字段缺失、未知或非规范值时取消启动，不再从玩法开关、DLL、FOV 或通道猜测模式。
- 本候选只重新构建和发布 Launcher test；公开 Hooks 保持 2.0.138 原字节，不修改或重编 Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.27 (test candidate)

- 修复 MOD 已正常加载并可以进入线上模式时仍弹出“联机功能可能未生效”的误报：Launcher 现在等待实际运行时就绪，而不是在启动过程中过早判定失败。
- MOD 热加载运行时按游戏进程专属 shadow 目录与代际文件识别，并要求已加载字节与游戏目录中已校验的 `scbl_mod_runtime.dll` SHA256 完全一致；不通过日志或固定文件名放宽安全校验。
- 校验可随游戏监控取消；只有在有限轮询结束后仍未取得完整同字节证据才保留原警告，不影响正常游戏启动与服务器联机流程。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.26 (test candidate)

- 修复 FusionFix 安装根重复拼接 `SYSTEM` 的问题：Launcher 选择的游戏目录已经是 `src\\SYSTEM` 时，现在会在实际游戏加载目录检查和安装，不再误用 `SYSTEM\\SYSTEM`。
- 已存在的同名 FusionFix DLL/ASI 只要是有效 Windows 二进制就直接复用；玩家配置只按同名存在保留，其他 MOD 的不同文件不阻断，也不要求玩家自行备份或删除。
- 记住玩家上次选择的“官方 / 模组”偏好；启动早期服务端状态或文件检查暂未完成时只临时失败关闭，不再把 MOD 偏好覆盖成 Official，恢复可用后静默回到 MOD。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.25 (test candidate)

- 修复游戏 `SYSTEM\\scripts` 中存在无关脚本时 FusionFix 被静默跳过的问题；现在只有真实加载器冲突才会为保护已有 MOD 停止安装，并显示明确提示。
- FusionFix 必需二进制按冻结 SHA256 验证；玩家可修改的 `.ini`、`.xml` 等配置只按同名文件是否存在判断，已有内容不读取、不比较哈希、不覆盖，缺失文件才创建。
- 安装结果会明确记录“已安装 / 已存在 / 因冲突保留”、新增与保留文件数量及脱敏原因，避免 MOD 配置已写入但 FusionFix 实际未安装而无从诊断。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.24 (test candidate)

- 服务器开放模组后，每次选择“模组 / MOD”都会显示模组简介和 FOV 调节，范围 1.0–1.3、默认 1.2；设置由 SCBL Hooks 在游戏内存中应用，不读取或修改 FusionFix 配置文件。
- 首次选择 MOD 时会在后台安装随 Launcher 冻结的 FusionFix；检测到已有 FusionFix 或其他 MOD 时整批保留，绝不覆盖玩家现有 MOD 文件。
- FusionFix 安装使用路径校验、不可变资产哈希、分阶段创建与失败回滚；重复选择不会重复安装或弹出第二个安装确认。
- 本候选只重新构建和发布 Launcher test；公开 Hooks 保持 2.0.138 原字节，不修改或重编 Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.23 (test candidate)

- 切换“官方 / 模组”时不再由界面线程重复读取并计算玩法 DLL 哈希，选择操作保持响应。
- 启动游戏时把核心与玩法组件的校验、复制和原子部署移到后台；官方模式不再扫描 MOD 文件，MOD 仍会在启动前重新完整验证。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.22 (test candidate)

- 测试版可在服务器签名开关开放时选择“模组 / MOD”；开关关闭、签名无效或本地玩法组件不完整时会安全回到官方模式。
- MOD 启动前会核对同一套核心、玩法和 MOD 运行时文件，启动后再确认实际模块加载；不再把尚未启动误判为运行时缺失。
- 官方模式继续关闭全部玩法功能并隔离玩法、诊断和历史独立音频插件；当前 stable 的 MOD 开关保持关闭。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.21 (test candidate)

- stable 通道完全忽略本地 Hooks 覆盖，并要求已安装回执、随包 SHA256 与实际核心 DLL 三方一致；缺失或错配时失败关闭。
- test 通道只接受内嵌有效版本标记、且版本不低于当前 test Hooks 基线的本地覆盖；旧版、无标记或无效 DLL 会自动回退到 canonical test Hooks。
- 官方模式继续隔离独立音频、玩法、诊断和 MOD 插件；本候选只重新构建 Launcher，不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.20 (test candidate)

- 优化启动器内部结构，网络、更新、登录、游戏启动和进程状态由明确的独立组件负责，窗口仅保留装配、生命周期与状态显示，玩家操作和界面保持不变。
- 加强游戏网络状态监测的生命周期隔离：停止监测后迟到的探测结果不会重新写入活动状态或缓存；监测自然结束后可以可靠重新启动。
- 本候选只重新构建和发布 Launcher test；不修改或重编 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## Server Runtime v2.0.29

- 修复同一房间多名成员先后发出邀请时，后发邀请覆盖接收者其他待处理邀请的问题；不同发送者的邀请现在互不删除。
- 同一发送者、接收者与邀请类型的重复请求会原位刷新并保留服务器邀请 ID；房主在线且发送者仍是该房间成员时可继续邀请、接受和精确发现房间。
- 本次 Runtime 仅重新构建 Dedicated；不修改 migration、协议、配置、Manager、客户端组件或通道。

## SCBL Launcher v2.2.19 (test candidate)

- 组件更新与测试/正式通道切换现在共享同一事务门禁；更新检查、公告确认、下载、验证和重启交接期间均拒绝通道切换，不会让旧通道更新与新通道重启交叉执行。
- 通道切换在公网开关异步返回后、保存偏好后安排重启前都会重新确认运行状态；期间若更新或游戏流程开始，会回滚偏好并保持原通道。
- 更新或其他忙碌状态开始时立即关闭并刷新设置菜单，避免已展开菜单保留过期的可点击通道入口。
- 本候选只重新构建和发布 Launcher test；不修改 Hooks、Updater、Route Guard、EasyTier、Client Files、Runtime、完整包或 stable。

## SCBL Launcher v2.2.18 + Server Tool v2.0.22

- Launcher 不再把“获得虚拟 IP”误判为覆盖网络已经可用；现在会以服务器真实数据通路的有限轮询作为最终就绪门禁，避免 EasyTier peer/路由仍在收敛时被立即销毁并反复切换模式。
- 网络探测保留各端口的超时或 Socket 错误，并在恢复前冻结 EasyTier peer/route 快照，使诊断包能够还原故障瞬间而不是只记录重建后的空状态。
- Server Tool 支持当前 Runtime schema 2；测试客户端开关改为控制面健康后才提交配置，失败时尽力恢复旧运行状态，避免出现“配置已开、实际运行仍关”的半应用状态。
- Launcher 本轮只形成更高不可变 test 候选；Server Tool 以独立 Manager-only 可回滚事务更新，不修改 Server Runtime、Dedicated、数据库、客户端其他组件或 stable。

## SCBL Launcher v2.2.17 + Client Files v2.0.24 (test candidate)

- 游戏模式统一为“官方 / 模组”（`Official / MOD`）；旧 `Casual/Serious` 或未知设置失败关闭迁移到官方模式，模组当前可见但保持“开发中”且不可选择。
- 官方模式只使用联机核心；启动前会把玩法/诊断 DLL 移出游戏加载目录，Client Files 仅分发唯一 `[Official]` 配置且 8 项玩法/诊断开关全部关闭。服务端签名开关、DLL 校验和同一 runtime 已加载三项未同时满足时，MOD 不可选也不可启动。
- 测试通道入口由公开签名服务端状态控制，本地参数或旧设置不能绕过；状态不可用、无效或禁用时失败关闭回 stable。启动公告和顶部滚动公告固定从公网入口读取，失败时仅使用各自最后一次有效缓存，不改变更新清单/签名信任边界。
- 游戏正在启动或运行时，DX9/DX11 与游戏模式同时锁定；移除已被 Official 单路径取代的 Serious 专用完整性监控、回调和旧呈现路径。
- 本候选只正式构建 Launcher，并装配 Client Files；不构建或发布 Hooks、Updater、Route Guard、EasyTier、Runtime、完整包或 stable。

## SCBL Launcher v2.2.16 (test candidate)

- 正常 Launcher 的 test 通道现在可从 `local-components/hooks` 读取本地 Hooks 核心及 `scbl_gameplay.dll`、`scbl_diagnostics.dll` 两个附加组件，并在启动游戏前按 SHA256 原子部署到 `SYSTEM`。
- 本地核心存在时，两插件必须同时存在且通过 Windows DLL `MZ` 头校验；缺失、无效或部署失败会 fail closed，并清理首次创建失败的目标或恢复原文件。stable 通道及无本地核心覆盖路径保持不变。
- 本候选只重新构建 Launcher；不构建或发布 Hooks、Gameplay、Diagnostics、Updater、Route Guard、EasyTier、Client Files、完整包、Runtime 或 stable。

## Server Runtime v2.0.28

- 修复私人邀请与在线心跳并发写入 SQLite 时，延迟事务升级可能立即返回 `database is locked` 的问题；现有多步写事务在读取业务状态前统一取得 `BEGIN IMMEDIATE` 写入资格，并继续使用既有 WAL 与 5 秒 busy timeout。
- 不增加客户端重试，不修改 migration、协议、配置、EasyTier、Control Plane、更新服务或独立 PRUDP 响应重放逻辑；本次 Runtime 仅重新构建 Dedicated。

## Server Runtime v2.0.27

- 修复可靠 PRUDP 请求重传时重复执行业务并分配新响应序号的问题；同一连接中的重复请求现在重放首次生成的完全相同响应包，使首个 `LoginEx` 响应丢失后仍能完成首次线上登录。
- 重放缓存按连接限制为最多 64 项、512 KiB；数据库 migration、配置、EasyTier、Control Plane、更新服务及 Dedicated 业务语义保持 2.0.26 基线不变。

## SCBL Launcher v2.2.15 (test candidate)

- 修复 Hooks 等组件已即时激活并写入正确回执后，Launcher 错误地把更新视为无需重启的问题；任何玩家已确认且实际应用的组件批次现在都会完成一次重启交接。
- 重启辅助进程必须先明确确认已接管，Launcher 才会退出；辅助进程提前退出或确认超时会保留可见错误，不再静默结束更新流程。
- 本候选只重新构建 Launcher；保留 Hooks `2.0.117`、Updater `2.0.8`、Route Guard `2.0.5`、EasyTier `2.6.4-scbl.1` 与 Client Files 的现有不可变字节。

## SCBL Launcher v2.2.14 (test candidate)

- 修复后台检查发现 Launcher 更新后，玩家确认更新却因停止后台轮询而误取消同一安装事务的问题；已确认的更新现在会继续完成唯一一次 Updater 替换与重启交接。
- 取消在玩家确认前仍然生效；进入停止网络的提交阶段后不再留下“文件已下载但 Launcher 未替换、网络已关闭”的半完成状态，也避免随后出现派生的清单检查失败。
- 本候选只重新构建 Launcher；保留已发布的 Hooks `2.0.115`，Updater `2.0.8`、Runtime `2.0.26`、Route Guard `2.0.5`、EasyTier `2.6.4-scbl.1` 与 Client Files 保持 test 现有不可变字节。

## SCBL Launcher v2.2.13 (test candidate)

- 修复部分电脑通过零售启动器启动游戏较慢时，Launcher 在 30 秒后停止接管实际游戏进程的问题；现在会在原有、有限的 600 秒启动观察窗口内继续识别实际游戏进程。
- 进程接管仍要求位于所选 `SYSTEM` 目录、命中游戏可执行文件白名单、属于当前 Windows 会话、未在启动前运行、候选唯一且连续两次观察一致；不会按进程名接管无关进程。
- 本候选只重新构建 Launcher；Updater `2.0.8`、Hooks `2.0.114`、Runtime `2.0.26`、Route Guard `2.0.5`、EasyTier `2.6.4-scbl.1` 与 Client Files 保持 test 现有不可变字节。

## SCBL Launcher v2.2.12 / Updater v2.0.8 (test candidate)

- 更新完成后，Updater 会先确认独立重启辅助进程已经接管，再退出并重新打开 Launcher；接管未确认时只执行一次立即回退，两种方式都失败会明确提示玩家手动启动，不再只留隐藏日志。
- 顶部滚动公告与启动公告改为直接读取公网公告源；公网超时、无效响应或暂时不可用时按公告类型保留本次运行中最近一次有效内容，明确关闭的公告会清空对应缓存且不阻断启动。
- 更新公告继续使用独立公网路径和既有失败关闭契约，组件 manifest/Ed25519 信任边界不变；本候选只重新构建 Launcher 与 Updater，Hooks `2.0.114`、Runtime `2.0.26`、Route Guard `2.0.5`、EasyTier `2.6.4-scbl.1` 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.11 (test candidate)

- 修正 EasyTier 默认连接标识的解析；当 EasyTier 同时建立 IPv4 与 IPv6 连接时，Launcher 现在显示 EasyTier 实际选用的默认连接，不再被另一条连接的瞬时延迟误导。
- 同时兼容字符串与 `part1`–`part4` 对象形式的默认连接标识；标识无效时才保留既有延迟回退，标识有效但未匹配时保持未确认而不伪造结果。
- 本候选只重新构建 Launcher；EasyTier `2.6.4-scbl.1` 未修改、未重编，Hooks `2.0.114`、Runtime `2.0.26`、Route Guard `2.0.5`、Updater 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.10 (test candidate)

- 连接成功时立即显示实际网络协议和地址类型；连接详情尚未从同一运行时完整确认时继续保持连接中，不再先显示残缺的成功状态。
- 网络模式切换期间清空上一运行时的协议和地址类型，只有新运行时的实际探测结果完整且匹配后才原子发布连接成功。
- 本候选只重新构建 Launcher；Hooks `2.0.114`、Runtime `2.0.26`、Route Guard `2.0.5`、EasyTier、Updater 与 Client Files 保持现有不可变字节。

## Server Runtime v2.0.26

- 精简 Dedicated 邀请与账号存储中的重复常量、查询和无引用包装路径；邀请、Party、私人房间及游戏行为保持不变。
- 本次 Runtime 仅重新构建 Dedicated；数据库 migration、协议、配置、EasyTier、Control Plane 与更新服务输入保持 2.0.25 基线不变。

## SCBL Launcher v2.2.9 (test candidate)

- 更新使用指南中的当前玩家数量与网络、游戏模式说明，使文案与当前 Launcher 行为一致。
- 测试通道切换失败时，键盘焦点回到主要确认操作，便于直接重试或关闭提示。
- 本候选只重新构建 Launcher；Hooks `2.0.113`、Runtime `2.0.25`、Route Guard `2.0.5`、EasyTier、Updater 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.8 (test candidate)

- 无待更新的冷启动和热启动检查期间，左下主动作保持正常“启动游戏”语义，不再短暂显示“正在更新中”；忙碌与禁用门禁保持不变。
- 只有确认存在待更新、完成公告确认并即将应用更新时，才切换为固定“更新中 / Updating”主动作与整体 0–100% 进度。
- 本候选只重新构建 Launcher；Hooks `2.0.113`、Runtime `2.0.25`、Route Guard `2.0.5`、EasyTier、Updater 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.7 (test candidate)

- 网络模式切换开始后立即隐藏上一运行时的 WSS/QUIC，只有新运行时实际确认连接协议后才显示，避免快速模式切换过程中短暂闪出旧 WSS。
- 启动检查在确认确有待更新前始终隐藏更新面板；无待更新时不再短暂显示“正在更新中”，真实更新仍统一显示整体 0–100% 与固定“更新中 / Updating”。
- 本候选只重新构建 Launcher；Hooks `2.0.113`、Runtime `2.0.25`、Route Guard `2.0.5`、EasyTier、Updater 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.6 (test candidate)

- 修复网络模式切换后连接协议显示滞后一轮的问题；快速模式显示实际 QUIC，兼容模式显示实际 WSS，未确认到正确协议时会明确失败并沿用既有回滚。
- 更新进度改为按本次全部更新内容统一计算 0–100%，跨多个组件、断点续传和 bundle 回退时不重复计数、不倒退；成功完成显示 100%，失败不伪装完成。
- 更新过程文字固定为“更新中 / Updating”，不再显示获取、校验、下载、应用等阶段动作。
- 本候选只重新构建 Launcher；Hooks `2.0.113`、Runtime `2.0.25`、Route Guard `2.0.5`、EasyTier、Updater 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.5 (test candidate)

- 网络恢复：快速模式连接失败时只询问一次是否显式切换兼容模式；目标模式失败或抛出异常时尽力恢复原模式与旧运行时，不保存半完成状态，也不静默跨协议回退。
- 地址恢复：有可用公网 IPv6 时优先使用 IPv6，同时保留同协议域名候选，使动态域名地址变化后能够重新解析；IPv4-only 环境保持原路径。
- 模式界面：统一“网络模式 / 游戏模式 / 渲染模式”中英文及辅助功能名称，收紧模式区尺寸，并把网络确认默认焦点放到确认按钮。
- 更新进度：确认、失败、取消、完成和重启阶段会及时收起进度；进度覆盖层不再推动左下模式区和启动按钮。
- 本候选只重新构建 Launcher；Hooks `2.0.113`、Runtime `2.0.25`、Route Guard `2.0.5`、EasyTier、Updater 与 Client Files 保持现有不可变字节。

## SCBL Launcher v2.2.4 (test candidate)

- 更新启动：合并冷启动阶段重复的组件清单请求，同一次验签结果同时用于网络配置和待更新判断，保留确认后的清单 SHA 换代门禁。
- 更新进度：新增清单获取、验签、检查、下载、文件验证和应用阶段；只有服务器提供真实大小时才显示确定百分比。
- 界面精简：模式标题统一为“网络模式 / 游戏模式 / 渲染模式”并缩小横向间距；在线玩家面板改为“在线玩家”并移除虚拟 IP 列。
- 本候选只重新构建 Launcher；Hooks、Runtime、Route Guard、EasyTier、Updater 与 Client Files 复用 test 已有不可变字节。

## SCBL Launcher v2.2.3 / Server Runtime v2.0.25 (test candidate)

- 网络模式：新增“快速 / 兼容”选择；快速仅使用 QUIC 服务入口，兼容仅使用既有 WSS 入口，两者均保留 UDP P2P、禁用 TCP 打洞且不静默切换协议。
- 服务端入口：保留现有 11010 UDP/WSS，新增独立 11012/UDP QUIC listener；新 Launcher 从 required Ed25519 组件清单读取签名 bootstrap。
- 设置界面：网络、玩法、渲染三组改为上下两项，默认项位于上方；玩法名称改为“优化 / 修复”，底层 Casual / Serious 语义与启动参数不变。
- 安全门禁：游戏运行期间拒绝网络模式切换；非法或缺失网络设置回到快速模式，连接失败不会自动降级到另一协议。
- 本候选只构建变化的 Runtime 与 Launcher；Hooks `2.0.113` 及 Route Guard、EasyTier、Updater、Client Files 复用 test 已有不可变字节，stable 与完整包保持不变。

## SCBL Launcher v2.2.2 / Hooks v2.0.112 / Server Runtime v2.0.24 (test candidate)

- 多人显示：补齐 5～6 名同队玩家的原生姓名对象容量。
- 邀请处理：清理服务端已失效的本地邀请卡片，保留瞬时错误重试；Runtime 携带不可复用邀请 generation migration。
- 游戏稳定性：暂时停用会造成队友噪声和界面互扰的破坏者队友白圈运行时 Hook。
- 启动兼容：只有 SCBL Launcher 注入进程级数据目录时才启用 SCBL Hooks，官方启动方式保持原版存档路径。
- 更新可靠性：跨通道回执按通道独立收敛；大文件下载支持绑定目标身份、ETag、大小与 SHA256 的安全断点续传。
- 本候选先部署 Runtime 并发布到 test；stable 与完整安装包保持不变，等待多人实机验收。

## SCBL Client / Launcher v2.2.1 (stable/test)

- 更新弹窗改为读取服务器独立更新公告；组件说明继续仅供内部发布审计。
- 修复同批组件更新可能连续重启并再次提示的问题。
- 更新流程移入 Application Coordinator，WPF 只负责展示与宿主命令执行。
- 更新弹窗采用深色滚动条，支持长正文和独立 160 字符标题；日志不再记录被截断的公告原文。
- 公告缺失、未启用、超限或字段无效时在下载前安全停止并允许重试。
- Launcher 只正式构建一次；通过 test 回读后把完全相同的 `72,807,037` bytes / SHA256 `8aaae020...8cfa0` 提升 stable，其他五个客户端组件未重编，完整安装包仍为 `2.1.4`。

## SCBL Client / Launcher v2.2.0 / Hooks v2.0.110 / Route Guard v2.0.5 (stable)

- 更新流程：Launcher 启动发现组件更新时先展示签名清单中的实际双语说明，用户确认后才下载；清单换代会在下载或激活前失败关闭并重新提示。
- 界面与存档安全：修复两层存档覆盖确认各自的 5 秒倒计时；删除 15 个零引用 XAML 资源/样式，Launcher 只嵌入最终背景，正式 EXE 由 80,457,855 bytes 降至 72,783,997 bytes。
- 网络与文件安全：Route Guard/WinDivert 使用受控目录和 5 分钟无 Launcher/游戏心跳退出；停止分发会清空安装根的卸载脚本，升级只清理受信回执拥有且未被修改的旧文件，未知和用户文件保留。
- 通道隔离：test 下 Fun/Serious 永久开启只读 `TickrateDiagnostics`；stable、缺省、未知和非法通道会在启动前强制写回 `false`。
- 装备与存档：扩展装备使用有界 sidecar 保存，Fun/Serious 共享同一原版进度；修复保存缓存偏移导致的读档崩溃，并移除扩展装备启用时无行为的逐帧 Tick Hook。
- 维护精简：移除 Hooks 四个零引用辅助函数、未绑定已确认 manifest SHA256 的旧更新重载，以及无人引用的旧 GitHub 组件发布脚本；签名、事务、回滚、bundle fallback、旧生产认证兼容和安装迁移门禁均保留。
- 最终冻结 Launcher 自动测试 `222/222`；Hooks 审查回归 `79/79`、hooks-config `5/5`、hooks-addresses `6/6`。Launcher/Hooks 仅正式构建一次，Route Guard `2.0.5` 与 Client Files `2.0.23` 复用 test 原字节，再同字节提升 stable。
- 完整客户端 ZIP 仍为不可变 `2.1.4`；本次未部署 Server Runtime、数据库或 Manager，也未操作 GitHub。

## SCBL Client v2.1.3 / Launcher v2.1.3 / Hooks v2.0.93 (stable)

- Launcher：修复诊断导出对 EasyTier 多层转义配置的敏感字段脱敏，避免网络密钥进入诊断包；修复外部复用 EasyTier core 的一次探测抖动被游戏连续性监控误判为停止，同时保持启动与生命周期门禁严格失败关闭。
- Launcher：恢复任何活动公告持续滚动，短公告不再静止；无活动公告时默认口号保持静态，保留悬停暂停与移出恢复。
- Hooks：新邀请至少展示 750 ms，并要求展示后先观察到 F5 释放，避免卡片刚出现就被相邻按键边沿误接受；服务端有界重投不会重置展示期。
- Hooks：默认关闭高频 StormEventDispatcher 逐事件 INFO 追踪，仅在显式诊断配置时启用；迁移关键事件仍保留 INFO，WSA10054 按 socket/error 和 10 秒窗口聚合，功能性 Winsock 强制虚拟 IP Hook 不变。
- 现有客户端仅下载变化的 Launcher 与 Hooks；Route Guard、EasyTier、Updater、Client Files 复用已验收版本和 SHA256。完整包由同一冻结输入装配，仅供新安装与修复。

## 发布工具工作区更新（未部署）

- 新增客户端统一 release plan：自动推导六组件复用/提升/发布动作，一次完成完整包、实际更新公告、manifest、Ed25519 签名和机器可读 QA 回执。
- 加入单写锁、发布前基线、同版本不同内容拒绝、故障回滚、新产物清理和同计划幂等续跑；未变化组件只复用冻结二进制，不重新编译。
- 该工具已通过 Manager 104 项回归，但尚未替换生产 Manager 或用于真实通道发布。

## SCBL Client v2.1.2 / Launcher v2.1.2 (stable)

- 修复生产 Runtime 仍返回旧 Rust secretbox token、但不返回现代 `user` 对象时被 Launcher 误判为登录失败的问题。
- 旧响应兼容只接受规范 Base64URL、固定 nonce 和严格密文长度；不会把旧 token 写入现代 source-bound 会话存储，空响应、部分响应和畸形响应继续 fail closed。
- 已完成 Launcher 定向 `14/14`、全量 `141/141`、Release win-x86 构建、stable/test manifest 与 Ed25519 签名核查，并在 2.1.1 客户端上真实更新到 2.1.2 后确认登录和 DX11 游戏进程接管成功。
- 本次仅发布 Launcher 组件；Hooks、Route Guard、EasyTier、Updater、Client Files 与生产 Server Runtime 均未重编或替换。完整客户端 ZIP 仍为不可变的 2.1.0 包。

## SCBL Client v2.1.0 / Launcher v2.1.0 (stable)

- 设置中新增测试通道入口；持久通道偏好、启动前继续/返回询问、安全回滚及 UAC/Updater/Launcher 重启参数由 ApplicationLayer 统一协调。
- Launcher 可按当前已验签 manifest 中实际缺失的 1～6 个组件请求按需 ZIP，逐项验证大小与 SHA256 后事务写入缓存；服务器尚未提供合并端点时会自动安全回退逐组件下载。
- 配套更新服务端已实现按 channel、manifest SHA256 和排序组件 SHA256 的即时组合缓存，以及单飞、原子发布、容量上限、世代宽限和崩溃恢复门禁；该后端本次未随客户端部署。
- 完成中英文设置菜单、弹窗焦点和 100%/125%/150% 离屏布局复核；英文长菜单项不再裁切。
- 本次只提升并重编 Launcher/完整客户端；Hooks `2.0.92`、Route Guard `2.0.4`、EasyTier `2.6.4-scbl.1`、Updater `2.0.7`、Client Files `2.0.20` 复用已验收产物。

## SCBL Client v2.0.38 / Launcher v2.0.38 / Hooks v2.0.92 / Route Guard v2.0.4 / Updater v2.0.7 (test)

- 完成 Launcher 应用层、EasyTier 服务边界、Updater 事务安装、Dedicated Storage 领域和 Hooks 邀请/呈现边界的阶段性拆分，并加入对应自动回归。
- 客户端组件清单支持轻量 Ed25519 分离签名与可信公钥门禁；控制面身份支持由登录令牌派生稳定用户 ID。
- Route Guard 正式版本改由组件版本清单注入并在构建后自检；共享 proto、组件目录和完整包回执统一使用权威来源。
- 本测试发布为变化后的 Launcher、Hooks、Route Guard 和 Updater 分配新不可变组件版本；EasyTier 与 client-files 内容未变，继续复用既有版本和 SHA256。

## Server Runtime v2.0.21

- 修复 Control Plane 多条心跳线程同时发布玩法模式快照时共用 PID 临时文件、互相移走文件并触发 `FileNotFoundError` 的竞态。
- 每次发布改用同目录唯一临时文件，完成 UTF-8 写入、刷盘与权限设置后再原子替换正式快照；失败路径会清理临时文件。
- 增加 8 路同步发布回归测试并连续执行 20 轮，验证无发布异常、最终 JSON 有效且不残留临时文件。

## SCBL Client v2.0.30 / Hooks v2.0.89 / Dedicated Server v2.0.19 / Server Tool v2.0.16

- 启动器恢复更厚实的微软雅黑视觉和上一版稳定的文字渲染方式，重新统一 DX9、DX11、欢乐模式、严肃模式的等宽间距与选中线比例。
- 选择严肃模式时显示专用确认提示；选择“返回”会静默回到欢乐模式，不写入严肃模式配置。
- 游戏内提示卡收窄，中文主字体由 16px 提升至 18px，减少横向空白并提高可读性。

## SCBL Client v2.0.29 / Hooks v2.0.88 / Dedicated Server v2.0.19 / Server Tool v2.0.16

- 启动器将“娱乐模式”统一更名为“欢乐模式”，并在启动前生成无注释的精确 Hooks 参数组合；欢乐模式仅启用佣兵冲刺垂直视角和队友破坏者范围，严肃模式仅启用枪口遮挡修复、FusionFix 默认 FOV、情报衣破坏者免疫和线上新增装备栏隐藏。
- 服务端为多人大厅、私人房间、邀请与快速匹配增加欢乐/严肃模式隔离；合作模式不受限制，严肃模式配置校验失败会在启动器提示“文件校验失败”。
- 游戏内状态卡加宽并提高文字不透明度和对比度，首次状态会明确显示当前模式；模式不兼容、状态尚未同步等情况提供中英文提示。
- 修复游戏启动超过 10 秒后仍长期停留在“正在启动中”的问题；即使 Ubisoft/Steam 进程发生交接，也会恢复可用于重启游戏的按钮。

## Launcher Component v2.0.23 (test)

- 修复从启动器结束首次游戏后，“检测网络”和“启动游戏”按钮没有重新启用的问题。

## Hooks Component v2.0.28 (test)

- 修复房主已在私人游戏中邀请玩家时，受邀方在加载阶段被单人 Party 恢复事件打断并自动退回大厅的问题。
- 私人邀请从接受到进入比赛或明确退出期间禁止注入“邀请自己”事件；保留冷启动邀请重放及比赛内保护。

## Hooks Component v2.0.27 (test)

- 修复运行时状态机名称包含 ID 后缀，导致比赛状态识别始终失效的问题。
- 进入 `StateMatch` 时取消已排队的单人 Party 恢复，并继续在执行前检查比赛状态，避免二次加入私人房间后被自动送回大厅。

## Hooks Component v2.0.24 (test)

- 修复私人房间加入后约 5 秒自动掉出的问题：延迟的单人 Party 恢复不再于比赛进行中注入事件。
- 恢复逻辑只在大厅控制器离开 `StateMatch` 后执行，保留真正退出房间后的大厅队伍恢复。

## Launcher Component v2.0.22 (test)

- 启动游戏按钮只在公网网络完整连接并取得有效 SCBL 虚拟 IP 后启用；网络准备、连接、重连和失败期间保持禁用。
- 游戏启动过程中锁定启动按钮，避免 Ubisoft/Steam 两段式进程切换期间再次点击触发“重新启动”竞态；游戏运行后仍可随时使用“结束游戏”。

## SCBL Client v2.0.20 / Dedicated Server v2.0.15 / Server Tool v2.0.15

- 完整客户端包新增已安装组件校验记录；首次启动会核对实际文件 SHA256，未改动组件不再全部重新下载，缺失或被修改的组件仍会按清单单独修复。
- 清理已删除的旧版远程更新检查，改为覆盖当前公网组件清单、游戏期间延后提示、启动前强制更新的真实流程。
- 服务端全量包重新编译运行时并纳入密码恢复迁移；发布迁移加入不可变哈希校验，避免后续误改已部署数据库历史。
- 最终全量重编译产物分别发布为 Hooks 2.0.15、Route Guard 2.0.3、Updater 2.0.4，并将 EasyTier 2.6.4 纳入正式组件清单；完整包与公网组件逐项使用同一 SHA256。

## SCBL Client v2.0.19 / Server Tool v2.0.15

- 根目录新增 `卸载启动器.bat`，卸载逻辑位于 `tools/SCBL-Uninstall-And-Delete.ps1`；驱动延迟卸载时自动等待，仍被占用则安排重启后删除，无需再次运行脚本。
- 发布清单新增向旧启动器兼容的 `files` 区域，维护文件作为独立小包按路径下载，不再嵌入 `SCBL.exe`，也不会触发未修改运行组件的重新装配。

- 客户端运行期间每分钟在公网静默检查组件更新；游戏中不弹窗、不下载，退出游戏后再提示，启动游戏前强制确认组件为最新。
- 客户端安装记录加入实际文件 SHA256，自检发现文件缺失或损坏时只重新下载对应组件；旧记录会就地升级，避免安全迁移把未变化组件全部重下；校验移至后台线程，避免界面卡顿。
- Windows 完整编译会查询 EasyTier 官方最新 Release，校验官方资产 SHA256 后打包；服务端新增独立 EasyTier 升级入口、在线玩家保护、健康检查和失败回滚。
- 调整“启动游戏”按钮的中文字体栅格化与像素对齐，改善四字视觉不齐。
- 组件更新改为不可忽略的“更新并重启”事务；下载校验后先停止 EasyTier/Route Guard，再由新启动器在运行环境启动前激活组件，修复 Route Guard 每 60 秒重复提示更新。

## Hooks v2.0.14

- 移除 Storm 事件诊断中对任意 owner 指针的虚表读取，修复 `uplay_r1_loader.dll+0x254247` 访问冲突；保留房主迁移事件、对象地址与事件载荷日志。

## Hooks v2.0.13

- 游戏内提示提高冷白文字、标题和侧边标识的亮度与对比度，同时保留缓慢呼吸效果。
- “不能邀请自己”“已在同一队伍 / 房间”“好友不存在”等操作反馈改为普通 5 秒提示；网络和服务异常仍持续显示。

## Hooks v2.0.12 / Dedicated Server v2.0.10 / Server Tool v2.0.13

- Hooks：重复邀请会明确提示“已在同一队伍 / 已在同一房间”并停止再次跟随；异常失去远端房主且未收到正常大厅恢复时，5 秒后回退到单人大厅，避免成员列表为空。
- Dedicated Server：创建邀请前核对 Party 和房间成员关系，从源头拦截重复邀请。
- Server Tool：设置公告改为打开 `nano` 多行模板；兼容 UTF-8 与 GB18030/GBK 保存内容，并统一转为 UTF-8 原子发布。

## Hooks v2.0.11 / Server Tool v2.0.12

- Hooks：提高游戏内提示文字呼吸亮度变化范围，保持缓慢节奏并提升实际游戏画面中的可见度。
- Server Tool：公告自由文本输入同时兼容 UTF-8 与 GB18030/GBK，避免 Xshell 中文正文触发解码失败。

## SCBL Client v2.0.12 / Hooks v2.0.9 / Updater v2.0.3

- Updater: Launcher 组件更新只备份并替换 `SCBL.exe`，不再停止或重新装配 EasyTier、Route Guard、Hooks 等无关组件。
- Client: 同一份清单同时更新 Updater 与 Launcher 时，先启用新 Updater，再执行 Launcher 替换。
- Hooks: 游戏内卡片统一为 `SCBL CONNECT` 与一行简洁正文，移除旧分类、分隔线和废弃邀请面板；无待处理邀请时按 F5 完全静默。
- Build: Hooks 改为先完成编译与测试、再复制进组件输出，杜绝补丁误带上一版 DLL。

## SCBL Client v2.0.11 / Hooks v2.0.8 / Server Tool v2.0.9

- Release flow: the component manifest is now the only routine update source; full packages are reserved for first install and repair.
- Server: publishing a Launcher component no longer rewrites the full-package manifest; online admission follows the latest stable Launcher component instead.
- Hooks: reduce the in-game card to `SCBL CONNECT` plus one concise status line; F5 is silent when no invitation is pending.
- Client: verified Hooks are promoted to `tools` first, then deployed from that canonical copy immediately before game start.

## SCBL Client v2.0.10 / Hooks v2.0.7

- Launcher: restore continuous scrolling for both remote announcements and the built-in slogan.
- Launcher: keep only the active verified component cache after a successful update; completed packages and obsolete component payloads are removed on the next startup.
- Hooks: replace the generic gray dialog frame with a darker, asymmetric Fifth Echelon information strip and arm the initial status card on the first rendered game frame.

## SCBL Client v2.0.9 / Hooks v2.0.7

- Hooks 2.0.7: start the initial in-game status card timer on the first frame that is actually rendered, so slow game initialization can no longer consume the entire display lifetime before the player sees it.

## SCBL Client v2.0.9 / Hooks v2.0.6

- 公告滚动改由 WPF 合成动画驱动；网络检测刷新到相同公告时不再重置滚动位置。
- 网络检测继续在后台执行，检测期间窗口、动画和公告保持响应。
- 右下角在线玩家、网络检测、延迟三项统一居中与间距规则。
- 游戏内通知改为深色高不透明度的 Fifth Echelon 风格，使用冷灰层级、细边框和左侧识别线，并保留克制的淡入淡出。
- 组件更新仅下载版本变化的文件，校验后原子替换；下载临时文件、备份和解压工作目录在成功或失败后清理。

## SCBL Client v2.0.6

- 启动器正式程序名、Windows 进程名和发布文件名统一为 `SCBL.exe`。
- 正式客户端仅保留 `SCBL.exe`，不再生成或识别旧启动器文件名。
- 服务端主菜单第 2、3 项分别改为“服务端管理”和“客户端管理”。
- 公告管理兼容 Xshell 发送的 UTF-8 与 GB18030/GBK 中文输入，避免中文标题触发解码失败。

## SCBL Client v2.0.5

- 收紧右下角状态栏三段布局，减少项目之间的无效留白，同时继续保证网络检测动画不会推动在线玩家和延迟位置。
- 普通关闭启动器时只结束 Route Guard，保留不接管游戏流量的 EasyTier 隧道；再次启动通过只读健康检查快速复用。
- 客户端更新、网络修复或运行配置发生变化时仍完整停止并重建 EasyTier，避免锁定更新文件或沿用旧配置。

## SCBL Client v2.0.4

- 启动器改为电影感雨夜界面，统一冷灰字体、设置菜单、弹窗、指引和底部状态栏视觉层级。
- 顶部公告改为不限字数的循环滚动显示；固定喇叭与滚动文字使用独立布局。
- 修复密码可见模式下占位文字与明文重叠，并新增恢复码密码重置流程。
- 底部状态栏改为固定列宽和统一矢量图标，网络检测动画不再推动在线玩家位置。
- 右下角统一显示延迟：大厅显示服务器延迟，组队或进入房间并识别房主后切换为房主延迟。
- 左侧网络区域只显示总体连接状态，移除重复且可能失真的固定 UDP 线路文字。

# SCBL v2.0.0

- 将 Windows 客户端、Updater、Route Guard、Hooks、Dedicated Server、Quazal、共享 API crate 和 Linux Server Tool 统一到 2.0.0 产品基线。
- 仓库按 `client / server / shared` 重组；Hooks 与 Dedicated Server 已合并回 SCBL 主项目，并由统一的本地构建脚本生成。
- Windows 客户端升级到 .NET 10，移除旧网络链与 Updater 差分更新冗余，保留稳定的完整包更新路径。
- 启动器不再强制固定测试 Hooks 哈希；测试通道可重复替换 DLL，正式通道仍保留完整性验证。
- 修复私人房间邀请、队伍恢复和房主退出相关状态同步问题，并冻结已通过双机验证的生产 Hooks 行为。
- 修复 Control Plane SQLite 连接泄漏，强化 Dedicated Server 身份校验与异常数据容错。
- WORKPC 端到端验证通过：Ubisoft 重启 PID 收养、Route Guard 严格模式、真实游戏启动、Hooks 会话清理和启动器网络关闭均正常。
- Windows 客户端生成的数据统一整理到 `temp/计算机名/`，并将 Hooks 配置正式切换为标准 `scbl.toml`。
- 删除客户端 0.x AppData/日志配置迁移和已停用的 EasyTier 设置字段；密码配置只接受 DPAPI 密文，服务端生成的隧道密钥仅允许首次引导后转存 DPAPI。
- Route Guard 与 EasyTier 组件改为整组事务替换，任一文件失败时恢复全部旧文件；客户端自动更新拒绝无授权降级。
- 游戏日志、诊断包、下载工作区和版本化组件缓存加入自动保留策略，避免长期运行持续占用磁盘。
- 新增 .NET 10 客户端单元测试，覆盖版本策略、DPAPI、组件事务回滚和存储清理；Windows 工作流统一使用 .NET 10 SDK。
- 客户端右上角改为统一的 `⚙` 设置菜单，收纳指引、语言与声音功能，并允许校验后修改服务器域名或 IP、隧道端口和更新服务端口。
- 设置菜单增加双重确认并自动备份的全解锁存档覆盖、EasyTier/Route Guard 网络修复和脱敏诊断导出；服务器地址保存后自动完整重启网络运行时。
- 公告、客户端更新与组件清单统一使用当前服务器更新端口，并清理未使用的运行时、公告状态和重复网络状态逻辑。
- GitHub 正式发布合并为一个稳定版工作流，同时生成 Windows 客户端、Linux 二进制服务端、完整/补丁部署包和四个独立组件 Release。
- 客户端组件改为“服务端权威、GitHub 仅作上游”：服务端可在线取得单个组件或接受本地上传，客户端仅下载服务端版本高于完整包基线的组件。

## Linux Server Tool v1.0.11

- 修复菜单16从 GitHub 下载测试候选后，在 `/tmp` 与 `/opt` 位于不同文件系统时因 `os.replace` 返回 `Invalid cross-device link` 而无法写入缓存的问题。
- `scbl-test-manager` 与兼容命令 `scbl-invite-test` 固定使用 `/opt/scbl-public/incoming/invite-test/.tmp` 作为下载临时目录，临时文件与最终缓存始终处于同一文件系统。
- 临时目录权限固定为 `0700`；外层 SHA256、同名 `.sha256`、内部 `CHECKSUMS.sha256`、组件来源提交、大小和 SHA256 校验保持不变。
- 该错误发生在正式部署之前，无需回滚；升级后重新选择同一测试候选即可。Windows Client 继续保持 v1.0.14。

## Linux Server Tool v1.0.10

- 主菜单第16项由“邀请 / 组队测试版本管理”升级为通用“测试管理”，不再要求通过 Xshell/SFTP 手工上传测试 ZIP。
- 可直接查看 `caox233/5th-echelon` GitHub Releases 中的测试候选，按编号选择并自动下载、校验和部署，或直接部署最新候选。
- 测试 Release 必须同时提供符合命名规则的 ZIP 和同名 `.sha256`；下载仅允许受信任的 GitHub HTTPS 地址，并限制 API 响应、校验文件、压缩包和解压文件大小。
- 外层 SHA256 通过后，继续校验内部 `CHECKSUMS.sha256`、Hooks 与 dedicated server 来源提交、组件元数据、文件大小和 SHA256。
- 部署继续自动备份 dedicated server、数据库和 test 组件清单；失败时自动恢复，不修改 EasyTier、Route Guard、防火墙、控制平面或 stable 正式通道。
- 新增 `scbl-test-manager` 命令，旧 `scbl-invite-test` 命令继续保留兼容。
- 修复 Server Tool Release 打包阶段在 `pipefail` 下验证压缩包成员时可能因管道提前关闭而失败的问题；PR 仅执行构建验证，正式 Release 仍只在合入 `main` 后发布。

## Windows Client v1.0.14 / Server Tool v1.0.9

- 客户端统一核对 Hooks、Route Guard、EasyTier 和 Updater，只有缺失、大小不符或 SHA256 不一致的组件才会下载。
- Hooks 从 Launcher 内嵌资源移出；正式完整包改为携带独立校验的 bootstrap Hooks，Hooks 更新不再触发 Launcher 重编。
- 新增 `--test` 快捷参数；在正式版快捷方式目标末尾添加该参数即可进入测试组件通道，普通启动仍默认 stable。
- Route Guard、EasyTier 和 Updater 使用版本化缓存，并在下一次同通道启动、相关进程未运行时原子应用和回滚。
- 客户端、Updater、Route Guard、EasyTier、Hooks 和 dedicated server 采用独立构建流水线；正式完整包只组装已验证产物。
- Server Tool 组件仓库扩展为四类客户端组件，支持不可变 test 发布、同二进制 stable 提升、历史回滚和完整 SHA256 校验。
- Route Guard 源码未发生功能变化，继续保留独立版本 v1.0.13；其组件构建不再由客户端版本号变化触发。
- stable 外置组件在签名清单验证完成前仍保持关闭；本版本不修改数据库、EasyTier 拓扑或 Route Guard 数据包策略。

## Windows Client v1.0.12 / Server Tool v1.0.8

- 房主状态栏改为延时与实际 EasyTier 底层线路展示，不再显示没有诊断价值的玩家数量。
- 本机被权威确认为房主时持续测试到固定服务端的延时和 IPv4/IPv6、UDP/TCP/WSS 路径；加入他人房间时测试到房主的延时和路径。
- “检测网络”按钮移动到连接状态标题行，状态正文独占下一行，避免较长的房主延时文字被按钮遮挡或过早截断。
- 控制平面取消单人会话房主回退；普通线上大厅产生的个人会话不再误报“本机房主”，房主判断继续要求多人会话的服务端权威证据。
- 玩家数量仍保留在在线玩家面板、控制平面响应和诊断快照中，但不再进入主状态栏。

## Windows Client v1.0.11 / Server Tool v1.0.7

- 修复 dedicated_server PRUDP 分片顺序判定错误导致游戏首次进入“线上模式”可能长期停在“连线中”的配套发布；专用服务端二进制继续由 `caox233/5th-echelon` 独立滚动 Release 提供。
- 控制平面允许单人房间创建者通过 `/v1/game-session` 获取本机房主状态；全局玩家拓扑仍要求至少两名参与者和两个虚拟 IP，不恢复客户端本地猜房主。
- 客户端控制平面请求明确使用 `Connection: close`，与服务端响应语义一致，避免复用已关闭连接后的超时重试。
- 网络已通过静默只读快检时，启动游戏前不再重复执行 PowerShell 路由绑定；失败时仍保留现有修复路径，游戏运行期间保护边界不变。
- 修复 Server Tool 在线升级遗漏 `scbl_server_diagnostics.sh`，菜单15和 `scbl-server-diagnostics` 会被校验、备份、安装并在失败时回滚。

## Windows Client v1.0.10 / Server Tool v1.0.6

- 控制平面签名改为读取当前 EasyTier 运行配置中的实际网络密钥，修复部分客户端已进入虚拟网但 `/v1/heartbeat`、`/v1/peers`、`/v1/game-session` 持续 HTTP 401 的问题。
- 控制平面401响应增加 `invalid_signature`、`clock_skew` 等原因及服务器时间；客户端可自动校正时钟偏差并重试，服务端按来源和原因限频记录拒绝日志。
- 客户端诊断包补充启动器可执行文件 SHA256，并修复 EasyTier JSON 内嵌 TOML 导致 `network_secret` 未脱敏的问题。
- Server Tool 新增 `scbl-server-diagnostics` 和菜单15，一键收集服务、网络、数据库元数据、崩溃记录和二进制哈希；不复制账号数据库并统一脱敏。
- 7人联机已验证创建、搜索和加入正常；实时房主退出后所有人回大厅仍是已知限制，未宣称完成对局状态接管。

## Linux Server Tool v1.0.5

- 控制平面HTTP响应改为每次请求后主动关闭连接，避免15秒玩家列表轮询与8秒服务端空闲超时反复创建等待线程、刷出大量 `Request timed out` 日志并抬高长期内存占用。
- 增加控制平面内部签名健康自检：每15秒完整访问一次 `/v1/health`；连续两次无法完成TCP、鉴权和HTTP响应时，以失败状态退出，由systemd自动拉起。
- 控制平面systemd策略改为 `Restart=always`，增加任务数和内存安全上限；启动、重启和配置保存时等待 `10.66.0.1:19080` 真正监听后再报告结果。
- 修复 `scbl-server-status` 把 `0.0.0.0:50051` 误报为账号服务未监听；控制平面状态增加最多2秒的启动就绪等待。
- 修复服务端工具在线升级只更新备用控制平面文件、未同步正在运行的 `/opt/scbl-public/control-plane/scbl_control_plane.py` 的问题；v1.0.5首次启动会自动迁移并重启实际控制平面。

## Windows Client v1.0.9

- 服务端路线显示增加三次连续采样确认：首次结果立即显示，后续 UDP/WSS 或 IPv4/IPv6 变化需连续稳定约30秒才更新界面；实际 EasyTier 选路与备用连接不受影响。
- 路线候选变化仍写入日志，但不会让状态栏每个采样周期闪动；单次查询失败也不会清空当前已确认路线。
- 启动和导出诊断包时清理旧版本遗留的 `game-route-status.json`、`game-route-history.jsonl` 与轮转文件；这些本地流量推断记录自 v1.0.5 起已停用，不再混入当前会话诊断。
- 诊断摘要明确标记旧房间路线历史未包含，避免把数日前的多人测试记录误判为本次会话。

## Windows Client v1.0.8

- 修复 v1.0.7 服务端路径查询把 `10.66.0.1` 误判为非客户端地址，导致 IPv4/IPv6 与 UDP/WSS 实际链路信息长期为空的问题。
- 启动器初始化时把 Updater 与 WinDivert 文件哈希、替换检查移出 WPF UI 线程，并显示“正在准备启动器”，改善开机后首次启动的短暂无响应。
- 游戏运行期间若服务器快速检测瞬时失败，先进行一次只读重试；仍失败时仅重新绑定现有 EasyTier 路由并复检，不重启 EasyTier、不重建虚拟网卡，降低进入线上模式时短暂路径抖动留下半失效会话的概率。
- 增加启动准备耗时和游戏内轻量自愈日志，方便后续区分本机冷启动、Windows 路由与服务端/运营商瞬时波动。

## Windows Client v1.0.7

- 控制平面心跳、在线列表、房间查询和启动信息使用独立连接池；超时或瞬时网络错误会重建对应连接并自动重试一次，不再由一个半失效长连接同时拖慢全部接口。
- 心跳改为固定5秒节拍，不再把单次请求耗时叠加到下一次心跳；短暂的 EasyTier 路径切换不会轻易触发20秒在线TTL误下线。
- 在线玩家列表合并服务器心跳注册表、EasyTier 当前路由和玩家探测结果；注册表暂时漏报时仍可从虚拟网络发现玩家。
- 修复 EasyTier 2.6.x verbose JSON 中数值型 `ipv4_addr` 无法解析，导致明明存在路由却退化为扫描整个 `/24` 的问题。
- 不再把 peer 表中的“可用协议集合”误判为当前 WSS 路径；增加 verbose 路径查询时间，避免状态栏长期错误显示 TCP/WSS 兜底。
- Route Guard 在游戏正常结束时不再先记录一条“意外退出”错误，减少诊断日志误报；严格导流行为不变。

## Windows Client v1.0.6

- 修复更新时 `tools/WinDivert64.sys` 被内核驱动占用，导致完整包复制中断且无法自动重启的问题。
- 启动更新程序前先完整关闭 Route Guard 与 EasyTier；Updater 也会停止属于当前客户端目录的 WinDivert 驱动服务并等待文件句柄释放。
- Release 不再直接携带易被锁定的 `tools/WinDivert64.sys`，改为携带 `WinDivert64.payload.sys`，新版启动器在网络初始化前完成校验和原子安装；v1.0.5 的旧更新器也可以顺利升级到本版本。
- 更新器跳过哈希完全一致的文件，对锁定文件执行重试和临时文件原子替换，并在复制失败时恢复启动器/工具备份。
- 更新器增加独立管理员权限清单；更新失败会重新打开原启动器，不再让用户停留在无窗口状态。
- 自动重启不再只判断 `Process.Start` 成功，而会确认新版启动器至少持续运行3秒。

## Windows Client v1.0.5

- 删除基于本地游戏流量和唯一通信对象猜测房主的回退逻辑，房主状态只采用服务端权威会话信息。
- 删除 Route Guard 的房主流量采样、状态文件和历史文件代码。
- 删除对任意 `x.x.x.255`、`255.255.255.255` 和组播地址的自定义转换，以及按最近玩家复制广播为多份单播的逻辑。
- `10.66.0.255` 数据包保持原样交给 EasyTier 官方 UDP 广播中继处理；严格进程路由和源地址恢复继续保留。
- 控制面客户端允许 `tcpPort=null`，修复关闭裸 TCP 入口后 bootstrap JSON 解析失败的问题。
- EasyTier 配置显式启用官方 `bind_device=true`，并刷新运行配置标记。

## Server Tool v1.0.4

- 与客户端 v1.0.5 同步控制面兼容修复和版本元数据。
- dedicated_server 二进制继续由 `caox233/5th-echelon` 的独立滚动 Release 提供；本版本不修改数据库结构和 Hooks。

## Windows Client v1.0.4

- 客户端版本检查在显示失败弹窗前自动执行最多3次，每次都创建新的 HTTP 连接。
- 单次版本检查使用4秒连接超时和7秒总超时，并对超时、网络错误、HTTP 408/429/5xx执行短退避重试。
- 日志新增尝试次数、耗时、错误类型和重试恢复记录，便于定位间歇性双栈或链路故障。

## Windows Client v1.0.3

- 修复客户端更新完成后偶尔没有自动重新打开启动器的问题。
- 更新器改为先启动独立重启等待阶段，等待原更新进程完全退出后再启动新版客户端。
- 重启路径固定优先使用客户端根目录的 `SplinterCellCNLauncher.exe`，并增加20次重试及详细日志。

## Server Tool v1.0.3

- 修复 v1.0.2 服务端完整包遗漏 `scbl_update_server.py`，导致首次部署生成双栈更新服务时中止的问题。
- 一键安装和在线升级现在会校验、保存、备份并恢复双栈更新服务脚本。
- 发布流程增加安装包必需文件和归档内容检查，禁止再次发布残缺 Server Tool 包。

## v1.0.2

- 客户端连接固定服务器时删除裸 TCP 入口，使用 UDP 主入口和 WSS 兜底；玩家间仍保留 UDP 与 TCP 打洞。
- WSS 默认复用 11010/TCP，EasyTier UDP 使用 11010/UDP，减少家庭宽带需要开放的端口号。
- 客户端更新服务改为 IPv4/IPv6 双栈监听，提升监听队列并关闭目录浏览。
- 控制平面监听队列提升到 128，增加连接超时保护并降低游戏会话数据库刷新频率。
- 配合专用 dedicated_server 增加只读会话容量诊断日志，不改变房间人数规则或数据库结构。

## v1.0.1

- 玩家之间继续优先使用 P2P 直连，普通客户端不再承担第三方游戏流量中继。
- 固定服务器保留为唯一中继兜底，并提示客户端优先建立到服务器的直连路径。
- 关闭延迟优先路由，避免为了很小的延迟差异切换到不稳定的多跳路径。
- 房主延迟状态简化为“与房主连接 XXms 延时”，详细路径、抖动和丢包仍保留在诊断日志中。

## v1.0.0 正式版

- 客户端版本确认成为启动器首要流程，版本不一致时必须更新或退出。
- 客户端和服务端统一使用完整包更新，版本由发布文件自动识别。
- 删除客户端和服务端的 Stable Release，正式版本使用清晰的 `[CLIENT]`、`[SERVER]` 标题。
- 删除人工最低客户端版本配置，控制平面自动要求服务器当前发布的客户端版本。
- 首次部署和 Server Tool 升级后自动检查并同步正式客户端。

## Server Tool v0.6.10

- 修复 v0.6.9 DDNS-GO 原生化迁移中 `write_ddns_go_service` 无限递归导致 `SCBL` 卡住的问题。
- 增加 DDNS-GO 服务生成函数的递归回归检查。

# 更新记录

## Server Tool v0.6.9

- DDNS-GO 改为官方原生配置：A / AAAA、DNS 服务商、域名、网卡和 IPv6 匹配规则全部由官方 Web 页面管理。
- 删除 SCBL 自建 IPv4 / IPv6 取址命令、模式强制器和配置监视服务；升级时只迁移引用这些旧命令的配置项，并先生成备份。
- Web 管理自动绑定服务器局域网私有 IPv4 的 9876 端口；没有私有 IPv4 时回退到 127.0.0.1，拒绝 0.0.0.0、公共 IPv4 和 IPv6 监听。
- DDNS-GO 菜单仅保留安装、更新、启动、状态、密码重置和保留配置的卸载。
- Server Tool v0.6.8 已在真实 Ubuntu 26 Server 上完成原地升级验证，Windows 客户端 v0.6.3 可进入线上模式；客户端和 Hooks 保持不变。

## Server Tool v0.6.8

- 修复全新服务器缺少 `service.toml.template` 时，dedicated_server 自动配置把 `SandboxUrl`、`SandboxUrlWS`、`secure_server_addr` 和 `storage_host` 保留为 `127.0.0.1`，导致客户端账号登录成功但游戏无法进入线上模式的问题。
- 首次安装生成与 dedicated_server 默认结构一致且带独立随机票据密钥的配置；升级已有服务器时自动备份，并只修复四个错误的客户端服务地址。
- 服务端状态新增在线配置、内容服务、PRUDP 认证与安全端口检查。
- 控制平面按 UDP 协议检查 21126/21127，不再用 TCP 检测 PRUDP 服务而误报 degraded。
- 不修改、不重编译 Hooks 源码；Windows 客户端版本保持不变。

## Server Tool v0.6.5

- Fixed the piped bootstrap installer appearing to hang after downloading checksums.
- Added bounded download and validation timeouts with visible stage messages.
- Reattached the downloaded manager explicitly to `/dev/tty` before entering the interactive menu.


## Server Tool v0.6.4

- Added explicit rollback for the retained previous Windows client package.
- Added manual rollback to the most recent server-tool upgrade backup with a pre-rollback safety copy.
- Hardened component manifest/file matching and safe bootstrap extraction.
- Added embedded-Python validation before installing a downloaded manager script.

## Client 0.6.3 / Server Tool 0.6.3

- Split Windows client and Linux server-tool versions and release workflows.
- Added GitHub client Release import through the existing package watcher.
- Added verified server-tool online self-upgrade with backup and rollback.
- Retained the newest two locally published client packages.
- Changed Route Guard TCP/UDP fallback owner resolution to precomputed O(1) indexes.


## v0.6.1

- 修复 Linux 服务端菜单选择“1. 首次安装 / 重新安装”后短时间无输出的问题；
- 移除 `set_defaults` 中静默执行的公网 IP 网络探测；
- 进入安装流程后立即显示阶段提示；
- 仅在需要填写公网入口时检测公网 IPv4，并显示最长等待时间与检测结果；
- 保持客户端更新协议、EasyTier、WinDivert 和 Route Guard 逻辑不变。

## v0.6.0

- 公开当前客户端使用的 Hooks Rust 源码快照；
- 使用项目维护者提供的新存档文件替换旧嵌入存档，并固定 SHA256 清单；

- 建立 GitHub 公开仓库结构，保留 `client/`、`server/` 主目录；
- Windows 客户端支持 GitHub Actions 自动编译和 Release 完整包；
- 仅提供客户端本地编译脚本，普通用户从 Release 下载预编译 ZIP；
- 新增 Linux 服务端 Release 一键部署入口；
- 服务端继续下载专版 `dedicated_server`，本地哈希一致时复用；
- 客户端更新检查提前到 EasyTier 启动前；
- 公网更新通过 TCP/18080，失败后通过原私网地址兜底；
- 保留现有更新公告、manifest、差异更新、Updater、回滚和服务端全量包发布流程；
- 第 11 项继续进入 `/opt/scbl-public/incoming/client`；
- 新增源码验证、客户端构建、正式 Release 和 GitHub Pages 工作流。
## SCBL Client v2.0.15 / Hooks v2.0.10 / Server Tool v2.0.11

- 修复 Xshell 中录入中文滚动公告时因文本流与原始字节流混用造成的误判乱码；服务端管理器统一使用 UTF-8 文本输入。
- 修复 Windows 休眠恢复后服务端重复投递邀请、Hooks 却因邀请 ID 复用而静默去重的问题，并在轮询失败时重建 API 连接。
- 游戏内普通提示显示 5 秒，邀请显示 30 秒并可重复提醒，错误保持显示；加入倒计时条和轻微文字呼吸效果。
- 客户端组件校验并应用到 `tools` 后只保留已安装版本记录，删除临时 `components` 下载目录。
