# 客户端组件分发

## 权威边界

SCBL 客户端只信任当前配置的 SCBL 服务端。GitHub 是服务端管理员可选择的上游资产源，
不是客户端更新源：

```text
GitHub Release / rz / 服务端文件
                |
                v
       SCBL Server Manager
       文件名、版本、大小、SHA256 校验
                |
                v
   服务端不可变版本目录 + stable/test 清单
                |
                v
          SCBL Windows 客户端
```

因此，更换 GitHub 仓库、GitHub 暂时不可用或管理员使用本地测试二进制，都不会改变客户端
协议；客户端始终从服务端更新端口下载同源文件。

## GitHub 资产

每个组件有一个滚动正式标签：

| 组件 | 标签 | 文件 |
| --- | --- | --- |
| Launcher | 构建 artifact | `SCBL.exe` |
| Hooks | 构建 artifact | `uplay_r1_loader.dll` |
| Route Guard | 构建 artifact | `route-guard.zip` |
| EasyTier | 构建 artifact | `easytier-windows-x86_64.zip` |
| Updater | 构建 artifact | `SCBL.Updater.exe` |
| Client Files | 构建 artifact | `client-files.zip` |

每个 Release 同时包含 `component.json` 和 SHA256 sidecar。服务端先下载元数据，再只下载管理员
选择的组件文件。GitHub 工作流与服务端都拒绝“同版本、不同 SHA256”。

## 客户端判定

完整客户端的 `client_package_manifest.json` 包含 `componentVersions`，作为本地基线。客户端将
下载缓存状态覆盖到该基线上，再与服务端清单逐项比较：

- 服务端版本低于本地：拒绝降级；
- 服务端版本等于完整包基线：不下载；
- 服务端版本等于已下载版本：复用 SHA256 一致的缓存；
- 服务端版本高于本地：只下载该组件，验证大小和 SHA256 后缓存；
- Launcher 由 Updater 在退出后替换；Hooks 在启动游戏前应用；Route Guard、EasyTier、Updater 在下一次启动时事务应用；Client Files 立即应用。

正式服和测试服是两个独立实例。测试通过后，把同一组件原始字节发布到正式服；不在单台服务器内做 test→stable 提升，也不重新编译。Launcher 或整体协议变化后还必须发布同版本完整客户端 ZIP。
