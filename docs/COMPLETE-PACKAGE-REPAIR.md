# 完整候选包一致性与重新安装

这份步骤用于排查 `RELEASE_MISMATCH`、`PACKAGE_CATALOG_MISMATCH` 和残留组件，
不是摄影机闪退的修复声明。安装本轮新候选后，仍须按交付说明验证相应问题。
只使用交付条目中的 **完整 program ZIP**；Windows 组件 artifact 或单独 DLL 不能代替它。

1. 所有人结束本次游戏，房主正常关闭旧启动器及它拥有的服务端。保留旧程序目录、
   `data`、原游戏文件、`.scbl-backups`、安装记录和个人存档。不删除或覆盖它们，
   不通过“恢复默认存档”解决包身份问题。
2. 每人下载同一交付条目、同一构建的完整包。若下载的是 GitHub artifact 外层 ZIP，
   先取出内层 `fifth-freedom-1.1.0-migration1-program.zip`。在 Windows PowerShell
   中进入它所在目录并执行下面命令，将结果与该交付条目公布的**内层 ZIP** SHA256 比较。
   文件名和界面上的版本字符串相同并不能代替哈希一致。

   ```powershell
   Get-FileHash -Algorithm SHA256 -LiteralPath '.\fifth-freedom-1.1.0-migration1-program.zip'
   ```

3. 将内层 ZIP **全部解压到新的可写目录**，不要覆盖旧目录或从旧包复制 DLL。
   新目录必须同时包含 `FifthFreedom.exe`、`runtime`、`package` 和 `licenses`；
   `runtime` 中的 `uplay_r1_loader.dll`、`scbl_gameplay.dll`、
   `FifthFreedom.Server.exe` 及其校验文件不得拆开。不要编辑目录清单来“配平”摘要。
4. 在新包根目录执行下面的只读命令。各端的 release/compat 字符串、目录清单摘要及
   四组件摘要均应与同一交付包一致。输出只在本机核对，不上传包含用户路径的完整输出。
   启动器和服务端随后仍执行自己的完整目录、嵌入身份和组件校验；这些命令不会绕过它们。

   ```powershell
   $catalog = Get-Content -LiteralPath '.\package\RELEASE-CATALOG.json' -Raw -Encoding UTF8 | ConvertFrom-Json
   $catalog | Select-Object schema, release_id, compat_id
   Get-FileHash -Algorithm SHA256 -LiteralPath '.\package\RELEASE-CATALOG.json'
   Get-FileHash -Algorithm SHA256 -LiteralPath '.\FifthFreedom.exe', '.\runtime\uplay_r1_loader.dll', '.\runtime\scbl_gameplay.dll', '.\runtime\FifthFreedom.Server.exe'
   ```

5. 只打开新目录里的 `FifthFreedom.exe`，不要使用仍指向旧目录的快捷方式。
   选“本地模式”，由一台电脑通过创建确认启动**新包自带**服务端；其他人确认发现的房主，
   或手动输入房主实际 IPv4 和界面给出的 API 端口。不要继续连接旧目录仍运行的服务端。
   如果端口被占用，先在本机确认占用程序的路径，再正常退出相应旧程序；不要批量结束同名
   或不相关进程，不修改防火墙来绕过身份校验。
6. 新目录不会自动迁入旧 `data`，房主账号数据库也不会自动合并。先用同包的干净测试环境
   验证连接；旧账号数据迁移另行核对，原数据保留。游戏个人存档无需清空。
   手动选择 `Blacklist_DX11_game.exe` 后由启动器执行原有带校验、备份及所有权记录的部署。
   如果提示游戏目录存在外部 `scbl_gameplay.dll`、备份不匹配或文件正被占用，就保留现场，
   使用已保留的干净游戏副本或先查清第三方组件；不要删安装记录、手动换 DLL 或伪造校验值。
7. 只有在各端包一致、房主身份已确认后，才由玩家手动开始对应联测。若仍失败，集中反馈
   “交付构建编号、界面错误码、失败发生在连接/登录/启动/入局哪一步”即可；本步骤不要求
   再上传原始日志、账号、IP、口令或存档。已经运行的游戏/服务器不会通过替换磁盘文件
   自动升级已锁定的身份，必须正常退出后从所核验包重新开启。

## 错误和兼容矩阵

| 条件 | 当前预期行为 |
| --- | --- |
| 同一完整包，四角色身份/字节、目录摘要都一致 | 通过包身份这一关；不代表网络、房间或游戏生命周期已通过 |
| 完整包 `release_id` 不同，即使 `compat_id` 相同 | `RELEASE_MISMATCH`，拒绝 |
| release 相同而 `compat_id` 不同 | `COMPAT_MISMATCH`，拒绝 |
| 两次构建 release/compat 字符串相同，目录摘要不同 | `PACKAGE_CATALOG_MISMATCH`，拒绝；仍需所有端和房主用同一完整构建 |
| 四角色缺失或声明混用 | `PACKAGE_MIXED` 或本机目录校验拒绝；不拼装单 DLL 修复 |
| 请求没有包身份 | `PACKAGE_IDENTITY_REQUIRED`，与 `RELEASE_MISMATCH` 分开调查 |
| 声明与已加载 DLL、包内实际文件不符 | 本机部署/包身份校验拒绝；不能靠修改声明解决 |

`da1805e` 与 `dcb97d0` 的已核验公开完整包，release 都是
`fifth-freedom-1.1.0-migration1`、compat 都是 `scbl-lan-1.1.0`，但目录摘要分别为
`be66b742755cc6a4f73b30ec7b475969069bf0987cfc3b4cd8acb630a4d2f2ea` 和
`511c898e4c0a98e677445d88f2376aed7b1a559a39314d2b21bc97b0fbb10c7f`。
两版原生源码没有差异，重新构建后的 DLL 字节有差异；这既不证明玩家混装，
也不证明更新包能修复原生闪退。截图上的“SCBL LAN”文字不能确定实际四组件或房主身份。
本轮记录见[兼容性调查](tasks/package-compatibility-feedback.md)。
