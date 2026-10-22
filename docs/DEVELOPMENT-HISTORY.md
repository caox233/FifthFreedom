# 开发历史与实现接手索引

## 交接范围

Fifth Freedom 从已验证的 LAN 候选继续演进，不以重写或回退换取项目改名。
本索引覆盖关键决策、实现入口、历史验证结果和未完成边界，不是全部聊天/日志的逐字导出。
完整私有历史、原始捕获和历史原生回放依赖仍有部分位于旧工作区；在完成清单迁入和验证前，
不能删除旧目录，也不能宣称云端已收到全部历史证据。

公开知识库应引用这里的解释和源码，不复制含账号、地址、机器路径、聊天正文或会话状态的原始资料。
[验收记录](ACCEPTANCE.md) 保留 14 个稳定 ID；[待办](tasks/remaining-bugs.md) 保留未完成项。

## 里程碑和教训

| 阶段 | 实际进展 | 接手时不能丢失的边界 |
| --- | --- | --- |
| LAN 武器导入 | 战役 SC-IS/VSS **复制**到多人阵营武器目录，补全资源、配件、弹药、描述、显示与入局数据。 | 不移动战役原件；显示正常不等于全部物理/联网参数正确。VSS 的普通/ADS 资源与加载生命周期必须分别验证。 |
| SC-IS 视角与射击 | 处理第一人称资源、鼠标精度、固定镜框变倍、同帧镜片/相机归属、呼吸与射击恢复。 | 摄像头、世界、镜筒、准星纹理不是同一层；不能用整幅世界晃动代替镜筒呼吸，不能用吞掉真实开火输入“修复”动画。 |
| 中文输入 | 聊天与装备重命名分别接入 IME；修复简体字体显示与输入期间镜头跳转。 | 输入候选确认和保存确认不同；本机发送通过不等于第三客户端接收通过。 |
| UNIFIED25 | 用户反馈本轮快速开关镜和最高倍率准星清晰度未见明显问题。 | 仅这两项本轮反馈；部分 CPU 记录相符不是最终 GPU 全覆盖。无新可复现问题不重复调参。 |
| 1.1 摄影机/F9/ATS | 摄影机点击、长按发声、双击引爆，以及引爆期阻止回到原生摄像头；F9 冷却自杀；ATS 消音声波 20 米。 | 摄影机声音资源寿命、原生空间声源与输入手势分开处理。摄影机操作已有用户通过；ATS 距离和 F9 保留离线/实机边界。 |
| 1.1 多人反馈 | 修复导出字节串分类、VSS view 过滤与入镜就绪、加入者消息房主中转；增强崩溃导出。 | 三端聊天、间歇丢镜和历史崩溃仍未闭环，不因汇总版本发布而自动关闭。 |
| VSS EMF | 单帧图形回放定位 ADS 玻璃在 EMF 不透明绘制；在原生材质调用前做精确剔除，用户反馈已正常。 | 不是“补一个玻璃贴图”或删除整只镜模。必须保留准星、壳体、深度及其它武器/视觉模式。 |
| Fifth Freedom | 统一命名，保留 LAN 基线并引入 3.0 UI/公网服务供体，开始可独立构建和云端交接整理。 | UI 集成、Windows/LAN 与 Linux/public 适配不是复制目录即完成。以 [迁移计划](MIGRATION.md) 为准。 |

## 源码职责地图

下表路径均相对仓库根。源文件名保留旧协议/模块身份是兼容措施，不表示品牌尚未决定。

