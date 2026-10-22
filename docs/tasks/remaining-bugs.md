# 保留待办：迁移与修复并行

迁移不再等待所有 BUG 完成。这里保存未完成问题和所缺证据，不重新要求已通过项目。
完整 14 项及其最新通过边界见 [ACCEPTANCE.md](../ACCEPTANCE.md)。

标签：`needs-triage`、`needs-info`、`ready-for-agent`、`ready-for-human`、`wontfix`。
`ready-for-human` 只表示最后一段依赖真实设备/多人配合，不表示云端可以忽略其它开发。

## 当前优先缺陷

### `vss-scope-lifetime` — 间歇丢枪上附镜/ADS 镜模

- 标签：`ready-for-agent`、`needs-info`。
- 已做：view 0/1 同对象资源补全、本地控制器/viewport 入镜就绪判断及离线回归。
- 未做完：其它异步加载、复活/切局生命周期的故障闭环；不能以 VSS EMF 已正常结案。
- 入口：`src/native/client/hooks/gameplay/src/vss_attachment.rs`、`scis_scope_actor.rs`、`scis_preload.rs`。
- 下一步：延续已有资源身份、owner generation、FIFO/lease 证据，针对缺失的生命周期
  建立源码与原生失败回放。不要盲目重复创建配件、写共享定义或把“非空指针”视为已绘制。
- 完成证据：正常/ADS 引用和实际显示在目标生命周期一致；回归无多余 FIFO 入队；用户集中确认。

### `multiplayer-crashes` — 两次历史闪退

- 标签：`needs-triage`、`needs-info`。
- 已做：确认两次故障，改善 fault context 和导出关联，保留原日志/转储及模块身份。
- 尚未确认：哪个写入方造成损坏、具体根因、修复与反事实回放。
- 入口：`src/native/client/hooks/src/exception_record.rs`、`src/launcher/src/Services/NativeExceptionExport.cs`。
- 下一步：按已存证据的实际覆盖范围重建故障链；没有保留的内存不能推断为已知损坏来源。
  原始转储留私有，公开任务只使用脱敏摘要。不要发布“已修复闪退”的公告。
- 完成证据：能解释并复现原因的失败例、最小修复、回滚和相关生命周期验收。

### `chat-peer-relay` — 加入者消息未到其他加入者

- 标签：`ready-for-human`。
- 已做：房主中转及 391 项原生检查，排除发送者/房主，保留原生团队过滤。
- 待验证：真实第三客户端的收件；同阵营收到团队消息、敌方不收到；中文内容正常显示。
- 入口：`src/native/client/hooks/gameplay/src/chat_relay.rs`、`goggle_feedback_native.rs`。
- 完成证据：至少三客户端、全体/团队/敌方对照；发送成功或房主看到都不足以证明第三端收到。
  不记录聊天正文、账号或普通按键。不得把仅两台机器结果填为三端通过。

### `diagnostic-export` — 受影响用户机导出

- 标签：`ready-for-human`。
- 已做：合法完整 hex byte array 的分类修复；真实导出执行与 24 回归通过。
- 入口：`src/launcher/src/Services/DiagnosticTextSanitizer.cs`。
- 待验证：曾报 `InvalidDataException` 的用户机可导出且包可读；畸形输入仍拒绝。
- 迁移要求：3.0 UI 的导出按钮必须调用保留实现，不能回到旧错误路径。

## 已实现但仍保留的验收待办

| ID | 标签 | 待补内容 |
| --- | --- | --- |
| `scis-sound` | `ready-for-human` | 听距增大后的双端近/远距离对照；保留本机“已响且变大”的通过。 |
| `sniper-kill-icon` | `ready-for-human` | VSS 击杀图标实际显示；SC-IS/VSS 共用规则不应显示原始标记字符串。 |
| `damage-armor` | `ready-for-human` | SC-IS 穿甲弹/无穿甲弹、护甲、部位、距离矩阵实机验收。保留既有 357 项离线合同。 |
| `vss-copy` | `ready-for-human` | 新无穿甲弹基准：消音 PFS-12 基础伤害与 1.5 倍衰减距离；UI 威力/射程及预览持枪位置。普通/20 发长弹匣已做，不倒退成四发弹夹。 |
| `rfd-log-repair` | `ready-for-human` | 用户原漏显场景核对；保留 unknown receipt 离线修复，不把未知源直接视为未暴露。 |
| `drone-exit-audio` | `ready-for-human` | 退出后控制循环音消失、空间声音和再次操控保留。 |
| `drone-dead-spy-mark-audio` | `ready-for-human` | 尸体不发标记声，活目标仍正常，别的来源标记不误清。 |
| `sticky-camera-ats` | `ready-for-human` | 另一端 ATS 按设备所在位置识别消音发声；摄影机操作本身已通过。 |
| `vss-ats` | `ready-for-human` | 远端 VSS 的消音声源与 ATS 识别，含 null-logic 兜底场景。 |
| `ats20-version-notes` | `ready-for-human` | 消音声波 20 米边界真实视觉确认；不要再把原生回放当作用户实测。 |
| `f9-suicide` | `ready-for-human` | 真实多人死亡请求/10 秒冷却及聊天、菜单、失焦、死亡防误触；不自动触发真实角色死亡。 |

## 不重新打开的通过结果

- `aim-mouse`、`scis-ads-entry-pop`：UNIFIED25 本次指定视觉检查用户未见明显问题。
- `scis-reticle-breathing`：世界不随镜晃动及射击后自然回正结果保留；不假造更广压力验收。
- `scis-held-fire-animation`：长按、松键、连点已用户确认正常。
- `mac11-hud-icon`：手枪 HUD 已用户确认。
- `chat-delivery-ime`：本机中文输入、装备重命名、简繁字号与输入镜头稳定已确认；
  接收/团队测试依旧归入 `chat-peer-relay`，不要要求重做全部输入测试。
- `retail-combat-explanation`：原生配置与 MOD 规则的解释成果保留。
- `sticky-camera-control`：单击/长按发声、双击引爆、引爆期间不进入摄像头已确认。
- `vss-electromagnetic`：本次用户明确反馈“可以了”，不重复采集或重建同一修复。
- F3 变倍对齐已经通过；仅相关代码真正发生变化时做针对性回归，不默认重测。

## 迁移/重构中的处理顺序

继续 [MIGRATION.md](../MIGRATION.md) 的既定顺序：品牌、3.0 UI + LAN 功能整合、
Windows/LAN 与 Linux/public、公开源码/知识库、云端接手、独立性验证后整理旧目录。
上述遗留问题并行跟踪，不使迁移停滞，也不允许在迁移中被静默删除。

云端接手时先核对已上传提交、构建依赖和可执行测试，再改相应 seam。
历史 native replay 的依赖若仍只在旧机器，标为未迁入，不能用“有同名 unit test”冒充覆盖。
每次交接记录：改变的分支/字段、实际运行的输入/输出与退出码、回滚结果、仍需用户的验证。
