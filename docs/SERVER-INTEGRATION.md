# Windows/LAN 与 Linux/公网整合

状态：Windows LAN 与 Linux 基线/供体组件已通过 hosted 构建；生产认证桥已实现，
**完整公网服务及部署验证仍未完成**。本页保留最初整合合同，当前执行收据见
[SERVER-02](tasks/server-02.md)、[CI-01](tasks/ci-01.md) 和 [云端进展](CLOUD-PROGRESS.md)。
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

## 3. 建议的实现顺序和明确边界

### SERVER-01：协议与配置接缝（下一条服务器实现任务）

- 先保持LAN后端源码为唯一游戏业务实现，提取显式 `DeploymentProfile = Lan | Public` 配置，
  用依赖注入/配置工厂替代 `main.rs` 对LAN初始化的硬绑定。
- Lan配置保留当前端口选择、就绪、release admission、OB许可及拥有者生命周期；不要改变
  既有数据库迁移。Public配置接入donor部署/控制面的监听、数据目录和账号令牌约束。
- 合并protobuf以LAN字段编号为基线，新增版本/能力协商，不重新编号；为旧/新客户端及缺少
  capabilities、错误package_identity、未知protocol添加明确拒绝/升级结果测试。
- 为控制面所读数据库字段列兼容表；若模型不同，写只读查询适配或显式迁移，不共用未验证SQL。
- 这一步只需合成输入、临时目录/loopback测试；不部署服务、不读取真实账号库。

### SERVER-02：Linux公网生命周期

- 迁入donor Linux Manager的校验ZIP、不可变Runtime、备份、切换、失败回滚、systemd管理。
  连接上述单一游戏业务构建产物，不长期发布两套漂移的Dedicated。
- 控制面只在覆盖网络/规定的受保护入口可达；更新面不拿游戏身份密钥；认证、限流、上传/下载
  路径和manifest校验要做负面测试。隧道/域名/端口用部署配置，不继承开发者现网端点。
- 留出LAN与公网独立包清单及明确兼容ID；分支开关不能绕过已验证玩法模块的包匹配。

### SERVER-03：UI公共适配器接入

- `UI-INTEGRATION.md` 的Public adapter执行公网网络就绪、受保护账号流程和能力协商，
  然后把会话交给保留的安全启动管道。
- 把donor Fast/Compatible作为Public内部传输选择，不能解释为Windows/Linux或Host/Join。
- 切换环境前确认没有运行中的游戏/owned启动；失败回退原环境，不跨环境复用密码或登录状态。

## 4. 当前构建入口及CI差异

| 入口 | 已有源码规定 | 迁移后需要 |
|---|---|---|
| `scripts/build.py all` | Windows四组件构建/测试/打包；.NET10、MSVC x86、固定Rust、protoc、默认资源 | 保留作为Windows LAN/客户端基线；不要在Linux声称可完成全包 |
| `scripts/build.py test` | build-entry、PortPlan、BackgroundTaskRegression、ObserverSession及native回归 | 拆纯逻辑与Windows测试，保留现有断言 |
| 根 `.github/workflows/release.yml` | 审计时是Windows构建；tag push或manual可接发布job，manual publish默认true | 用户仅授权源码上传，先改为开发候选CI，不自动Release；独立复审后才发布 |
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

## 7. 云端第一批服务器任务

- **SERVER-01A**：添加两份protobuf差异契约测试，固定现有字段号和兼容拒绝行为；
  再实现单一 `DeploymentProfile` 配置边界。限定改动在游戏后端启动/配置/API及其测试，
  不动已经接受的客户端gameplay。
- **SERVER-01B**：列出donor控制面实际查询的表/字段与新存储对应关系，用合成SQLite建库/升级/回滚。
  缺少映射时让测试失败，不“临时”读真实用户库补数据。
- **CI-01**：在根 `.github/workflows` 注册Linux源码/服务端lane和Windows候选lane，
  先只PR/manual运行、只保存artifact、不部署/发布。嵌套donor测试用明确工作目录。
- **SERVER-02**：配置/协议回归通过后接Linux安装包，制作运行/升级/回滚文档和受保护配置模板，
  不把用户电脑当持续服务端，也不自动部署现网。

这些是接下来在云端实现的任务，不在当前本机回合修改产品代码。Windows编译可通过
Windows runner单独完成；云端交接只有实际checkout和实际测试/构建进程结果才算启动成功。
