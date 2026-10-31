# 联测包身份与 DX11 定制地址表

Updated: 2026-11-01 (Asia/Hong_Kong). Parent baseline: `d207fc611be3a89351f8517fcbc37aeb18610dd4`.

## DX11-only 表解析：可复现，最小修正等待候选实测

Status: **ready-for-human** for the corrected candidate's actual loader acceptance.
Hosted Windows now verifies the actual eight custom-file controls and five release-admission controls; see the [exact e4 evidence](../evidence/hosted-native-production-e4f8990.json) and [complete candidate](../DEVELOPMENT-CANDIDATE-TESTING.md). This is not a reconstruction of the unknown real launcher/server identities.

用户交接：两台机器、四份运行记录各三条 `missing field dx9`。本地先前已用实际
`SavedAddresses` / `load_custom_addresses` 做 Windows only-DX11 B1/M0/R1；
云端沿用该结论。没有据此推断游戏闪退或匹配失败由该格式问题造成。

源码要求 `SavedAddresses.dx9` 必须存在；合法 DX11-only JSON 因此整份解析失败，
然后原逻辑继续使用内置地址。最小修改仅在 `dx9` 加 `#[serde(default)]`，缺省为空表。
DX11 仍必需；已出现但格式错误的 DX9 不静默忽略；DX11 字段、64字符十六进制摘要和
实际程序哈希选择保持原校验，内置地址对同哈希仍优先。没有新增偏移、地址 pin，
没有恢复 DX9 产品选项或支持 DX9 启动。

新增八项真实文件回归覆盖 DX11-only、旧双表、缺失/坏类型 DX11、出现但畸形 DX9、
错误摘要整表拒绝、必要地址字段拒绝、内置优先、实际文件哈希选行及修改后拒绝。
数据来自已跟踪的地址表和明确标记的合成文件，不调用任何游戏地址。
基线八项五通过/三失败，修改后全包十八项通过；独立 Git 快照执行 B/M/R/RP，
八项分别为五通过三失败 / 八通过 / 五通过三失败 / 八通过，退出 101/0/101/0。
原私人累计四角色证据未改写。精确日志摘要见[本轮收据](../evidence/package-compatibility-feedback-20261031.json)。

## 包身份：待定位，保持严格一致性

Status: **needs-triage** for the new-player screenshot; the November 1 local e4 installation is independently scoped below. 用户先前截图中的 `RELEASE_MISMATCH` 已保留，实际截图对应的
启动器与房主四组件身份尚未证明；不得因为文字显示“SCBL LAN”就归咎玩家旧版或混装。

本地审查补交指出九份运行记录的主 DLL `34183282…` / 玩法 DLL `50ce67c2…`
精确对应 `da1805e` 公开包。云端独立读取并重算两份已下载公开包四角色和目录清单：
`da1805e` 与 `dcb97d0` 的 release/compat 字符串相同但目录摘要不同。
精确 `git diff da1805e dcb97d0 -- src/native` 为空，因此换到 dcb 不能被描述为
该组摄影机崩溃的原生代码修复。仅主/玩法 DLL 匹配不能补出未知启动器或实际房主身份。

生产 `ReleaseIdentity::admit` 按 release、compat、目录摘要依次严格比较；
若仅上述两包互联，预期应是 `PACKAGE_CATALOG_MISMATCH`，不是 `RELEASE_MISMATCH`。
这只解释已知包之间的预期合同，并不证明现场双方使用了它们。

另有房主 protocol10/method1 的 `PACKAGE_IDENTITY_REQUIRED` / `InternalError` 799条，
本地补充审查为不同时间、调用/会话的真实重复请求，不能说是同条日志重复写入，
也不能和新玩家截图视作同一个错误。缺失身份请求与未批准游戏会话的上游调用另行调查，
不关闭认证、不把账号会话升级为游戏会话。

本轮增加真实 Users Login / Register 的完整但不同 release/compat 声明矩阵，分别验证
Account / Official / MOD 请求被相应错误拒绝，账号、last_login、game_runs、tickets 均不变。
它与现有缺失/混合/目录差异、合法声明、签名改动、旧 nonce 等共五项真实 SQLite /
签名声明测试通过。包身份生产代码不改；这不是多人或真实安装验收。

[完整包修复步骤](../COMPLETE-PACKAGE-REPAIR.md) 给出同一内层 ZIP 校验、全新目录、
保留原件和私人 data、正常退出旧房主、从配套服务端创建房主，以及旧组件阻断时的操作。
修复包一致性不等于摄影机、等级匹配或电磁镜性能已修复。原日志与玩家标识不公开。

## 11 月 1 日：本机 e4 身份已明确，不能再归为旧包

用户在本地逐字节核验的两份内层 ZIP 均为
`36228f99ec05938bb20addcb9891987d318991234251214da6c2b8cfda125d6e`，
与公开 `e4f8990` 完整包一致；四个安装组件均对应该包。三次真实运行加载的主 DLL 为
`1d5f661db61ca10954d53a140031e4359e57e5044e811ca50ef56a4f359f9893`，
玩法 DLL 为 `60f6eee1d8c68bd4d1692510128ec4b347434917a4d54f47c587b532f6e7222e`。
本机新启动器未观察到 `RELEASE_MISMATCH`。本轮用户端事实来自已脱敏交接；
云端复核的是已下载官方包，未读取用户 ZIP、安装目录或私人日志。

因此，不再为这台已核实机器建议重复安装，也不把其摄影机崩溃归为旧包/混装。
这不补全另一位新玩家截图中尚未知的两端身份，也不宣称该玩家准入问题已解决。
包身份通过不能代替摄影机、快速匹配、多人聊天或性能验收。

用户另以三个真实 ZIP 的 catalog 调用 e4 生产 `ReleaseIdentity::admit`：
SCBL LAN 1.1 与 Fifth Freedom e4 双向为 `RELEASE_MISMATCH`；前一个 Fifth Freedom
候选向 e4 为 `PACKAGE_CATALOG_MISMATCH`；e4 对 e4 为 `Ok`。这是用户完成的离线
合同核验，不是云端重新运行，也不是未知截图现场身份的证明。

新增启动器修复提示仅在登录/注册明确收到两种拒绝时区分“发行版本不同”和
“完整包目录校验不同”，再指导房主与加入端从**同一个完整 ZIP**启动配套组件。
RPC 必须是 `FailedPrecondition` 且 detail 是精确 reason 或现有 `REASON: explanation`
格式；其它状态、嵌入子串或相似前缀均不改写。兼容字符串相同不会放行，
原生准入、账号/存档和 UI 布局均未改。

真实链接生产 `AuthService` 的纯内存 transport 回归共 43 项：基线 27 通过/16 失败，
修改 43 通过；隔离源码回滚/重应用仍为 27/16 与 43/0，退出 1/0/1/0。
覆盖 Login/Register 的 RPC 与 response 分支、两种原因、有无说明、未知代码/相似子串、
Unavailable/Cancelled/Internal、成功路径和 capability 取消。Python source-suite wrapper
实际执行 C# 程序；未启动任何服务器或游戏。既有 `FinalLaunchPreparation` 只重新构建，
未运行其 loopback 服务测试。详细输出摘要见[11/1 收据](../evidence/package-capability-feedback-20261101.json)。

六条 capability `Unavailable`、一条 `Cancelled` 及首次未发现可用房主另列
[LAN capability 与首次发现](lan-capability-discovery-feedback.md)，不并入包版本拒绝。
