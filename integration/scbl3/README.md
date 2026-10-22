# SCBL

SCBL 是面向《细胞分裂：黑名单》社区联机环境的开源客户端、游戏 Hooks、自建服务端和安全更新系统。

当前源码组件版本只在 [COMPONENT_VERSIONS.json](COMPONENT_VERSIONS.json) 维护；README 不复制版本表，避免发布后出现多处事实漂移。

项目仍处于 SCBL 3.0 整理阶段。当前架构、操作、发布和接手方式统一见 [SCBL_HANDOFF.md](SCBL_HANDOFF.md)；Windows 客户端见 [client/README.md](client/README.md)；推荐的原生 Windows 服务端见 [server/windows/README.md](server/windows/README.md)。原 Linux 服务端手册保留在 [server/INSTALLATION.md](server/INSTALLATION.md)，只作为现有 Linux 实例的维护入口。

## 主要能力

- Windows Launcher：登录、公告、更新、网络状态、游戏准备和启动。
- EasyTier + Route Guard：虚拟网络、QUIC/WSS、IPv4/IPv6 和 Windows 路由保护。
- Rust Hooks：游戏内 Overlay、好友/邀请、Party、兼容和玩法能力。
- Dedicated + Control Plane：账号相关游戏业务、房间、会话、在线状态及持久化事务。
- Server Manager：组件 ZIP 完整性校验、服务端部署、客户端通道、备份和回滚。

## 仓库结构

```text
client/       Launcher、Updater、Hooks、EasyTier 与 Route Guard
server/       Dedicated、Control Plane、Update Server、Windows Host/Manager 与组件 ZIP packaging
shared/       跨端协议
scripts/      固定构建、冻结、发布和清理入口
docs/design/ 技术设计资料；日常维护不要求全文读取
```

## 本地验证

所有固定工具从以下入口开始：

```powershell
scripts\coordination\scbl-task.cmd help
```

该入口用于日常开发任务、定向验证和工具路由；个人完整 Windows 客户端构建固定使用 `client\build_all_windows.ps1`。不要为单次修改创建新的构建或发布脚本。

## GitHub Actions

- `CI`：push、pull request 和手动触发的无秘密测试与编译检查。
- `Online component build`：手动选择 Launcher、Updater、Hooks、Dedicated 或原生 Windows Server，生成七天保留的开发候选 artifact。
- 在线构建不会自动发布 test、stable、生产服务器或 GitHub Release。
- Launcher 在线构建在 `FusionFix.zip` 完成再分发许可审核并建立受保护输入前会明确失败，不会静默生成缺少运行资源的候选。

正式发布需要独立的远端事务、哈希核对、公开回读和回滚门禁；这些权限不会暴露给普通 pull request。

## 安全与开源边界

不得提交：

- 私钥、SSH 身份、token、密码、数据库和生产运行配置；
- 日志、诊断包、账号数据或原始网络载荷；
- `bin/obj/target`、完整包、patch、服务端 ZIP 和本机缓存；
- 未完成许可证与来源审计的第三方二进制或研究材料。

安全问题请使用 GitHub 私密安全报告。更多要求见 [SECURITY.md](SECURITY.md)。

## 许可证

项目当前使用 GPL-3.0。第三方组件按其各自许可证分发，详见 `THIRD_PARTY_LICENSES/` 与 `THIRD_PARTY_NOTICES.md`。公开发布前仍需完成第三方二进制和视觉资产的逐项来源审计。
