# 摄影机投出闪退与音频回调生命周期

Status: **needs-triage**（多端闪退的完整动态因果）；11 月 1 日新增证据确认
Disable 栈参数 ABI 缺陷，已最小修正并完成真实托管 Windows 四角色验证。
10 月 31 日音频生命周期回归的通过结果保留，不代表这项新 ABI 问题此前已覆盖。

## 联测反馈保留

### 11 月 1 日：已确认 Disable 栈参数缺失

用户确认新候选源码 `0e691519` / `e4f8990` 的组件真实加载。两份离线 WER
异常均为 x86 execute AV，`EIP=1`、异常地址 1、参数 `[8,1]`。旧日志的
`[0,0,0]` 不能推导 `EIP=0`，不得把这项新证据归成旧问题重报。栈内发现的游戏
RVA `1c9519/ada246/af1a32/8e1d92` 只属于扫描结果，不能当成完整调用栈。

用户对 SHA256
`c6b9f330fac1412f19f32a6fd86edb4c66291a6902611e9433b9b0ea659eb4bc`
的原版 DX11 文件只读核验：摄影机虚拟表 `02AB095C + 4D0 = B52610`；
`EDA240` 读取要传入的 DWORD，`EDA243 push eax`，`EDA244 call edx`。
`B52610` 三处早退与正常路径汇合至 `B5271E pop esi / B5271F ret 4`，没有尾跳。
`Cleanup B52570` 独立以 `B52608 ret` 返回。上层 `EF1A2B push 1` 是分派选择，
**不是可以硬编码为 Disable 参数的常量**；本轮逐位透传实际 DWORD，不猜参数语义。

公开候选 DLL 的独立反汇编把 `+D5A54` 及旧包 `+113A64` 对应到 Disable 的
`thiscall → boxed Fn` 包装器；原入口仅接受 ECX，回调也未向原生调用压入参数，
包装器以 `ret` 返回，与已核的 `ret 4` 合同矛盾。详见
[精确二进制映射](../evidence/native-disable-binary-map-20261101.json)。
原生额外弹出 4 字节会破坏其 Rust 调用者的 `add/pop` 退栈；该机制与跳至数据
地址相容，但静态扫描仍不能独自证明两份 WER 的完整动态调用链。

修复只把 Disable 改为 `unsafe extern "thiscall" fn(usize, usize)`，replacement
接收并向原 trampoline 原值转发第二参数。Cleanup、`+4D8`、手势、UI、原生地址
和既有签名 pin 都不改。用户已核 `EDA246/EDA249` 在调用后重新装载 ECX/EAX，
此调用点不消费 EAX；保留原来的 void 返回声明，不推断全部其他调用者的返回语义。

新的 `camera-abi-fixture` 是默认关闭的测试功能：自有 x86 机器码调用器、伪造对象
的 `+4D0` 分派、真实 production StaticDetour/replacement/trampoline、synthetic
`ret 4` 原生叶及独立 `ret 0` Cleanup。四阶段、两个接收者、四个 opaque 参数、
两次连续调用共 64 次 Disable，每次前执行 Cleanup；验证原值、ECX、栈、非易失
寄存器及原生精确调用次数。它没有加载或执行零售函数体。

旧 ABI 可能在回到自有分派器前就因内部退栈错误而异常。测试专用 VEH 只在已验证
前 16 次原生控制、当前同线程明确 armed、真实叶恰调用一次且接收者正确/参数错误、
execute AV 的异常地址/EIP/目标一致时写出收据并退出隔离子进程；不恢复上下文，
不把剩余未执行场景算通过。父进程必须产生失败断言，runner 区分受控异常的部分
执行与完整 64 场景。修改版必须全部正常返回，任何其他异常均拒绝。C71 门禁不变。

本地线程另报告两哨兵的真实 Rust i686/vtable 检查 B/M/R/RP 为失败/通过/失败/通过，
旧参数丢失且 ESP 差 -4，新参数保留且 ESP 差 0；这是独立本地报告，不能冒充云端
托管结果。云端的源码编译、fixture 检查与后续真实结果分别记录于
[本轮源码与测试边界收据](../evidence/camera-disable-abi-source-20261101.json)。

### b784636 的实际交付结果

