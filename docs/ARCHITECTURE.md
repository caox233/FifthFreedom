# 架构

启动器（C#/WPF）管理用户界面、连接、启动准备、进程及诊断；原生主 DLL（Rust/x86）在游戏内接入接口并加载玩法 DLL。玩法 DLL 仅在 MOD 模式启用。服务端（Rust/x64）承担认证、房间、会话及存储，仍配合原游戏的对局与房主同步逻辑，不是重新实现的战斗模拟引擎。

共享协议位于 `src/native/shared/api`；主 DLL 与玩法 DLL 的 ABI 位于 `src/native/client/hooks/plugin-api`。统一发布身份位于根目录 `release-identity.json`，由 C# 和 Rust 构建生成相同身份声明。程序包通过四个组件的角色、身份、长度和哈希核对匹配性；它不是签名或反作弊证明。

本轮保留内部模块结构，不把整理目录混同于重写逻辑。后续优先缩小启动流程/窗口状态的调用接口，明确 Hook 生命周期的单一负责人，并让测试直接调用生产模块，而不是重复拼接源代码文本。

公网只读控制面位于 `src/public/fifth_freedom_control`：HTTPS 入口使用连接的实际来源，
验证原生服务端签名的 release/run 身份后，每次读取当前数据库准入状态。
其独立 Python 候选包和 systemd 模板不等于完整公网游戏服务端；原生 Public 启动仍受门禁约束。
Linux 公网包需要同时绑定 Linux 服务端产物与被准入的 Windows 客户端 catalog，不能通过
改变现有四组件哈希或跳过校验来解决两平台产物不同的问题。具体边界见服务端任务记录。

程序目录保持 `runtime`、`package`、`licenses` 按用途组织；运行数据在程序目录的 `data/<机器名>/<用户名>`，游戏存档默认在 `%APPDATA%/5th-Echelon/Saves`。运行数据不是源码或发布包输入。
