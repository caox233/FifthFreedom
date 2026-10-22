# SCBL 3.0 服务端安装、编译与维护手册

本文仅维护现有 Linux 服务端。新部署默认使用原生 Windows 服务端，安装、可视化管理和 Windows 构建见 [`windows/README.md`](windows/README.md)。

以下内容覆盖旧 Linux 构建机、首次安装、已有服务器更新、回滚和客户端包发布，不与 Windows 路径混用。

推荐固定分工：

- Linux 编译机：保存源码、Rust/Cargo 和构建缓存，生成服务端 ZIP。
- 正式/测试服务器：只安装经过校验的 ZIP，不保存源码，不安装 Rust，不执行编译。
- Windows 本机：编译客户端。客户端操作见 [`../client/README.md`](../client/README.md)。

当前可按以下拓扑理解：

| 机器 | 用途 |
|---|---|
| Windows 本机 | 客户端源码、测试、编译、patch 和完整 ZIP |
| `192.168.1.250` | Linux 服务端编译/测试机，可保存源码和构建缓存 |
| `192.168.1.252` | 正式运行服务器，只接收并安装二进制 ZIP |

正式服和测试服使用同一套服务端程序、同一种 ZIP。两台机器的配置、数据和客户端发布目录完全独立；`server.mode` 只声明本机服务 `stable` 还是 `test` 客户端。

## 日常最快入口

首次准备源码和工具链后，以后在 Linux 编译机完整编译只需要：

```bash
cd /srv/src/SCBL3.0
python3 server/packaging/scbl-server.py full
```

脚本会自动读取版本、查找系统或 `$HOME/.cargo/bin/cargo`、复用 `.work/cargo-linux` 缓存、生成 ZIP 与 `.sha256`，并打印输出路径和 SHA256。日常不需要执行 `source "$HOME/.cargo/env"`，也不需要手工设置版本变量、输出目录或缓存目录。

`full` 会额外生成一个给操作者使用的可视化安装包：

```text
.work/server-packages/SCBL-Server-v<当前版本>-installer.zip
```

它已经内含安装菜单、底层安装器、full ZIP 和内部校验文件。新服务器只需要这一份 installer ZIP。

只更新一个组件同样是一条命令；已有服务器的 Runtime 组件需要额外提供从目标服务器取得的当前清单：

```bash
# Manager
python3 server/packaging/scbl-server.py component manager

# Dedicated 示例
python3 server/packaging/scbl-server.py component dedicated \
  --runtime-manifest .work/inputs/prod-server-package.json
```

后续章节是首次准备、跨机器传输和故障排查时才需要看的完整步骤。

## 1. 服务端结构

完整服务端 ZIP 只含 5 个 SCBL 自有组件：

| 组件 | 作用 | 更新时重启 |
|---|---|---|
| Manager | 安装、更新、回滚、备份和菜单 | 否 |
| Dedicated | 游戏业务与数据库 | Dedicated |
| Control Plane | Launcher 登录、状态与控制接口 | Control Plane |
| Update Server | 公告、客户端组件和完整包下载 | Update Server |
| Assets | Dedicated 使用的平衡配置 | Dedicated |

EasyTier 和 DDNS-Go 是外部组件，不进入服务端完整包：

- Manager 菜单负责首次下载、检查、激活和更新。
- 当前安装完整时，重复安装/修复直接复用，不再次下载。
- EasyTier 下载缓存位于 `/var/cache/scbl/external/easytier`。

主要运行目录：

```text
/opt/scbl/current                     当前 Runtime
/opt/scbl/previous                    直接回滚 Runtime
/opt/scbl/releases                    不可变 Runtime 目录
/opt/scbl/store/sha256                SCBL 组件内容仓库
/opt/scbl/external/easytier/current   当前 EasyTier
/var/lib/scbl                         数据库和运行数据
/var/lib/scbl/client-updates          客户端发布目录
/var/backups/scbl                     备份
/etc/scbl/server.toml                 本机配置
```

## 2. Linux 编译机一次性准备

推荐 Debian 12 / Ubuntu 24.04。以下操作只在性能较好的 Linux 编译机执行，不在 `192.168.1.252` 这类正式运行服务器执行。