官方 run `41222450356` / Windows 作业 `124599657507` 成功，精确源码与 PR checkout
`aa35da89094b15d75b60944a71dff5cf46c4ffb2` 的 tree 相同。下载 artifact `13050946720`
后重算官方 digest/CRC、所有输出摘要与生产/runner/fixture Git 对象，再由严格解析器
独立重读四角色：全部实际编译退出 0；B/R 各正常完成 16 个无挂钩控制，在第一个挂钩
调用中记录单次原生执行、正确 receiver、错误参数及 execute AV，子进程观测后退出 86，
父测试以 101 真实失败。异常没有恢复，余下场景未运行。M/RP 各 64 次调用、64 次
Cleanup、515 检查全部通过，零跳过。合成异常 EIP 与玩家 EIP=1 不同，不宣称重放了
真实 WER；见[完整执行收据](../evidence/hosted-camera-abi-b784636.json)。

完整程序包作业 `124599695245` 也成功。内层 ZIP SHA256
`d08f55da2dfdcdf293f37212b2e3fbfdab803d0785a0de1af4196f61b6518cdd`，
实际 gameplay DLL 为 `a4653a06f988d147c57e0b5f29cc65dc4293799f44b26d408efe35945eab102f`。
独立读取其真实机器码确认 Disable 入口 `C2 04 00`、opaque 参数贯穿 closure/body
并压入原 trampoline；Cleanup 仍 `C3`。
[静态成品证据](../evidence/camera-disable-built-abi-b784636.json)与运行回放分开。
包内 UI197/save27、65 文件、四身份和 UTF-8 校验通过，既有生命周期/内存回放保留。
这关闭已确认 ABI 缺参的源码/隔离验证缺口，不关闭全部游戏闪退。现在不要求用户
重测或转述技术数据，后续集中联测边界见[候选说明](../DEVELOPMENT-CANDIDATE-TESTING.md)。

### 10 月 31 日的历史摘要

2026-10-31（Asia/Hong_Kong）收到本地审查的脱敏摘要：同一约 29 秒窗口内，
房主与两客户端出现 `0xc0000005`、`Access=8`，早期摘要记为 `Instruction=0`；
该值不能替代后来核实的 WER 上下文（见上文），没有可信的返回
调用点，第四端同期游戏退出。另一次房主异常为 `Access=1`、`RVA=0x20c25b4`，
单独保留，不能据此把两类异常都归因于摄影机。云端没有接收或公开玩家日志、
转储、标识和截图。

补充包绑定来自本地审查：九份运行记录的 LoadedModules 主/玩法组件属于
`da180` 完整包；`da180` 到 `dcb` 的 `src/native` 源码没有变化。
`SCBL_STICKY_CAMERA` 九条记录只证明初始化，不能充当投出时的调用栈或成功回放。
加载摘要不是崩溃根因。新截图的 `RELEASE_MISMATCH` 另由
[包一致性任务](package-compatibility-feedback.md) 处理。

## 已确认的源码边界

当前摄影机手势适配器仅接管已部署并通过身份/世界/所有者检查的摄影机，
初始投出状态继续交给原生路径。检查范围没有可公开、精确绑定的投出/复制
原生指令体，不能猜偏移或把 synthetic 叶函数当成这些指令体。

[首投/复制独立源码审阅](../evidence/sticky-camera-first-throw-source-review-20261031.json)
记录了 `5227af4` 的 31 个文件哈希及复核未变结果；该审阅未执行游戏或运行时测试。
初始输入交回原生不能排除插件的 Tick/Cleanup、共享资源及瞄具销毁路径；候选默认
启用人数扩展，另行暂停的是网络频率 boost。未定位边界和两类异常继续分别保留。

源码中另有可独立验证的同步重入缺口：替换声音调用原生 End/Stop/Start 后，
外层路径继续使用调用前的身份，或者按摄影机身份删除已经由嵌套调用接管的声音
记录。它是一项源码缺口，尚未建立与上述执行异常的因果关系。
本轮最小修改保持原生地址、签名 pin、手势时间、角色判定、复制协议与存档不变：

- End 返回后，在新 Start 前重新核对原来的摄影机、所有者、世界、资源及启用状态。
- 每次声音记录使用不复用的内部序号；外层失败只删除自己登记的记录。
  内层已接受的记录接管后，外层既不混入原 Whistle，也不再发布一次 ATS/发送。
- End 失败后，只有原身份仍成立且没有新的声音记录时才调用原 Stop。
- 原生可用性查询和取消声音都可能重入；在这两个边界后重新核对原 Owner/READY，
  才调用原引爆入口。正常可用性查询和引爆仍走原地址。
