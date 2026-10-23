# Windows/LAN 与 Linux/公网整合

状态：Windows LAN、Linux 基线/供体/受保护控制面组件与同轮跨平台包身份校验已通过
GitHub-hosted 检查；真实 Linux 接口隔离检查也已通过。
**完整公网服务及部署验证仍未完成**。当前执行收据见下表、
[CI-01](tasks/ci-01.md) 和 [云端进展](CLOUD-PROGRESS.md)。
目标固定为 Windows 服务端用于局域网、Linux 服务端用于公网，不沿用 donor 文档中
“新部署默认 Windows 公网”的旧产品选择。所有路径相对于仓库根。

## 1. 实际已有的两组实现

| 部件 | LAN基线 | 3.0 donor |
|---|---|---|
| 游戏后端 | `src/native/server/dedicated-server` | `integration/scbl3/server/dedicated-server` |
| 协议 | 同侧 `quazal`、`sc_bl_protocols`、`shared/api` | 相同crate名称，不代表版本相同 |
| 运行入口 | `main.rs` 强制验证 release package，初始化/使用LAN数据目录，`lan_runtime`，OB策略，必要服务退出监督 | 旧公网Dedicated、独立控制面和更新面 |
| Windows管理 | Launcher拥有的 `HostRuntimeService`，显式创建/加入、初始化、就绪/退出确认 | `server/windows` 是WPF Manager+Windows Service Host+Updater，含EasyTier/控制面/更新服务 |
| Linux管理 | 没有已整合的公网安装器 | `server/bootstrap`、`server/manager`、`server/packaging`、模块化控制面、更新服务 |

donor Windows管理器并不是“更完整的LAN后端”。不能直接替代现有LAN host而暗中新增公网
监听、下载、驱动、管理员权限或自动服务；它可提供原子安装/回滚设计参考，当前用户目标不需
同时维护第二套Windows公网产品。

## 当前实现入口

下表使用现在实际任务编号。第 3 节和第 7 节保留最初规划，其阶段编号不代表当前同名任务的范围。

| 当前任务 | 已实现的边界 | 仍需完成 |
| --- | --- | --- |
| [SERVER-01](tasks/server-01.md) | 显式 LAN/Public profile、保留 LAN 默认及 Public 拒绝入口 | 不能单凭 profile 打开公网服务 |
| [SERVER-02](tasks/server-02.md)、[受保护传输](tasks/public-transport.md) | 原生签名令牌、当前数据库准入、HTTPS 只读会话服务 | 与实际公网进程/来源边界集成 |
| [SERVER-03](tasks/server-03.md) | Linux 私有密钥、运行代际观察者授权、数据目录互斥 | 公网启动事务复用这些已验证边界 |
| [SERVER-04](tasks/server-04.md) | Linux 服务身份与原始 Windows 客户端 catalog 分离；真实同轮产物校验通过 | 不代替完整资源包或运行验收 |
| [SERVER-05](tasks/server-05.md) | 原生只读部署预检接入真实包、密钥和服务配置；真实同轮 hosted 产物预检通过 | 报告仍为 `startup_allowed:false`，不代表服务已启动 |
| [SERVER-06](tasks/server-06.md)、[隔离检查](tasks/socket-isolation.md) | 全部原生 socket 使用明确策略；GitHub 三命名空间入口隔离通过 | 覆盖网络存活监管和受控启动接入 |
| [SERVER-07](tasks/server-07.md) | 固定 EasyTier 源码审计；实际会话负例与最小候选修复 | 完整所选入口合同、独立构建身份和自有进程生命周期 |
| [SERVER-08](tasks/server-08.md) | 原生 API、PRUDP、HTTP 和票据入口在处理请求前执行明确来源网段约束 | 与受控 Public 启动共同启用；LAN 行为保留 |
| [UI-05](tasks/ui-05.md) | 启动器受保护 HTTPS 会话读取、严格 TLS 和运行代际约束 | 实际自有 native token 回传与覆盖网络路由接入 |
| [SERVER-09](tasks/server-09.md)、[SERVER-10](tasks/server-10.md)、[SERVER-11](tasks/server-11.md) | 精确子进程所有权、原生父进程许可与五监听端口退出监管 | 实际同轮候选 main 进程生命周期验证；测试进程不代替完整启动 |
| [SERVER-12](tasks/server-12.md)、[SERVER-13](tasks/server-13.md) | 明确私有网络配方、固定加密源码与隔离探测；真实 nft 预检及构建通过 | 正确密钥、错误密钥与恢复的实际三种传输矩阵仍有失败 |
| [SERVER-14](tasks/server-14.md)、[SERVER-15](tasks/server-15.md) | 自有 core/CLI 的实时观测已接入许可，实际组合探测和云端作业已实现 | 真实覆盖网络与原生服务组合的两项撤销控制尚未通过；普通 Public 仍关闭 |
| [UI-06](tasks/ui-06.md)、[UI-07](tasks/ui-07.md)、[UI-08](tasks/ui-08.md) | 自有启动器/原生命名管道认证回传、最终登录和可撤销连接约束 | Windows 自有适配器会话与安全 peer 存活链；具体运行结果按提交记录 |
| [UI-09](tasks/ui-09.md) | 固定 Windows 后端与 EasyTier 调用方的真实会话观察补丁，配对源码构建作业已注册 | 完整 MSVC 构建和实际 Windows 会话验证尚未通过；局部类型检查不代替它们 |