```bash
sudo apt update
sudo apt install -y python3 build-essential pkg-config libssl-dev protobuf-compiler zip git curl
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
source "$HOME/.cargo/env"
python3 --version
cargo --version
protoc --version
```

将完整 SCBL3.0 源码放到编译机，例如 `/srv/src/SCBL3.0`。不要只复制某个变更文件；Cargo workspace、`Cargo.lock`、共享 API 和直接依赖必须完整。

创建可复用缓存和输出目录：

```bash
sudo install -d -m 0755 /var/cache/scbl-build/cargo-target
cd /srv/src/SCBL3.0
mkdir -p .work/server-packages .work/inputs
```

以后重复编译继续使用 `/var/cache/scbl-build/cargo-target`，不要每次从零下载和编译依赖。

## 3. 版本规则

版本源只有根目录 `COMPONENT_VERSIONS.json`：

- `serverReleaseVersion`：本次服务端组合版本。
- `serverComponents.manager`：Manager。
- `serverComponents.dedicated`：Dedicated。
- `serverComponents.controlPlane`：Control Plane。
- `serverComponents.updateServer`：Update Server。
- `serverComponents.assets`：Assets。

只提升实际变化的组件版本，并同时提升 `serverReleaseVersion`。不要在手册或命令中写死历史版本。读取当前版本：

```bash
cd /srv/src/SCBL3.0
SERVER_VERSION="$(python3 -c 'import json; print(json.load(open("COMPONENT_VERSIONS.json"))["serverReleaseVersion"])')"
echo "$SERVER_VERSION"
```

## 4. 第一次编译完整服务端 ZIP

```bash
cd /srv/src/SCBL3.0
SERVER_VERSION="$(python3 -c 'import json; print(json.load(open("COMPONENT_VERSIONS.json"))["serverReleaseVersion"])')"

python3 server/packaging/scbl-server.py full \
  --output-dir "$PWD/.work/server-packages" \
  --cargo-target-dir /var/cache/scbl-build/cargo-target

(
  cd .work/server-packages
  sha256sum -c "SCBL-Server-v${SERVER_VERSION}-full.zip.sha256"
  sha256sum -c "SCBL-Server-v${SERVER_VERSION}-installer.zip.sha256"
)
```

输出：

```text
.work/server-packages/SCBL-Server-v<当前版本>-full.zip
.work/server-packages/SCBL-Server-v<当前版本>-full.zip.sha256
.work/server-packages/SCBL-Server-v<当前版本>-installer.zip
.work/server-packages/SCBL-Server-v<当前版本>-installer.zip.sha256
```

完整包不包含数据库、配置、EasyTier、DDNS-Go、源码、Cargo 缓存或旧 release。

## 5. 把二进制包传到低性能目标服务器

目标服务器只需要 Python 3.11+、基础系统工具和已经构建好的 installer ZIP。

在 Linux 编译机执行（主机名按实际目标替换）：

```bash
TARGET=root@192.168.1.252
SERVER_VERSION="$(python3 -c 'import json; print(json.load(open("COMPONENT_VERSIONS.json"))["serverReleaseVersion"])')"
ssh "$TARGET" 'install -d -m 0700 /var/tmp/scbl-incoming'
scp ".work/server-packages/SCBL-Server-v${SERVER_VERSION}-installer.zip" \
  "${TARGET}:/var/tmp/scbl-incoming/"
```

使用已配置的 SSH 密钥或你自己的交互登录。不要把密码、私钥或身份文件内容写进仓库和脚本。

## 6. 正式服务器首次安装

推荐直接使用交互式安装入口：

```bash
ssh root@192.168.1.252
cd /var/tmp/scbl-incoming
unzip SCBL-Server-v*-installer.zip -d scbl-installer
cd scbl-installer
bash SCBL-Installer.sh
```

它会显示安装菜单，自动找到内置 full ZIP、校验 `.sha256`，然后让你选择正式/测试服务器、填写域名并确认安装。新服务器直接回车即可开始；如果检测到已经安装的服务器，可选择强制修复安装，保留现有身份、端口、网络密钥和数据库。底层仍调用 `install.sh`，不会绕过任何校验和回滚门禁。

