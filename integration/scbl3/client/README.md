# SCBL 3.0 Windows 客户端编译、打包与发布手册

本文是 Windows 客户端唯一操作手册。覆盖 Launcher、Updater、Hooks、EasyTier、Route Guard、Client Files、组件补丁和完整客户端 ZIP。

服务端编译、首次安装和更新见 [`../server/INSTALLATION.md`](../server/INSTALLATION.md)。

## 1. 客户端包含什么

| 组件 | 作用 | 主要输出 |
|---|---|---|
| Launcher | 登录、公告、更新、网络编排和启动游戏 | `SCBL.exe` |
| Updater | 原子替换 Launcher/客户端文件并回滚 | `SCBL.Updater.exe` |
| Hooks | 游戏内 Uplay/Quazal 兼容和 Overlay | `uplay_r1_loader.dll` |
| MOD Gameplay | 选择模组模式时加载的玩法功能 | `scbl_gameplay.dll` |
| Route Guard | WinDivert 进程路由 | `route-guard.zip` |
| EasyTier | Windows 虚拟网络运行时 | `easytier-windows-x86_64.zip` |
| Client Files | 独立分发的客户端文件 | `client-files.zip` |

完整包不是额外组件；它把当前六类发布组件和必要说明装进一个普通 ZIP。

## 2. Windows 构建环境一次性准备

需要：

- Windows 10/11 x64。
- PowerShell 5.1 或 PowerShell 7。
- .NET 10 SDK。
- Visual Studio 2022 C++ Build Tools（C++ x86/x64）。
- Rustup 和仓库 `rust-toolchain.toml` 指定工具链。
- Protobuf `protoc`。
- Go 1.23.x。
- Git。

检查：

```powershell
dotnet --info
cargo --version
rustc --version
protoc --version
go version
git --version
```

脚本会优先复用仓库同级 `.toolchains` 中已登记的 Rust/Protobuf 工具，也会复用 NuGet、Cargo、Go 和已下载的 EasyTier/WinDivert 缓存。不要每次删除缓存。

不要使用 `-ExecutionPolicy Bypass`。若组织策略拒绝脚本，应修复受信工作区/脚本入口，或使用脚本中已经审计的直接构建参数；不要临时复制一套新的编译脚本。

## 3. 版本怎么改

只有准备一个新候选时才提版，普通源码验证不提版。

- 根目录 `VERSION_CLIENT`：完整客户端与 Launcher 的唯一版本。
- 根目录 `COMPONENT_VERSIONS.json`：各独立组件版本；其中 `clientVersion`、`launcherVersion` 必须与 `VERSION_CLIENT` 完全一致，构建和装包入口会自动拒绝错位。

例：只修改 Launcher，准备客户端 `3.0.5`：

```text
VERSION_CLIENT                         3.0.4 -> 3.0.5
COMPONENT_VERSIONS.clientVersion       3.0.4 -> 3.0.5
COMPONENT_VERSIONS.launcherVersion     3.0.3 -> 3.0.5
```

Hooks、Updater、Route Guard、EasyTier、Client Files 没有变化时，不提升它们的版本。

例：只修改 Hooks，则只提升 `hooksVersion`；如果 Launcher 或完整客户端版本变化，必须把 `VERSION_CLIENT`、`clientVersion` 和 `launcherVersion` 同时提升为同一个新版本。

检查版本：

```powershell
Set-Location D:\codex\SCBL3.0
$ClientVersion = (Get-Content .\VERSION_CLIENT -Raw).Trim()
$Versions = Get-Content .\COMPONENT_VERSIONS.json -Raw | ConvertFrom-Json
$ClientVersion
$Versions | ConvertTo-Json -Depth 5
```

## 4. 第一次把整个客户端编译一遍

先关闭 Launcher 和游戏，再执行：

```powershell
Set-Location D:\codex\SCBL3.0
$Output = Join-Path $PWD '.work\client-packages'

.\client\build_all_windows.ps1 -Fast -Package -OutputDir $Output
```

这一个入口会：

1. 停止 SCBL 受管运行进程。
2. 复用或准备 Windows EasyTier。
3. 构建 Route Guard。
4. 构建 Launcher。
5. 构建 Updater。
6. 用固定 Rust 工具链测试并构建 Hooks/MOD Gameplay。
7. 装配 `publish-single`。
8. 生成完整客户端 ZIP 和 `.sha256`。

默认使用增量缓存。只有明确确认缓存污染时才加 `-Clean`；日常不要为了“保险”全清缓存。

主要输出：