这里的通过范围分别对应源码、库测试、实际组件和隔离拓扑；没有启动游戏或现网服务。
最新运行编号和精确提交统一记录在 `PROJECT-STATE.json` 和 CI 收据，后续实现不能借用旧结果。

## 2. 已核实的协议和安全差异

1. LAN `src/launcher/src/users.proto` 和 `src/native/shared/api/proto/users.proto` 增加
   `GetCapabilities`；Login/Register携带 `client_product`(10)、`package_identity`(11)，
   capability含必要端口、ready、官方模式及 `observer_allowed`。
   donor `shared/api/proto/users.proto` 不包含这些字段/RPC。不能只换服务器二进制。
2. LAN登录前 `LanConnection.RefreshAsync` 验证 wire product、protocol_version和各监听就绪，
   `ProductContract`是独立稳定wire身份。Fifth Freedom显示名不能替代这些常量。
3. LAN `main.rs` 在数据创建/数据库变更/监听前调用 `verify_server_package`；运行时使用
   `Storage::init(...verified_release...)`。保留release admission与兼容性校验，不为适配旧公网
   客户端而把它们永久禁用。
4. donor控制面有token签名、有效期、source IP、auth_version/数据库撤销校验；公网整合应
   保留并测试，而不是把LAN直连HTTP认证直接暴露公网。LAN `ServerAddress.Parse` 当前只接受
  可信LAN的IPv4/域名HTTP，明确不是公网安全接口。
5. donor `PublicTunnelConfig`/部署文档含旧默认站点、私有示例地址与布局。
   发布前改成占位配置/环境输入，不自动连接旧部署，不把密钥放源码或日志。
6. 两侧存储/migration也有差异；数据库不可直接互换或覆盖。只在合成数据库上做升级/回滚测试，
   用户账号和存档不随源码上传。最新LAN修复是迁移基线，donor不是新版本的当然上游。

## 3. 最初规划的实现顺序和明确边界

### 原阶段 SERVER-01：协议与配置接缝

- 先保持LAN后端源码为唯一游戏业务实现，提取显式 `DeploymentProfile = Lan | Public` 配置，
  用依赖注入/配置工厂替代 `main.rs` 对LAN初始化的硬绑定。
- Lan配置保留当前端口选择、就绪、release admission、OB许可及拥有者生命周期；不要改变
  既有数据库迁移。Public配置接入donor部署/控制面的监听、数据目录和账号令牌约束。
- 合并protobuf以LAN字段编号为基线，新增版本/能力协商，不重新编号；为旧/新客户端及缺少
  capabilities、错误package_identity、未知protocol添加明确拒绝/升级结果测试。
- 为控制面所读数据库字段列兼容表；若模型不同，写只读查询适配或显式迁移，不共用未验证SQL。
- 这一步只需合成输入、临时目录/loopback测试；不部署服务、不读取真实账号库。

### 原阶段 SERVER-02：Linux公网生命周期

- 迁入donor Linux Manager的校验ZIP、不可变Runtime、备份、切换、失败回滚、systemd管理。
  连接上述单一游戏业务构建产物，不长期发布两套漂移的Dedicated。
- 控制面只在覆盖网络/规定的受保护入口可达；更新面不拿游戏身份密钥；认证、限流、上传/下载
  路径和manifest校验要做负面测试。隧道/域名/端口用部署配置，不继承开发者现网端点。
- 留出LAN与公网独立包清单及明确兼容ID；分支开关不能绕过已验证玩法模块的包匹配。

### 原阶段 SERVER-03：UI公共适配器接入

- `UI-INTEGRATION.md` 的Public adapter执行公网网络就绪、受保护账号流程和能力协商，
  然后把会话交给保留的安全启动管道。
- 把donor Fast/Compatible作为Public内部传输选择，不能解释为Windows/Linux或Host/Join。
- 切换环境前确认没有运行中的游戏/owned启动；失败回退原环境，不跨环境复用密码或登录状态。

## 4. 当前构建入口及CI差异

