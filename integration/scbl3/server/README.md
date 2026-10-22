# SCBL Server 3.0

The production server is a binary-only Linux deployment. It does not compile
source code and does not use the retired 1.x installer or invitation-test
management stack.

Current source ownership:

- `bootstrap/`: clean Linux bootstrap entry point.
- `manager/`: the `SCBL` management CLI, deployment, client publishing, DDNS,
  diagnostics, backup, and rollback logic.
- `dedicated-server/`: the Rust game backend.
- `packaging/`: standard ZIP package builder with component SHA256 integrity checks.
- `quazal/`: protocol implementation shared by the dedicated server.
- `scbl_control_plane.pyz`: overlay-only control plane; built from the modular Python sources into one deployable zipapp.
- `scbl_update_server.py`: public client update and component file server.

完整的 Linux 编译机准备、full/组件 ZIP、正式/测试首次安装、端口映射、
更新、备份和故障处理见 [`INSTALLATION.md`](INSTALLATION.md)。底层组件架构和 Manager
边界见 [`manager/README.md`](manager/README.md)。
