# 一键构建（Windows x64）

## GitHub Actions 开发候选构建

后续常规构建使用 GitHub 托管的 Windows/Linux runner，用户电脑无需作为自托管 runner
保持开机。Windows 编译 WPF、x86 DLL 和 LAN 服务端；Linux 编译并测试服务端。
PR 和手动任务的候选检查只保存构建日志、校验和与 artifact，
不自动创建正式 Release、部署生产或启动游戏。实际 job 和命令见
[`tasks/ci-01.md`](tasks/ci-01.md)；配置存在不等于本轮执行成功。

**公开 checkout 的完整包目前被资源隐私门禁阻塞。**
`docs/ASSET-PUBLICATION.json` 中 12 个 SAV/META 输入仍留在本地，不能上传、伪造或
修改预期哈希来换取成功状态。组件编译与纯逻辑测试可以独立执行；缺默认存档的启动器
只能标为编译产物，不能标为完整可交付包。完整资源检查继续独立失败并保留记录。

迁移初始 GitHub source-checks 运行
[`39855999361`](https://github.com/caox233/FifthFreedom/actions/runs/39855999361)
因这些缺失输入失败。云端在交接提交 `fc8ef510b51be83c31f9f8233997f758b0477acc`
独立复现了 37 项测试、16 个资源子检查错误、2 项 Windows 跳过；这不是完整构建通过。

源码开发可先在独立 Python 虚拟环境内执行：

```sh
python -m pip install --only-binary=:all: -r scripts/requirements-profile.txt
python -B scripts/check_migration.py --require-fifth-freedom
python -B scripts/ci_checks.py source
python -B scripts/ci_checks.py package-inputs
```

配方密钥验证固定使用 `cryptography==50.0.0`，缺少依赖不会被当成测试通过。
最后一条仍是明确会失败的完整输入门禁；不能用前面的源码检查替代它。
新增 Windows IPC、真实服务生命周期和隔离加密网络检查的命令及平台要求见
`tasks/ci-01.md`，完整测试集合与托管结果按提交记录在 `CLOUD-PROGRESS.md`。

## 最短流程

1. 下载源码 ZIP 后完整解压，或 Git clone。建议使用短路径，例如 `C:\src\FifthFreedom`。
2. 首次安装下面的编译工具。先检查资源隐私门禁；目前公开源码缺少获准公开前必须保留在本地的存档输入，完整包不能只凭公开 checkout 构建。首次代码依赖恢复需要联网。
3. 双击仓库根目录 **`build.cmd`**。无需管理员，也无需打开 Visual Studio。
4. 等待 `BUILD_SUCCESS`，按输出路径取得完整 ZIP。运行时文件在 `artifacts/program`；最新结果在 `artifacts/BUILD-RESULT.json`。

脚本依次执行：检查工具与资源 → 回归测试 → 主 DLL、玩法 DLL、服务端、启动器 → 完整包校验。不会启动游戏、安装到游戏目录、修改真实账号存档、自动提权或上传 GitHub。

## 首次工具准备

| 工具 | 要求与官方来源 |
| --- | --- |
| Python | 3.11+，安装后能运行 `py -3` 或 `python`；[官方下载](https://www.python.org/downloads/windows/) |
| Git | 安装时允许命令行使用；[Git for Windows](https://gitforwindows.org/) |
| Visual Studio Build Tools | C++ 桌面构建工具，包含 MSVC x86/x64、Windows SDK、CMake；[工作负载说明](https://learn.microsoft.com/en-us/visualstudio/install/workload-component-id-vs-build-tools?view=vs-2022) |
| .NET SDK | **10.0.302 功能带**，根目录 `global.json` 允许同功能带补丁；不是仅 Runtime。不要只装其他功能带；[安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows) |
| Rust | 通过 [rustup](https://rust-lang.org/tools/install/) 安装 Windows x64 MSVC 工具链管理器。仓库指定 `nightly-2025-10-15` 和 `i686-pc-windows-msvc`；rustup 首次按 `rust-toolchain.toml` 获取 |
| protoc | 本项目验证版本为 **35.1**。从[官方编译器说明](https://protobuf.dev/installation/)进入对应发布版本，将 `bin` 加入 PATH，或设置 `PROTOC` 为 `protoc.exe` 的完整路径 |

装完工具后重新打开构建窗口。可以先运行 `build.cmd --check`；缺项会汇总说明，不会开始编译。构建过程中每 15 秒提示仍在工作；依赖下载失败会保留原始输出供排查。

## 随源码提供的资源

`scripts/local-assets.json` 精确列出 20 项 LAN 构建输入及大小、SHA256，包括图标、背景、音乐、FusionFix、采集工具、原始加载器、默认存档模板和 DirectX 头文件。其中 SAV/META 因隐私审查尚未公开；donor 另有受同一门禁约束的存档，总计 12 项列于 `docs/ASSET-PUBLICATION.json`。重新 clone 不会补齐这些保留输入。

拥有本地保留输入的维护者可继续使用原有严格校验入口。公开完整包须等待匹配输入通过隐私审查，或完成另行审查且可复现的默认数据替换。现有第三方声明继续保留；用户的再分发授权声明不等于独立许可核实。采集工具源码和 FusionFix notices 的依赖闭包仍未完成，见 `ASSET-PROVENANCE.md`。

以下仅为资源损坏或手工维护时的可选修复方式，不是普通开发者的前置操作：

- 将匹配的 **`build-assets.zip` 放在源码根目录**，双击 `build.cmd` 时自动校验并导入。ZIP 内使用清单中的相对路径，例如 `src/launcher/src/logo.ico`，不要再套一层顶级目录。
- 使用已有资源目录或 ZIP：`build.cmd --asset-dir "C:\local-build-assets"`，或 `build.cmd --asset-dir "C:\local-build-assets.zip"`。

导入只处理清单内文件；完整校验通过后才写入，已有不匹配文件会报错而非覆盖，不会提取 ZIP 内的其他内容。用于修复的额外资源目录与 ZIP 仍被 Git 忽略；受隐私门禁约束的存档路径也继续忽略，不得由 CI 打包上传。

默认存档快照日期为 2026-10-15。MOD 模板使用该次用户选择的最新本地配置，包含 `.sav`、`.meta` 和前三套配置所需的 `.scbl-loadouts`，不能只替换 `.sav`。构建会重新生成匹配玩法 DLL 的模板清单。模板不会在正常启动时自动覆盖玩家存档。

## 重复构建与输出

- 同名结果已存在时，自动生成带构建时间的新目录和新 ZIP，不覆盖旧包，也不改发布身份。
- 不允许同一源码目录同时构建/测试，避免缓存互相覆盖。退出后系统释放锁，无需手动删锁文件。
- 每条子命令的 stdout、stderr、退出码保存在 `artifacts/logs`。失败返回非零退出码，不输出 `BUILD_SUCCESS`。
- 正式更改版本时修改 `release-identity.json`，四个组件一起重建；不得拼接其他版本 DLL 或关闭校验。
- 默认存档与玩法 DLL 的绑定写在 `artifacts/generated`，不修改源码模板。

## 命令行与可选配置

```powershell
.\build.cmd                     # 完整一键流程
.\build.cmd --check             # 只检查环境和资源
python scripts/build.py test     # 核心测试
python scripts/build.py build    # 单独编译打包
python scripts/build.py verify   # 校验最近一次成功产物
python scripts/build.py verify --package "C:\path\to\package"
```

自动化环境可设置 `SCBL_NO_PAUSE=1` 或 `CI=1`，关闭批处理结尾的按键等待。

一般开发者无需 `build.local.json`。非默认工具路径、共享编译缓存或已缓存依赖的离线编译，可参考 `scripts/build.local.example.json`；这个文件不提交 Git。不要把维护者机器路径复制为公共配置。`offline` 只控制 Cargo 离线，不能保证 .NET 首次恢复也能离线。

## 验证边界

核心回归和包校验不能代替多人实机验收。1.1 包含武器、输入与联机玩法变更，不应沿用1.0旧DLL或宣称只是资源更新。Git 中保留必要测试和明确授权的模板快照；账号数据库、登录信息、个人存档目录的其他文件、历史备份、采样和 AI 行为文件不进入仓库。

## 正式版与开发诊断版

正式版使用标准 `build.cmd` / `python scripts/build.py all` 入口。主 DLL 保留 `patch-free,lan-goal-origin`，玩法 DLL 保留 `goggle-feedback` 与默认的 8 人支持。不要启用 `scis-runtime-diagnostics`、`diagnostic-evidence`、`observer-camera-probe`、`observer-native`、`native-observation` 或 `dev-hot-reload`；本地配置中的 `observer_native` 和 `observer_camera_probe` 均设为 `false`。不要沿用开发包的 target 输出目录或额外 `RUSTFLAGS`。

构建脚本会清除私有诊断编译环境变量，包括热成像材质 F8 观察入口；实际玩法所需的 `SCBL_EMF_CONSTANT_REUSE_BUILD=1` 保留。正常崩溃摘要、诊断导出、中文输入、F5 接受邀请及 F9 自杀不依赖开发诊断特性。F9是正常玩法功能，10秒为冷却时间，不应随诊断热键一并禁用。

版本、发布身份和兼容身份必须同批更新。先运行 `python scripts/release_ci.py check --tag v1.1.0` 检查，再构建；`prepare` 和 `publish` 仅用于以后经确认的 GitHub Actions，不能伪造云端环境来给本地包标注云端来源。

正式构建入口会清理继承的 RUSTFLAGS、CARGO_ENCODED_RUSTFLAGS、CARGO_BUILD_RUSTFLAGS 和 CARGO_TARGET_*_RUSTFLAGS，防止私有诊断配置混入正式包；开发者仍可直接使用 Cargo 显式选择诊断 feature。