| 入口 | 已有源码规定 | 迁移后需要 |
|---|---|---|
| `scripts/build.py all` | Windows四组件构建/测试/打包；.NET10、MSVC x86、固定Rust、protoc、默认资源 | 保留作为Windows LAN/客户端基线；不要在Linux声称可完成全包 |
| `scripts/build.py test` | build-entry、PortPlan、BackgroundTaskRegression、ObserverSession及native回归 | 拆纯逻辑与Windows测试，保留现有断言 |
| 根 `.github/workflows/candidate-build.yml` | 当前为 PR/manual 开发候选构建，保留独立完整资源门禁 | 继续补实际公网运行合同；不自动 Release 或部署 |
| donor `.github/workflows/ci.yml` | Linux Python/manager/contracts、Windows launcher/hooks/server、Linux Rust/Go路由测试 | 嵌套目录不会自动注册到根Actions；提升经过路径改写的job，而不是宣称已有云CI |
| donor `.github/workflows/build.yml` | Windows launcher/updater/hooks/windows-server；Linux dedicated独立job | 只保留目标需要的job，固定actions版本，移除重复产品发布 |
| donor `server/packaging/scbl-server.py full` | Linux完整服务端组件ZIP | 接入新业务源/配置/manifest后独立测试，不改为生产机现场编译 |

当前 donor commands 都以它自身为工作目录。初期可显式 `working-directory: integration/scbl3`
做保留测试；根`src/native`使用独立Cargo workspace。两者同名crate不能在同一次root Cargo
命令中混合。整合后删除重复实现前必须证明契约覆盖，不只做逐文件合并。

最初规划的根 CI 范围（具体已实施 lane 与运行结果以 CI-01 为准）：

- Linux：Python编译/manager与协议合同、`cargo test --locked -p dedicated_server -p quazal -p sc_bl_protocols`
  （从选定workspace执行）、公共API合成fixture、源码隐私/资源清单检查。
- Windows：保留 `python -B scripts/build.py all`、WPF smoke、包哈希与隔离回滚；只上传候选artifact。
- 分离的包验收：清洁checkout与许可资源装配、manifest版本一致、未含data/私钥/转储/个人存档，
  禁止未授权GitHub Release、生产部署、真实游戏自动启动。

注意：源码中存在可条件编译的Windows资源处理，不能仅凭此断言完整LAN Dedicated在Linux
已经通过；其运行入口仍是LAN模型。先编译/测试再判断配置、监听、锁、权限、SQLite等差异。

## 5. 必须保留的回归

- LAN账号登录/注册/恢复码、package身份准入、官方/MOD隔离、OB许可/8人玩法配置。
- host ready必须等必要监听就绪；配置失败/端口冲突/子服务退出不得留下假绿色状态。
- 创建/加入切换和关闭只停止拥有者进程；拒绝错误路由和失效凭据作用域。
- 公网token过期/撤销/来源不符/错误签名、缺失密钥、路径穿越、损坏ZIP、迁移失败和回滚。
- 已接受的native gameplay、镜模/变倍/聊天/声音/设备行为留在新客户端基线，不用旧donor替代。
- 仍待办的三端聊天、历史闪退根因、间歇镜模等继续存在；迁移不等于关闭这些BUG。

## 6. 云端可以和不能证明什么

Linux Codex环境可修改源码文档、运行纯逻辑/Python/Rust Linux测试、生成Linux服务端候选。
Windows GitHub runner可构建/打包Windows启动器及x86 native并跑可自动化Windows测试；
实际运行权限、资源、SDK可用性仍需CI日志证明。

Linux云端不能代替WPF实际界面、IME/DPAPI、管理员驱动/防火墙、DX11 GPU/游戏模型、声音、
多台玩家真实联网、公众网络NAT和实际用户体验的验收。服务器编译成功不等于公网可用；
离线/GPU回放不等于新包实机通过。集中出包后由用户测试未通过/受影响项目，不要求重做全部旧项。

本页最初审计未执行构建；后续真实云端构建结果已记录在上述任务收据中。
没有启动游戏、部署现网或正式发布；迁入 donor 不代表已经完成最终架构。

## 7. 最初云端任务清单（保留规划记录）

- **SERVER-01A**：添加两份protobuf差异契约测试，固定现有字段号和兼容拒绝行为；
  再实现单一 `DeploymentProfile` 配置边界。限定改动在游戏后端启动/配置/API及其测试，
  不动已经接受的客户端gameplay。
- **SERVER-01B**：列出donor控制面实际查询的表/字段与新存储对应关系，用合成SQLite建库/升级/回滚。
  缺少映射时让测试失败，不“临时”读真实用户库补数据。
- **CI-01**：在根 `.github/workflows` 注册Linux源码/服务端lane和Windows候选lane，
  先只PR/manual运行、只保存artifact、不部署/发布。嵌套donor测试用明确工作目录。
- **SERVER-02**：配置/协议回归通过后接Linux安装包，制作运行/升级/回滚文档和受保护配置模板，
  不把用户电脑当持续服务端，也不自动部署现网。

以上保留最初交接的任务定义。云端已实际实现并运行前述组件与合同检查，后续继续按
`PROJECT-STATE.json` 的具体下一步推进。Windows 编译与验证由 GitHub-hosted runner 执行，
不依赖用户电脑开机；没有平台持续 Goal 接口时也不把下一步文档说成后台调度器。
