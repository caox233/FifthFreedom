# Fifth Freedom 开发候选测试说明

这是完整 **Windows 本地联机测试包**，包含启动器、两份原生 DLL、Windows 服务端和已准入的公共默认存档，不含游戏本体。不是正式 Release；公网可以选择和配置，实际公网启动仍未准入。

## 下载与校验

- [下载完整候选产物](https://github.com/caox233/FifthFreedom/actions/runs/41222450356/artifacts/13050941008)，需登录 GitHub；保留至 **2026-11-15 01:18（香港时间）**。
- 实际构建源码：`b7846360b6e80a90fca23f580f3778b2a85abe90`；工作流 `41222450356`，完整包作业 `124599695245`。
- 从外层下载 ZIP 取出 `fifth-freedom-1.1.0-migration1-program.zip`，**73,304,533 字节**，SHA256：
  `d08f55da2dfdcdf293f37212b2e3fbfdab803d0785a0de1af4196f61b6518cdd`
- 外层 artifact ZIP SHA256：`69f11285d7bb3ca45742fca24d4dc8b555e09a3f9c7e70f5dd2b3d7e7b935fd2`，不要与程序 ZIP 混淆。

```powershell
Get-FileHash .\fifth-freedom-1.1.0-migration1-program.zip -Algorithm SHA256
```

完整解压到新的可写目录，保留所有随包文件、旧版和个人存档；各端及房主使用同一 ZIP，不混换 DLL。遇到 `RELEASE_MISMATCH`、`PACKAGE_CATALOG_MISMATCH` 或 `PACKAGE_IDENTITY_REQUIRED`，按[完整包核对步骤](COMPLETE-PACKAGE-REPAIR.md)分别排查，不修改清单或关闭认证。

## 本轮修正与后续验收

- 修正摄影机 `Disable` 的 x86 栈参数约定：接收并原样转发四字节参数，`Cleanup` 保持无参。新包实际 DLL 的入口与原函数转发已独立核对。此前 e4 的安装/加载已经确认正确，本轮没有把新闪退归因于旧包；两份 WER 核验为 `EIP=1`，旧日志零字段不能替代异常上下文。
- 登录和注册分别说明 `RELEASE_MISMATCH`（发行版本不同）与 `PACKAGE_CATALOG_MISMATCH`（完整包目录不同），提示房主及加入端使用同一完整 ZIP。没有降低身份、目录或认证校验；LAN `Unavailable/Cancelled` 另行定位。
- 搜索诊断增加最终模式过滤后的返回数量。新增搜索、加入、队伍跟随回归不代表真实多人快速匹配已修好；等级 999 启用和单机进入 `eGoal_Run` 都不能证明同房。

**现在无需重测或重新采集。** 下次集中联测只关注新失败：房主与客户端投出/收到摄影机、重复部署及销毁是否仍同期退出，以及同服同模式快速匹配是否真正进入同一成员表和同一局。原单击、长按、双击、F3、MAC11 HUD、中文/世界相机和已通过的 VSS EMF 范围保留，不要求整套重测；另一次写入异常、三端聊天、其他玩家版本拒绝仍独立记录。

主界面布局保持 e4 的直接 MOD/官方与 FOV 控件。公网仍可选、可配置但实际启动未准入；本地发现、手动连接和创建确认行为保留。默认存档恢复保留确认与原件备份，不需要为了联测恢复存档。

本轮三份现存 trace 未见零尺寸绘制或 Render error，不能用此前其他机器的报错解释本轮电磁镜。没有有效视口下同条件逐帧样本，不报告 p95/p99 改善。

## 已验证与未完成范围

包内 UI **197** 项、存档工具 **27** 项通过；实际恢复并核对公共模板原字节。独立下载验证了 **65** 个文件、63 条清单、64 条 UTF-8 校验行、四组件身份及 50 份第三方说明/源码文件。见[构建证据](evidence/hosted-complete-package-b784636.json)与[下载校验](evidence/complete-package-download-b784636.json)。

[实际 WPF](evidence/hosted-dispatch-ui-b784636.json)覆盖中英、两种窗口、100/125/150/200% 根 DPI；修改与重新应用版各 128 场景、12,944 检查无失败，672 张图与 e4 对应图逐字节一致，主界面没有重排。

[新的 Windows ABI 四阶段回归](evidence/hosted-camera-abi-b784636.json)：基线与回滚各完成 16 个无挂钩前置控制，随后在第一个挂钩调用出现参数错误及受控 execute AV，明确停止，没有把剩余场景算通过。修改与重新应用各完成 **64 次调用、515 项检查，零失败**；每次调用前的独立 Cleanup 均正常。测试使用真实 production detour/replacement 与自有 x86 分派/原生叶，不是零售游戏指令回放；合成异常地址也不是玩家转储的 EIP=1。既有 87 项音频生命周期与内存范围四角色仍按原合同通过。[成品 DLL 核验](evidence/camera-disable-built-abi-b784636.json)另确认真实入口 `ret 4`、原值转发及 Cleanup `ret`。

[源码检查](evidence/hosted-dispatch-source-b784636.json)在 Linux 为 729 通过/2 跳过、Windows 为 612 通过/119 跳过，均零失败；[Linux 服务端与协议证据](evidence/hosted-dispatch-linux-b784636.json)另行记录。未运行玩家游戏；物理显示器、IME 和多人实机验收仍有边界。

完整包、源码检查、合成回放和实机验收分别记录。C71 仍缺 13 份审查输入、执行为零，独立于 Windows 打包；普通公网安全/会话准入、真实多人匹配、三端聊天、历史闪退和 VSS 生命周期仍按原待办保留。六项目标没有宣布全部完成；不自动启动游戏、采集、部署生产或发布正式 Release。