```text
client\ScblPublicLauncher\publish-single\
.work\client-packages\SCBL-Client-vX.Y.Z-win-x86.zip
.work\client-packages\SCBL-Client-vX.Y.Z-win-x86.zip.sha256
target\i686-pc-windows-msvc\release\hooks.dll
```

校验完整包：

```powershell
$ClientVersion = (Get-Content .\VERSION_CLIENT -Raw).Trim()
$Zip = Join-Path $PWD ".work\client-packages\SCBL-Client-v$ClientVersion-win-x86.zip"
Get-FileHash -LiteralPath $Zip -Algorithm SHA256
Get-Content -LiteralPath "$Zip.sha256"
```

如果你当前只想演练编译，到这里停止，不上传任何服务器。

## 5. 只编译变化组件

```powershell
# Launcher
.\client\build_all_windows.ps1 -Fast -LauncherOnly

# Hooks + MOD Gameplay
.\client\build_all_windows.ps1 -Fast -HooksOnly

# Updater
.\client\build_all_windows.ps1 -Fast -UpdaterOnly

# Route Guard
.\client\build_all_windows.ps1 -Fast -RouterOnly

# 只准备/更新完整包使用的 Windows EasyTier 输入
.\client\build_all_windows.ps1 -Fast -RuntimeOnly
```

Git 已有干净基线时可以使用：

```powershell
.\client\build_all_windows.ps1 -Fast -Auto
```

`-Auto` 根据 Git 变化选择组件。全仓尚未建立有效 Git 基线、所有文件都显示 untracked 时不要用它，应显式选择 `*Only` 或 `-Package`。

## 6. 变化点测试

正式候选前至少运行与修改范围对应的测试：

```powershell
# Launcher + Updater 测试
dotnet test .\client\ScblPublicLauncher\SplinterCellCNLauncher.sln -c Release

# Hooks（固定工具链，Release）
.\scripts\build-rust-components.ps1 -Release -HooksOnly

# Route Guard
Push-Location .\client\scbl-process-router
go test ./...
go vet ./...
Pop-Location
```

不需要为每次局部修改机械重跑无关组件。准备完整候选时，由 `build_all_windows.ps1 -Fast -Package` 在同一输入上只构建一次正式字节。

Windows 上的 `build-rust-components.ps1 -DedicatedOnly` 只能用于回归；可部署的 Dedicated 必须按服务端手册在 Linux 编译。

## 7. 制作客户端组件补丁

先把 `publish-single` 装配成组件目录。发布完整客户端前，目标服务器必须先拥有同一候选全部组件的基线，因此全新候选通常生成包含全部六类组件的 patch：

```powershell
$ComponentRoot = Join-Path $PWD '.work\client-components'
$PatchRoot = Join-Path $PWD '.work\client-patches'
$Components = @('launcher','hooks','route-guard','easytier','updater','client-files')

.\scripts\release\assemble-client-components.ps1 `
  -PublishRoot .\client\ScblPublicLauncher\publish-single `
  -OutputRoot $ComponentRoot `
  -Components $Components

New-Item -ItemType Directory -Force -Path $PatchRoot | Out-Null
.\scripts\release\build-client-component-patch.ps1 `
  -ComponentRoot $ComponentRoot `
  -Output (Join-Path $PatchRoot 'SCBL-Client-components.scblpatch') `
  -Components $Components
```

输出：

```text
.work\client-patches\SCBL-Client-components.scblpatch
.work\client-patches\SCBL-Client-components.scblpatch.sha256
```

仅发布一个变化组件时可缩小数组，例如：

```powershell
$Components = @('launcher')
```

发布组件后，服务器可从当前六组件直接封装完整 ZIP，不需要在服务器重新编译。若选择手动上传 Windows 已生成的完整 ZIP，Manager 仍会要求包内全部组件回执与服务器当前组件清单精确一致。

## 8. 把客户端包传到服务器

在 Windows PowerShell 中：

```powershell
$Target = 'root@192.168.1.252'
ssh $Target 'install -d -m 0700 /var/tmp/scbl-client-incoming'

scp .\.work\client-patches\SCBL-Client-components.scblpatch `
    .\.work\client-patches\SCBL-Client-components.scblpatch.sha256 `
    "${Target}:/var/tmp/scbl-client-incoming/"
```

通常只需上传组件 patch；只有准备验证或发布一份外部生成的完整包时，才额外上传 Windows 完整 ZIP。

正式/测试是两台独立服务器。要发布到哪一套客户端通道，就把同一不可变字节传到对应服务器。

## 9. 在服务器发布组件和完整包

登录目标服务器并运行：

```bash
sudo SCBL
```

进入 `客户端内容`：

