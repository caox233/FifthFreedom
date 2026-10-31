# 联测包身份与 DX11 定制地址表

Date: 2026-10-31 (Asia/Hong_Kong). Parent baseline: `d207fc611be3a89351f8517fcbc37aeb18610dd4`.

## DX11-only 表解析：可复现，最小修正等待候选实测

Status: **ready-for-human** for the corrected candidate's actual loader acceptance.
Hosted Windows verification of this revision is still pending here; use the final
candidate delivery receipt for the actual hosted result.

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

Status: **needs-triage**. 用户截图中的 `RELEASE_MISMATCH` 已保留，实际截图对应的
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
