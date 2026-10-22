# 历史原生回归参考（不是当前测试入口）

此目录保留多人反馈、VSS 入镜和 VSS EMF 修复的**人工编写回归源码清单**，
帮助云端开发者理解测试方法与边界。文件以 `.py.txt`、`.cpp.txt`、`.rs.txt`、
`.cs.txt` 保存，刻意不加入任何构建、CI 或自动测试发现流程。

## 内容

| 子目录 | 所覆盖的方法 | 原历史结果（本次没有重跑） |
| --- | --- | --- |
| `multiplayer/` | ChatString 房主中转的原生 ABI、导出字节串分类、故障观察及导出解析 | 聊天 391 项原生检查；导出 24 项回归；不证明三客户端实机消息交付或历史闪退根因。 |
| `entry/` | 本地控制器/viewport 就绪、scope owner 和入镜转换的源码/原生合同 | 90 项原生入镜检查通过，另有独立 guard 审阅；不证明所有间歇丢镜已修复。 |
| `emf/` | 实际 Rust material 函数和 owned-scope 链绑定原生 factory/draw/cleanup 的回放 | 137 项合同；baseline/rollback 外层退出 101，修改/重新应用退出 0。draw body 使用计数器，不是 GPU。 |

历史汇总、用户通过范围和未完成项见
[验收记录](../../ACCEPTANCE.md)、[开发历史](../../DEVELOPMENT-HISTORY.md) 和
[剩余问题](../../tasks/remaining-bugs.md)。不能用本目录源码的存在代替测试执行。

## 刻意未公开的输入

- 原游戏 EXE、DLL、抽取的机器指令数组及生成的 `native-code.h`、`retail.h`、
  `full-code.h` 等原生字节 fixture；不把这类内容当作自己编写的测试代码发布。
- 真实日志、命令输出收据、账号/个人存档、崩溃转储、GPU 捕获或资源。
- 原机器的绝对路径、用户名和工具安装位置，已替换成 `<LOCAL_PATH_NN>`。
  数字只在单个文件内标识位置，不是可执行的配置。原件与精确替换对照仅保留私有。
- 历史二进制 harness、历史部署入口、`transaction.py`、`integrate.py`、`finalize.py`。

测试中的 `TEST_SECRET`、`DO_NOT_EXPORT`、固定 PID 等是源码中人工构造的回归值，
不是实际凭据或玩家记录。它们用于证明敏感字段不会导出。

## 云端便携化待办

1. 先按当前产品源码与 [构建文档](../../BUILD.md) 确认模块/项目名。
   历史清单仍含旧项目名和旧相对布局，不能直接当作 Fifth Freedom 当前脚本执行。
2. 把源码根、输出目录、编译器及合法持有的输入映像改成显式参数；禁止读取真实
   玩家数据。输出必须是新的外部目录，不改源码、原游戏或累计证据。
3. 对原生输入保留 SHA256/版本门槛、签名和重定位检查。需要的机器码应由
   获准的本地映像确定性生成，不能用任意字节填补或关闭校验来让测试通过。
4. `run.py` 的原生解析辅助模块、C++ 引用的机器码 header、部分历史 pins 等
   **不在这个公开目录中**。先梳理依赖、替换路径并建立新的可复现测试，
   再把已验证的 portable 版本放到正式 `tests/`。不要运行旧 transaction/finalize 脚本。
5. 这些原生合同依赖 Windows x86 ABI、MSVC/对应 Rust 工具链，部分还依赖
   本地 DirectX 辅助库。Linux 云端能做源码分析不等于能执行 Windows native replay。
6. 同输入执行 BASELINE、MODIFIED、ROLLBACK、重新应用，核对源/恢复哈希与
   literal stdout/stderr/退出码。最后仍保留 GPU、混音及多人实机验收边界。

`INDEX.json` 只记录本目录公开清单的路径与 SHA256，不包含旧机器路径。
本次额外保全了 117 项旧历史资料的本地校验副本；这不代表所有旧项目资料已迁入、
全部依赖已闭合，或云端已经具备历史 native replay 的全部输入。