以下长参数方式只用于自动化或排查：

登录目标服务器：

```bash
ssh root@192.168.1.252
cd /var/tmp/scbl-incoming
sha256sum -c SCBL-Server-v*-full.zip.sha256
sudo apt update
sudo apt install -y python3 ufw iproute2 unzip curl
```

正式服示例：

```bash
sudo bash install.sh \
  --package SCBL-Server-v<当前版本>-full.zip \
  --mode stable \
  --server-id scbl-prod-01 \
  --game-port 11010 \
  --update-port 18080 \
  --control-port 19080 \
  --public-update-base-url http://你的域名:18080
```

测试服示例：

```bash
sudo bash install.sh \
  --package SCBL-Server-v<当前版本>-full.zip \
  --mode test \
  --server-id scbl-test-01 \
  --game-port 11011 \
  --update-port 18081 \
  --control-port 19081 \
  --public-update-base-url http://你的域名:18081
```

说明：

- `<当前版本>` 替换为 ZIP 文件名中的实际版本。
- 一个游戏端口同时承载 WSS/TCP 与 QUIC/UDP；例如 `11011/TCP` 和 `11011/UDP` 都要做路由器映射。
- `--public-update-base-url` 用于生成客户端组件和完整包的公网下载链接。
- 客户端已经保存游戏域名时可省略 `--public-host`；它不是下载地址。
- 安装器会持久开启 IPv4 转发并配置 UFW。
- 检测到已有 `/etc/scbl/server.toml` 时，首次安装会停止，不会覆盖现有实例。

安装完成后：

```bash
sudo SCBL status
sudo SCBL doctor
sudo SCBL
```

首次安装会在启动服务前自动检查并安装 EasyTier；已有完整版本或缓存会直接复用。安装后可在 `SCBL -> 外部组件` 中查看、修复或主动更新 EasyTier，并按需要配置 DDNS-Go。

## 7. 已有服务器只更新变化组件

### 7.1 先取得目标服务器当前 Runtime 清单

更新 ZIP 必须绑定目标服务器当前 `/opt/scbl/current/server-package.json`。在 Linux 编译机执行：

```bash
cd /srv/src/SCBL3.0
mkdir -p .work/inputs
scp root@192.168.1.252:/opt/scbl/current/server-package.json \
  .work/inputs/prod-server-package.json
python3 -m json.tool .work/inputs/prod-server-package.json >/dev/null
```

不要把编译机自己的 `/opt/scbl/current` 当作正式服务器基线。

### 7.2 示例：只更新 Dedicated

先修改 `COMPONENT_VERSIONS.json` 中 `serverReleaseVersion` 和 `serverComponents.dedicated`，再执行：

```bash
export PATH="$HOME/.cargo/bin:$PATH"
python3 server/packaging/scbl-server.py component dedicated \
  --runtime-manifest "$PWD/.work/inputs/prod-server-package.json" \
  --output-dir "$PWD/.work/server-packages" \
  --cargo-target-dir /var/cache/scbl-build/cargo-target
```

### 7.3 示例：只更新 Control Plane

```bash
python3 server/packaging/scbl-server.py component control-plane \
  --runtime-manifest "$PWD/.work/inputs/prod-server-package.json" \
  --output-dir "$PWD/.work/server-packages" \
  --cargo-target-dir /var/cache/scbl-build/cargo-target
```

### 7.4 多个 Runtime 组件一起更新

```bash
python3 server/packaging/scbl-server.py component dedicated control-plane \
  --runtime-manifest "$PWD/.work/inputs/prod-server-package.json" \
  --output-dir "$PWD/.work/server-packages" \
  --cargo-target-dir /var/cache/scbl-build/cargo-target
```

只产生一个普通 ZIP，未列出的组件从目标服务器当前 Runtime 复用，不重新编译、下载或装包。

### 7.5 Manager 更新必须单独打包

```bash
python3 server/packaging/scbl-server.py component manager \
  --output-dir "$PWD/.work/server-packages" \
  --cargo-target-dir /var/cache/scbl-build/cargo-target
```

