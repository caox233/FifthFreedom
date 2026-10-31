# LAN capability 与首次房主发现反馈

Updated: 2026-11-01 (Asia/Hong_Kong). Status: **needs-triage**.
Source reviewed: `e4f899072c53e2ea1079b8dd1e54616026357b21` and the retained current implementation.

用户交接包含六条 `LAN capability check` 的 `Unavailable`、一条 `Cancelled`，以及
首次未发现可用房主。这是脱敏计数与现象，不是云端复现；当前没有足够关联信息证明它们
属于同一操作、同一端点或同一原因。不将其归为版本、网卡、网络隔离或防火墙故障。
不索取重复日志、不联系玩家、不启动服务或游戏。

## 已核实的实际代码路径

| 路径/结果 | 代码含义 | 不能据此断言 |
| --- | --- | --- |
| `AuthService.CheckServerReachableAsync` → `LanConnection.ConnectAsync` → `RefreshAsync` | 解析 IPv4，选择并绑定本机来源，实际 gRPC `GetCapabilities`，再检查 config/content HTTP 端点。仅全部通过才返回 Ready。 | 失败不等于包版本不同；这个 RPC 不携带包 identity。 |
| gRPC `Unavailable` | RPC/客户端传输报告不可用，异常最终进入 capability 日志并返回 false。可能包含底层失败，但本次只有状态计数。 | 不能指定为端口、防火墙、线路、服务未启动或玩家旧包。 |
| gRPC `Cancelled` | `RpcException` 的状态；当前一般异常分支保留其错误文本并返回 false。 | 不能仅凭该文字认定用户点击取消、窗口关闭或某次超时。它也不是 `NetworkEnvironmentStatus.Cancelled`。 |
| 调用方令牌触发 `OperationCanceledException` | `AuthService` 仅在令牌确实已取消时重新抛出；窗口关闭路径识别并结束，不发布成功。 | 不能把未归因的 gRPC Cancelled 自动等同此路径。 |
| `LocalHostDiscoveryStatus.Completed` 且无有效 host | 所选 IPv4 的 UDP 广播查询完成有限观察窗，没有通过协议校验的回复；UI 展示“本轮未发现”，可重扫/手动/确认创建。 | 不证明网络上没有房主，也不证明 capability/API 不可达。 |
| discovery `TimedOut` / `NetworkError` / `InvalidEndpoint` | 超出总时限、socket/网络错误或地址无效；返回独立错误状态，UI 说无法确认是否有房主。 | 不能变成成功空扫描或自动开服。 |
| discovery `Cancelled` / `Superseded` | 调用方取消、切换/重扫/关闭导致的失效 generation；不发布迟到选择。 | 不等于上述 capability RPC 状态。 |
| `RELEASE_MISMATCH` / `PACKAGE_CATALOG_MISMATCH` | Login/Register 在 capability 之后携带完整包声明，服务器 `release_for_login` / `ReleaseIdentity::admit` 明确拒绝。 | 不能由扫描无回复或 Unavailable 推导，也不能因 compat 相同而放行。 |

`LanConnection` 的 TCP connect timeout 为 5 秒，capability RPC deadline 为 6 秒，
HTTP client timeout 为 8 秒；这些不是单次 UI 操作的统一总耗时，多个 IPv4 候选还会依次尝试。
`ConnectAsync` 保留最后异常作为 inner error；`AuthService` 目前向 UI 汇总为 bool，日志为
异常 message。现有计数没有具体 RPC/HTTP 阶段、底层错误类型、受控操作 generation 或
相对时间，不能反推真实起因。

发现使用 UDP 47777、所选网卡的全局/子网广播，绑定自有临时源端口；正常观察 2 秒，
flow 总上限 4 秒。回复必须符合 Version=1、ServerReady、HostIP 等于 UDP 来源、有效
API 端口及文本边界。畸形/未就绪回复不计为可用房主；因此空结果准确说的是“无有效回复”。
发现只是提示，不是认证或包身份准入。手动输入后仍走 capability 检查；确认之后才连接或创建。

`TryPublishLanCapabilities` 校验同一个 owned lease、端点、绑定和当前环境；发现结果还检查
UI generation、10 秒选择期限、切换/关闭及绑定变化。这些源码边界仍保留，未重新设计 UI。

## 本轮验证与下一动作

本轮 43 项实际生产 `AuthService` 分支回归中包含 in-memory `Unavailable`、`Cancelled`
连接异常和预取消令牌三项控制：前两项返回 false，预取消重新抛出且没有连接调用。
这些是分类边界测试，不是 LAN 真实故障复现，不建立网络根因。
已有 `tests/HostDiscoveryFlow/Checks.cs` 覆盖零/一/多回复、协议拒绝、socket 错误、超时、
取消、过期及重扫；本轮只读审查其覆盖，没有重跑其真实 loopback socket 场景，也没有新增
源代码文本断言充当运行验收。未确认新的生产逻辑缺陷，故不猜测修复。

若后续已有脱敏事件关联可用，先区分是扫描阶段还是 gRPC/HTTP 阶段，并关联同一次操作的
开始/结束、状态代码、内层异常类别及是否确有调用方取消；只需要这些最小字段，不需要私人
地址、账号、口令、存档或原日志。随后以已确定触发条件设计纯拥有的故障注入回归。
在此之前维持 **needs-triage**，继续独立完成原六项目标与完整候选交付。

证据：[package-capability-feedback-20261101.json](../evidence/package-capability-feedback-20261101.json)。
