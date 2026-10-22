# SCBL 3.0 Server Manager

Manager 是目标服务器唯一管理入口。人工运行 `SCBL` 菜单；CI/自动化才直接使用子命令。

## 组件边界

SCBL 自有发布组件只有 5 个：Manager、Dedicated、Control Plane、Update Server、Assets。完整 ZIP 包含这 5 项，更新 ZIP 只含变化项。

EasyTier 与 DDNS-Go 是 Manager 管理的外部依赖：

- 不写入 `COMPONENT_VERSIONS.json`；
- 不进入 full/update ZIP；
- 有效安装与已校验缓存直接复用；
- 只在菜单显式操作时检查更新。

## 唯一入口

```bash
# 首次安装包
python3 server/packaging/scbl-server.py full

# 一个或多个变化组件的更新包；多个 Runtime 组件仍是一次部署事务
python3 server/packaging/scbl-server.py component manager
python3 server/packaging/scbl-server.py component control-plane \
  --runtime-manifest .work/inputs/target-server-package.json
python3 server/packaging/scbl-server.py component dedicated control-plane \
  --runtime-manifest .work/inputs/target-server-package.json
```

`full` 同时输出内部组件包 `SCBL-Server-vX.Y.Z-full.zip` 和给新服务器操作者使用的 `SCBL-Server-vX.Y.Z-installer.zip`。后者内含交互式 `SCBL-Installer.sh`、底层安装器、full ZIP 和校验文件；新服务器只需上传并解压 installer ZIP。

内部 `package_builder.py` 是库，不是第二个人工入口。不得为单次任务复制新的编译或发布脚本。
编译机上的 `target-server-package.json` 必须先从实际目标服务器 `/opt/scbl/current/server-package.json` 只读取得，不能使用编译机自己的 Runtime 代替。

## 部署不变量

- ZIP 为标准格式，清单和组件使用 SHA256。
- 同版本必须是相同字节；降级默认拒绝。
- Manager 与 Runtime 分开更新，避免跨两套安装机制的半事务。
- Runtime 候选复用当前未变化组件，更新 `server.assets` 会覆盖实际持久化的平衡配置。
- 切换前检查在线人数、migration checkpoint 和备份。
- 失败恢复 current、systemd unit 与旧服务状态。
- `previous` 是可操作的直接回滚点，不是只写不读的记录。

## 目录

```text
/usr/local/lib/scbl/scblctl.pyz       Manager
/usr/local/bin/SCBL                   人工入口
/opt/scbl/current                     当前 Runtime
/opt/scbl/previous                    直接回滚 Runtime
/opt/scbl/releases                    Runtime 版本
/opt/scbl/store/sha256                内容寻址组件仓库
/opt/scbl/external/easytier/current   外部 EasyTier
/var/lib/scbl                         持久数据
/var/lib/scbl/client-updates/scbl-server.json  客户端唯一公开契约
/var/backups/scbl                     备份
/etc/scbl/server.toml                 本机配置
```

`server.toml` 是本机唯一配置源；Manager 将当前实例的网络、控制面、Dedicated、玩法、公告和最新客户端制品原子投影为 `scbl-server.json`。内部组件清单与公告文件不再直接暴露给 3.0 客户端，也不再下发独立 `scbl-features.toml`。

SCBL 管理的 Runtime、EasyTier、客户端完整包、组件、清单备份和数据备份统一保留最近三代；current/previous 与仍被当前通道引用的制品始终受保护。无法确认归属的目录不删除。

客户端组件补丁发布后，Manager 可直接从服务器当前六组件封装完整客户端；无需重新编译。无论自动封装还是手动上传完整 ZIP，公网固定下载地址均为 `SCBL.zip`，发布时与清单原子切换，带版本号的不可变文件仅用于三代回滚。

## 常用操作

```bash
sudo SCBL status
sudo SCBL doctor
sudo SCBL deploy update --package update.zip --dry-run
sudo SCBL deploy update --package update.zip
sudo SCBL rollback
sudo SCBL maintenance prune
sudo SCBL maintenance prune --apply
sudo SCBL easytier status
sudo SCBL easytier update
```

完整安装、端口、首次 EasyTier 下载和验收见 [../INSTALLATION.md](../INSTALLATION.md)。