Manager 不能与 Runtime 组件混在同一个更新 ZIP。Manager 和 Runtime 同批变化时，先安装 Manager ZIP，再安装 Runtime ZIP。

## 8. 在目标服务器安装更新 ZIP

在编译机校验并上传：

```bash
(cd .work/server-packages && sha256sum -c SCBL-Server-v*-update.zip.sha256)
scp .work/server-packages/SCBL-Server-v*-update.zip \
  .work/server-packages/SCBL-Server-v*-update.zip.sha256 \
  root@192.168.1.252:/var/tmp/scbl-incoming/
```

在正式服务器执行：

```bash
cd /var/tmp/scbl-incoming
sha256sum -c SCBL-Server-v*-update.zip.sha256
sudo SCBL deploy update --package ./SCBL-Server-v<当前版本>-<组件>-update.zip --dry-run
sudo SCBL deploy update --package ./SCBL-Server-v<当前版本>-<组件>-update.zip
sudo SCBL status
sudo SCBL doctor
```

也可运行 `sudo SCBL`，进入 `服务端部署 -> 更新变化组件 -> 使用服务器已有文件`。

正式服无法确认在线人数或检测到玩家在线时默认停止；菜单会显示人数并要求明确输入 `FORCE`。命令行只有操作者确实决定中断在线玩家时才加 `--force-online`。测试服部署不受在线人数门禁阻断。

部署过程会校验 ZIP/组件 SHA256、备份、复用未变化组件、原子切换，并只重启受影响服务。失败会恢复旧 Runtime。

## 9. 回滚、备份和清理

```bash
sudo SCBL rollback
sudo SCBL status
sudo SCBL doctor
```

优先使用 `sudo SCBL` 中的 `数据与备份`、`回滚到上一运行版本` 和维护清理菜单。

所有 SCBL 管理的 Runtime、EasyTier、数据备份、客户端完整包、组件制品和组件清单备份统一保留最近三代；当前版本和直接回滚版本始终受保护。无法确认归属的文件不删除。

## 10. 客户端完整包和补丁发布

客户端组件在 Windows 构建；服务端可直接用当前六组件封装完整 ZIP，不会重新编译 Windows 程序：

1. 按 [`../client/README.md`](../client/README.md) 生成 `.scblpatch`；也可以另行上传已生成的完整 ZIP。
2. 上传到目标服务器 `/var/tmp/scbl-client-incoming/`。
3. 运行 `sudo SCBL -> 客户端内容`。
4. 选择“发布组件更新”，发布同一候选的组件 patch。
5. 发布成功后直接回车，Manager 会从当前六组件封装并发布完整 ZIP；也可在客户端内容菜单单独执行。
6. 手动上传完整 ZIP 也使用同一发布事务。两种方式都会保留带版本号的不可变文件，并原子更新固定下载地址：

```text
<public-update-base-url>/SCBL.zip
```

正式服和测试服是两台独立服务器；在对应服务器发布，就只改变该服务器服务的客户端通道。

## 11. 每次操作的最小验收

服务端安装/更新后：

```bash
sudo SCBL status
sudo SCBL doctor
```

客户端发布后：

```bash
curl -fI "http://你的域名:更新端口/SCBL.zip"
curl -fH 'Range: bytes=0-1023' \
  "http://你的域名:更新端口/SCBL.zip" \
  -o /dev/null -D -
```

必须分别记录：源码测试、ZIP/SHA256、部署后的服务健康、公开下载回读和真实客户端/游戏会话。自动检查不能代替实机。

## 12. 你第一次完整演练的推荐顺序

1. Windows 本机按客户端手册构建变化组件和 `.scblpatch`，但先不发布。
2. Linux 编译机按本手册生成服务端 full ZIP。
3. 在测试服务器完成首次安装、EasyTier 安装和 `status/doctor`。
4. 将 Windows 生成的组件 patch 上传测试服务器；发布后由服务器直接封装完整 ZIP。
5. 使用新目录客户端连接测试服务器做实机。
6. 流程熟悉后，再对正式服务器执行“取得当前清单 -> 构建变化组件 -> dry-run -> 安装 -> 验收”。

正式服务器不承担任何编译工作；以后升级只需接收对应二进制 ZIP。
