# SCBL 3.0 服务端下发契约

## 唯一入口

SCBL 3.0 客户端启动时只读取当前服务器更新端口上的：

```text
/scbl-server.json
```

该文件由 Manager 从本机 `server.toml`、当前通道组件状态、完整包状态和三类公告原子生成。客户端不再拼接 `networkBootstrap`、Launcher controls、独立公告文件或独立玩法文件。

## 文档内容

`scbl-server.json` 只包含当前服务器的最新事实：

- 服务器身份：`stable` 或 `test`；两台服务器完全独立，不存在“转正”。
- 当前客户端：唯一最新版本、当前组件/文件、完整包及 SHA256。
- 公网入口：域名或来源主机、一个游戏端口、QUIC/WSS、EasyTier 网络参数。
- 控制面和 Dedicated：虚拟地址、控制端口、配置端口、PRUDP 游戏端口。
- 公告：滚动、启动、更新三类当前有效内容；没有自定义更新公告时自动生成与当前客户端版本绑定的通用公告。

制品 ZIP/DLL/EXE 仍按文档中的 URL 下载，它们不是第二份配置契约。组件 bundle POST 只是下载优化，也不承载服务器状态。

## 本地玩法配置

服务端不生成、不发布 `scbl-features.toml`，`scbl-server.json` 也不携带玩法功能开关。客户端在每次启动游戏前直接把模式与 FOV 原子写入游戏目录：Official 不加载玩法 DLL，MOD 加载玩法 DLL 并启用其中全部已完成功能；FOV 对两个模式都生效。官方与模组存档由 Hooks 在本地隔离。

控制面仍接收每个在线玩家的 `official` / `mod` 运行状态，用于阻止不同玩法模式互相组队或加入；这属于联机完整性，不是服务端下发的玩法配置。

## 更新规则

- 安装/修复、服务端配置应用、客户端发布和公告修改完成后，Manager 最后写入一次新快照。
- 完整 release plan 在内部事务完成前不刷新公开快照；失败时客户端继续看到上一份完整文档。
- `server.mode` 决定读取本机 stable 或 test 组件状态，另一通道不会混入。
- bundle 请求不接收客户端传入的 channel，只使用当前 `scbl-server.json` 中的 `server.mode`。
- Manager 生成快照前会复核每个组件和完整包的真实文件、大小与 SHA256；损坏的公告/清单不会被当成“空配置”覆盖旧快照。
- 不发布最低客户端版本；服务端只给出当前唯一最新版本。
- `scbl-server.json` 不使用签名文件。客户端仍按文档内 SHA256 检查下载是否损坏。

## HTTP 暴露范围

Update Server 只公开：

- `/scbl-server.json`
- `/releases/...` 完整包或补丁 ZIP
- `/components/artifacts/...` 单组件制品
- `/components/bundles/v1` 组件合并下载接口

内部组件清单、公告源文件和旧 Launcher controls 不再对 3.0 客户端公开。
公开路径拒绝 `%` 编码、`..`、反斜杠、query 和越出受管根目录的符号链接。