1. 先选择 `发布组件更新`。
2. 选择 `使用服务器已有文件`。
3. 选择 `/var/tmp/scbl-client-incoming/` 中的 `.scblpatch`。
4. 核对组件版本、大小和 SHA256 后发布。
5. 在“立即封装并发布最新完整客户端”处直接回车。
6. Manager 从服务器当前六组件封装完整 ZIP、校验并原子发布。

如需手动上传完整 ZIP，返回 `客户端内容 -> 发布完整客户端 ZIP`；它与自动封装使用同一校验、固定地址和回滚事务。

也可先对完整 ZIP 做只读预检：

```bash
sudo SCBL publish-client \
  /var/tmp/scbl-client-incoming/SCBL-Client-vX.Y.Z-win-x86.zip \
  --note '本次中文更新说明' \
  --note-en 'English release note' \
  --dry-run
```

组件 patch 仍优先使用菜单发布，避免手工拼装组件路径。

## 10. 发布后取得下载链接

若服务端安装时配置：

```text
public_update_base_url = http://你的域名:18080
```

完整包链接固定为：

```text
http://你的域名:18080/SCBL.zip
```

服务器内部仍保存带版本号的不可变 ZIP 供三代回滚，但玩家只使用上述固定链接。

组件下载地址由服务器公开清单给出，格式为：

```text
http://你的域名:18080/components/artifacts/<组件>/<组件版本>/<文件名>
```

公开回读：

```powershell
$Url = 'http://你的域名:18080/SCBL.zip'
curl.exe -fI $Url
curl.exe -f -r 0-1023 $Url -o NUL -D -
```

发布完成后必须把完整包下载链接交给使用者；只报告服务器本地路径不算完成。

## 11. 客户端目录和运行数据

玩家可见根目录只保留：

```text
SCBL.exe
SCBL.Updater.exe
client_package_manifest.json
README/许可证文件
components/
data/
```

组件目录：

```text
components/hooks/         Hooks 与 MOD Gameplay
components/network/       EasyTier
components/route-guard/   Route Guard 和 WinDivert 安装载荷
```

每台电脑/Windows 用户的数据隔离在：

```text
data/<computer-name>/<windows-user>/
  config/
  logs/
  network/
  runtime/
  components/
  updates/
  diagnostics/
  backup_saves/
```

游戏 `SYSTEM` 目录只保留游戏必须直接读取的 `scbl.toml`、Hooks DLL、原版 DLL 备份和存档。Hooks 日志仍写入 Launcher 当前用户的 `data/.../logs/game/`。

Updater 完整更新时会把根目录 `SCBL.Updater.exe` 复制到 `data/.../updates/runner/`，从临时执行位置原子替换 Launcher 和组件；后续启动清理临时副本。

## 12. 自建服务器配置

服务端首次安装后会生成客户端设置模板：

```text
/opt/scbl-public/client_launcher_settings.json
```

将它放到客户端：

```text
客户端目录\data\计算机名\Windows用户名\config\launcher_settings.json
```

也可在 Launcher 右上角设置中填写正式/测试服务器地址和更新端口。保存后 Launcher 会完整停止 Route Guard/EasyTier，再按新地址重启。

默认测试服务器为 `test.example.invalid:11011`，测试更新端口为 `18081`。该默认值只用于没有已保存配置的新客户端；用户已经保存的自定义地址不会被升级覆盖。

Launcher 的公告、服务器契约、组件更新和完整包信息都从当前服务器的更新端口读取。正式服务器只接受与其公开版本精确一致的客户端；测试服务器可按本机设置允许本地测试版。

## 13. 失败时不要这样做

- 不要删除整个 `.work`、NuGet、Cargo、Go 或 EasyTier 缓存后盲目重编。
- 不要为一次任务新建另一份编译/发布脚本。
- 不要在 Windows 编译可部署 Dedicated。
- 不要在低性能正式服务器编译源码。
- 不要只上传完整 ZIP 而漏掉同一候选组件 patch。
- 不要覆盖服务器上同版本但不同 SHA256 的不可变包。
- 不要把密码、SSH 私钥、真实运行配置、日志或诊断包提交仓库。

## 14. 你第一次完整演练的最短路线

```text
检查工具链
-> 更新版本（若确实准备新候选）
-> build_all_windows.ps1 -Fast -Package
-> 校验完整 ZIP/SHA256
-> assemble-client-components.ps1
-> build-client-component-patch.ps1
-> 上传测试服务器
-> SCBL 先发布组件 patch
-> SCBL 再发布完整 ZIP
-> 公网完整下载与 Range 回读
-> 新目录解压并实机测试
```

熟悉这条路线后，正式发布复用同一已验收字节传到正式服务器，不重新编译。