| 功能 | 源码入口 | 实现约束/典型回归 |
| --- | --- | --- |
| SC-IS/VSS 目录与资源 | `src/native/client/hooks/gameplay/src/mac11_catalog.rs`、`scis_catalog.rs`、`vss_catalog.rs`、`scis_preload.rs` | 多人复制路径、原生对象所有权及缓存复用，不能改共享战役定义来凑多人效果。 |
| VSS 普通附镜/ADS 引用 | `src/native/client/hooks/gameplay/src/vss_attachment.rs` | `reload_with`/`complete` 重读同一引擎拥有的配件，补齐 view 0/1 引用；原生过滤标志是排除规则。不能二次排队创建默认附件，避免下一次复活污染。 |
| 镜模生命周期 | `src/native/client/hooks/gameplay/src/scis_scope_actor.rs`、`scis_scope_policy.rs` | 角色代次、世界、本地控制器/viewport 和 scope lease；旧对象地址相同不代表身份相同。未就绪时退回原生路径。 |
| EMF 精确玻璃剔除 | `src/native/client/hooks/gameplay/src/vision_transparency.rs`、`vss_emf_glass_policy.rs` | `material` 在 native call **之前**拒绝当前本地 VSS、EMF mode 9/stage 1 的确切 Alpha=0 ADS Lens_02；结合类型、资源、surface membership 与 lease。 |
| 相机、变倍与镜片 | `src/native/client/hooks/gameplay/src/scis_frame_join.rs`、`scis_frame_commit.rs`、`scis_frame_install.rs`、`scis_reticle_content.rs`、`scis_zoom.rs` | 同帧、同 owner、实际材质/绘制分支一致；固定镜框与画面变倍分离；保留 F3 对齐。插件有无不能变成行为前提。 |
| 呼吸与入镜 | `src/native/client/hooks/gameplay/src/scis_scope_sway.rs`、`scis_scope_sway_policy.rs` | 从同帧原生 FP idle 骨骼姿态驱动所拥有的镜筒，等待 ADS placement 完成；不能用过渡大偏移作为永久呼吸锚点。 |
| 射击间隔/假反馈 | `src/native/client/hooks/gameplay/src/scis_fire_interval.rs`、`scis_event_policy.rs`、`scis_fire_policy.rs`、`scis_fire_recovery.rs`、`scis_shot_feedback.rs` | 保留 1.45 秒间隔；真实出弹事件和循环分类的第一人称动画恢复分别处理。单发使用原生单发反馈，不改全局共享动画。 |
| SC-IS 枪声 | `src/native/client/hooks/gameplay/src/scis_sound.rs`、`scis_sound_owner.rs`、`scis_sound_range.rs` | 核对真实声音事件、所有权、view filter、发声分支与作用范围；本机混音可闻与远端听距是两份验收。 |
| 数值与显示 | `src/native/client/hooks/gameplay/src/scis_damage.rs`、`scis_damage_policy.rs`、`scis_ammo.rs`、`scis_stats.rs`、`vss_balance.rs`、`vss_balance_ui.rs` | 原生伤害链、护甲、穿甲弹、部位、距离取整与 UI 不能互相替代。VSS 无穿甲弹基础使用消音 PFS-12 基准，衰减距离 1.5 倍；原生附镜加值要避免重复。 |
| 击杀/HUD 图标 | `src/native/client/hooks/gameplay/src/scis_kill_icon.rs`、`mac11_hud.rs` | 原生图标字符串消费者和白名单；只改呈现，不改变战斗武器类别。 |
| 中文输入 | `src/native/client/hooks/src/chat_input.rs`、`chat_input_native.rs`、`rename_input.rs` | 候选提交、宽字符、编辑/取消/失焦生命周期；防止输入消息被重复当成相机输入；不记录文字和普通按键。 |
| 聊天中转 | `src/native/client/hooks/gameplay/src/chat_relay.rs`、`goggle_feedback_native.rs` | 房主转发普通 ChatString receipt，排除发送者和房主；借用消息仅限原生 send 期间，保留原生序列化/队列和团队 UI 过滤，不读取正文。 |
| RFD/无人机 | `src/native/client/hooks/gameplay/src/goggle_rfd_source.rs`、`rfd_exposure.rs`、`drone_control_audio.rs`、`drone_corpse.rs`、`drone_tags.rs` | 来源/归属/取消和生命周期分别核对；不能仅用血量猜测“已死”，也不能删除别的标记来源。 |
| 摄影机/ATS | `src/native/client/hooks/gameplay/src/sticky_camera_gesture.rs`、`sticky_camera_input.rs`、`sticky_camera_runtime.rs`、`sticky_camera_sound.rs`、`ats_suppressor.rs`、`ats_native_ui.rs` | 单击/长按/双击状态机与原生部署键一致；引爆期禁止回入摄像头；声音位置为设备位置，不伪装成玩家位置。 |
| 导出/崩溃 | `src/launcher/src/Services/DiagnosticTextSanitizer.cs`、`NativeExceptionExport.cs`、`src/native/client/hooks/src/exception_record.rs` | 完整合法 hex byte array 特判不放宽畸形数据；导出固定故障上下文，不上传 raw log、不承诺“有诊断就必能定位根因”。 |

## VSS EMF 修复为什么必须在调用前

实际捕获中 ADS Lens_02 的玻璃与准星是不同 draw，镜壳又有独立深度绘制。
玻璃的 Alpha=0 在普通模式有效，但 EMF 材质分支使它遮黑镜孔。
只禁用玻璃 draw 的 GPU 干预恢复镜孔，准星/镜壳/高亮仍在，深度不变。

原生 material 调用会安装 sampler/uniform 状态；若调用后才返回 0，调用者会同时
跳过 draw 和清理，污染后续绘制。因此产品在 native call 之前做精确准入检查。
检查失败、对象失效、锁忙或签名不匹配时走原生路径；这不是跨线程寿命栅栏。
不要把纹理尺寸当成生产身份判定，也不要删除整个 scope node 或修改共享贴图。

## 回归入口与证据分层

仓库内已有入口（从仓库根执行；具体依赖见 [BUILD.md](BUILD.md)）：

```text
python scripts/build.py test
build.cmd --check
build.cmd
python scripts/build.py verify
```

其中 Windows/MSVC x86 玩法与原生 replay 不能因为云端 Linux 缺运行时而省略后声称通过。
云端可完成的源码测试、静态检查与 Linux 服务测试应各自产生收据。
Win32 图形、混音、真实游戏及三客户端投递仍须相应 Windows 环境/用户验证。
参见 [云端交接](CLOUD-HANDOFF.md)。

历史回放不等于普通 cargo 单元测试：一部分执行固定游戏版本的原生指令并绑定
真实源码函数；它们可能引用本机工具链、游戏映像和生成的指令字节 fixture。
尚未完成便携化的回放脚本/fixture 只在私有交接索引登记，**不保证公开 clone 可重跑**。
先把路径参数化、恢复输入 SHA 校验、列出依赖并重新执行，再宣布它已迁入。
不要照搬历史 `finalize.py`/`transaction.py` 改写累计记录，或执行历史部署/采集入口。

## 对新开发者的验证约定

1. 为缺陷建立可失败的同输入基线；修改、回滚、重新应用分别记录结果。
2. 每个结果写明测到了源码/ABI/GPU/声音/网络哪一层及没有测到哪一层。
3. 累计四角色为 changed archive、可重建 patch、literal verification ledger、可执行 rollback。
   历史大工件保留私有；不能为了公开文档小巧而删除唯一证据。
4. 新实现只替换已建立合同的 seam；UI 供体不得覆写已通过的 LAN native gameplay。
5. 真实失败更新待办，真实用户通过更新验收范围；日志没有报错不是新的通过。