- Whistle 的音频返回边界再次校验 Owner，避免对已经销毁/替换的对象追加原声音、
  ATS 或网络发送。内部对象字段 `+0x20` 仍只是已有身份/index，
  **不是已经证明的分配代数**；同地址同 index 重用没有获得全面检测保证。

这不是全部生命周期的修复。Tick 的附加声音调用后再调用原 Tick、Cleanup 的声音
结束后再调用原 Cleanup 等旧边界仍需真实原生返回/销毁合同；本轮不猜返回语义。
并发跨线程释放、ABA、实际资源 GC、真实声音、远端 ATS/复制与首次投出仍未验证。

## 验证方式与结果边界

`sticky_camera_lifecycle_tests.rs` 调用实际 production sound、Whistle 和引爆
execute 路径，只有原声音播放/停止、原 Whistle、发送和可用性/引爆叶函数换成
同进程计数回调。原固定页版本在独立测试子进程中申请地址，首次 Windows 执行在申请阶段失败；
下面记录真实失败及动态内存修正。测试不启动、附加或写入游戏。
场景包括本机/复制角色的边界、未部署/资源未就绪、两个独立设备、清理与重新部署、
End/Start 回调销毁或替换、资源/世界变更、嵌套发布，以及引爆的正常和失效身份分支。
这些不是实际投出、复制、引爆的原生指令回放。

固定预期为 87 条 callback 检查；既有手势/世界合同加该测试共 10 个 Rust 测试。
[四角色 runner](../../scripts/ci_native_production_replay.py) 在独立临时源码副本中
只切换 `d207fc6` 与修改后的三份生产文件（另含 progression lane），固定其余依赖和
测试；旧文件追加 `cfg(test)` 模块声明，随后四角色共同应用明确记录的测试地址转接。
原生产字节、追加模块后的字节与用于编译的测试字节分别记录 SHA256，并要求逆变换
逐字节还原；插桩源码不能冒充零改动的 retail 指令。编译失败不能计为基线失败。B/R 必须执行
原代码并出现指定的 35 条 callback 断言失败；M/RP 必须实际通过全部 87 条。
二进制由本次 Cargo JSON 选择并校验 i386 PE，源文件、可执行文件、输出均记录 SHA256。
它独立于 C71 的 13 份尚缺审核输入，不代替、放松或填充那些输入。

早期云端结果、编译范围及当时的 Windows 待执行状态见
[源码收据](../evidence/sticky-camera-lifecycle-source-20261031.json)。
原手势 5 项在 Linux 实际通过；这不等于原生 Windows/游戏运行通过。
最初 Python runner 的拒绝控制 10 项通过；动态转接修正后的拒绝/可逆转换控制
现为 13 项实际通过。这只证明结果解析和准入，不证明 Windows 执行。

下一统一候选的集中联测只针对新失败：房主及两个客户端分别投出/收到摄影机、
组件尚未就绪时取消/销毁、重复部署及换局，记录是否同窗退出；继续保留用户已通过
的单击/长按、双击引爆与引爆等待期间不进入相机行为。不要求重复全部已通过项目，
不自动启动玩家游戏或采集，也不对外发布原始证据。

## 首次托管回归入口失败（72e92af）

Windows 作业 `124509994860` 在 Python 源码检查阶段失败：TAR 成员 `/absolute`
在 Windows 的 `Path.is_absolute()` 语义下未被提前拒绝。原生四角色尚未执行，
不能将此登记为摄影机回放失败或通过。工具已改用平台无关路径校验，并补合法解压、
POSIX/Windows 根路径、drive-relative、UNC、反斜杠及 ADS 反例。
[实际失败与最小修正](../evidence/hosted-feedback-runner-path-72e92af.json)保留日志摘要、
明确的 0 原生执行和新 SHA 的 Windows 待执行状态；生产源码、fixture、87/35 条件不变。


## 首次真实 Windows 执行及动态 fixture 修正

