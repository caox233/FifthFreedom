# Fifth Freedom

《细胞分裂：黑名单》社区项目，正在整合 SCBL LAN 的功能与修复成果、SCBL 3.0 的界面和公网服务端。

**当前为迁移中的开发源码，不是已经整合完成的新正式版。**

## 确定的方向

- 项目和启动器统一名称：Fifth Freedom。
- 采用 SCBL 3.0 界面，保留 LAN 功能与已经验证的游戏修复。
- Windows 服务端用于局域网，Linux 服务端用于公网。
- 余下 BUG 与整合并行处理，不因迁移丢失通过结果。

## 接手入口

- [迁移范围与完成条件](docs/MIGRATION.md)
- [保留结果及待验收边界](docs/ACCEPTANCE.md)
- [云端交接](docs/CLOUD-HANDOFF.md)
- [云端实际进展与验证记录](docs/CLOUD-PROGRESS.md)
- [开发知识库](https://github.com/caox233/FifthFreedom-DevDocs)
- [当前工作状态](PROJECT-STATE.json)

`src/` 是最新 LAN 基线；`integration/scbl3/` 是 UI 和公网实现的移植来源，
尚未合并完成。新改动优先进入统一主线，不让两套 gameplay 修复分叉。

## 开发与验证

```sh
python -m pip install --only-binary=:all: -r scripts/requirements-profile.txt
python -B scripts/check_migration.py --require-fifth-freedom
python -B scripts/ci_checks.py source
```

建议在独立 Python 虚拟环境中执行。`source` 检查明确区分可运行的源码测试与
资源门禁；`python -B scripts/ci_checks.py package-inputs` 保留严格完整输入校验，
当前已准入的公共默认模板使严格输入门禁通过；构建及包冒烟仍须独立验证。

完整 Windows 构建使用 `build.cmd`，需要 [构建说明](docs/BUILD.md) 中的
.NET、Rust、MSVC 和明确列出的资源输入。本机工具链与证据放在被 Git 排除的
`.local/`；不得依赖旧项目目录才能构建。Linux 云端能力和尚缺输入见交接文档。

默认目标是由 GitHub Windows/Linux Actions 构建，不要求用户电脑保持开机。
根工作流已接通源码检查、Windows/Linux 组件构建和独立隔离探测；具体提交的
实际结果与失败见云端进展，工作流存在不等于构建通过。八份默认存档原件已根据
所有者对公共模板的明确确认获准公开，20 项输入严格校验通过；四项 donor 保留输入与
独立 C71 回放缺口继续记录在[资源门禁](docs/ASSET-PROVENANCE.md)。
完整候选下载、精确校验值和实测范围以[候选测试说明](docs/DEVELOPMENT-CANDIDATE-TESTING.md)为准。

仅通过源码检查、单元测试或构建，不代表 DX11、游戏实机或多人网络验收通过。
用户将集中测试新候选包。开发推送不会自动发布 GitHub Release。

## 发布边界

密钥、账号、个人存档、数据库、日志、转储和图形捕获不进入公开仓库。
构建资源按清单保留出处及许可；项目许可证不替代第三方授权。
请自备合法游戏，不提供游戏本体。

[LICENSE](LICENSE) · [第三方说明](THIRD_PARTY_NOTICES.txt)