`d40bfc3` 的官方组件任务 `41193432848 / 124514693989` 已实际编译 baseline
测试 PE 并执行：保留的 9 个手势/世界测试通过，新子进程却在第一项
`VirtualAlloc(0x3390000)` 返回 NULL 时失败，**87 条新 callback 检查均未开始**，
progression、M、R、RP 也未执行。没有记录 GetLastError/现有映射，不能唯一断言
是 ASLR 或哪一块内存占用。严格 runner 因缺失指定断言而拒绝这一失败，
没有把「9 通过、1 失败」误记为预期负控。源码门禁此前已通过，
本轮的 DX11 地址文件 8 项、release admission 5 项、认证/重传 4 项、
port 0 元数据 1 项等实际成功结果一并保留在
[官方失败及成功收据](../evidence/hosted-native-fixture-failure-d40bfc3.json)。

修正仅涉及 fixture/runner：用 Rust 自有动态内存保存测试全局槽和虚拟表，
不再尝试占用 retail 绝对地址。三组精确的逻辑地址/偏移读被转接到这些槽，
其余读仍原样进入 `equipment_native::word` 及真正的 Windows VirtualQuery。
四个既定 native 叶调用的目标转为 synthetic `thiscall` 叶函数；原参数、签名、
类常量、槽值比较、分支和执行顺序不变。自有内存若与逻辑 token 碰巧重叠，
须明确拒绝该 fixture，不能把真实对象误译为测试全局。

同一可逆转接函数用于 B/M/R/RP；只修改私有测试源码副本，release 生产文件不改。
未经过转接的直接 callback 测试会在前置条件处明确失败，不跳过也不访问原地址。
这项修正提交前，87/35 仍只是固定预期，Linux typecheck 和 Python 13 项通过
不能证明 Windows 执行。后续正式 CI 的实际四角色结果单独记录如下。

开发者请按 [构建文档的专用 Windows 命令](../BUILD.md#摄影机与等级表的专用-windows-四角色回归)
运行 `python -B scripts/ci_native_production_replay.py --output <新的输出目录>`。
它要求已提交源码和已获取的固定公开基线；普通 `cargo test` 没有测试地址转接，
会明确报 `STICKY_SHARED_TEST_ADDRESS_TRANSLATION_REQUIRED`，不会跳过后输出通过。

## 实际 Windows 四角色闭环（e4f8990）

源码 `e4f899072c53e2ea1079b8dd1e54616026357b21` 的官方候选运行
[`41195418269`](https://github.com/caox233/FifthFreedom/actions/runs/41195418269)
中，Windows 组件任务 `124520488334` 成功。实际 checkout 的 PR merge 为
`abdc1c2bb5e913e8e2bfbe6ceb6f4787cee18d9d`，与源码 HEAD 的 tree 完全一致。
四角色均真实编译成功并执行，不再停在固定地址申请前置条件：

| 角色 | 摄影机 Rust 测试 | 新 callback 检查 | progression Rust 测试 |
| --- | --- | --- | --- |
| baseline | 9 通过、1 个预期失败 | 87 项，精确 35 个指定断言失败 | 0 通过、2 个指定失败 |
| modified | 10 通过、0 失败 | 87 项，0 失败 | 2 通过、0 失败 |
| rollback | 9 通过、1 个预期失败 | 87 项，精确 35 个指定断言失败 | 0 通过、2 个指定失败 |
| reapplied | 10 通过、0 失败 | 87 项，0 失败 | 2 通过、0 失败 |

所有列出的测试 `ignored=0`。新 callback 子进程包含在外层 10 个 Rust 测试内，
不另加一次通过数。B/R 的负控退出码为 101，编译全部退出 0；M/RP 退出 0。
progression 的 M/RP 实际完成八种无效内存/容量反例；B/R 在容量 998 的
`variant=5` 与同分配跨 region 正例分别触发指定失败。

云端已下载组件证据 artifact `13041390637`，校验 ZIP CRC、官方 SHA256、
全部四角色的编译/执行输出哈希，并用该提交的严格 parser 再次核对原始输出。
三份生产源码的原字节、追加测试模块后的字节、共同地址转接后的编译字节分别
核对 SHA256；逆变换逐字节还原到公开 Git 对象。可执行文件 i386/哈希由官方
Windows runner 记录，证据 ZIP 不含该测试 PE，云端未另行下载或执行它。
详见[正式执行收据](../evidence/hosted-native-production-e4f8990.json)。

这闭合了有界同步重入与等级表内存准入的源码回归；它没有建立原始首投闪退、
另一次写异常或高等级快速匹配失败的因果关系。真实投出/复制/声音/引爆指令、
多端联测和 C71 的 13 个保留输入仍保持原边界；前两轮真实失败记录继续保留。
