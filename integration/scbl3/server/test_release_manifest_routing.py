#!/usr/bin/env python3
from pathlib import Path

bootstrap = Path("server/bootstrap/install.sh").read_text(encoding="utf-8")
wrapper = Path("scripts/install-server.sh").read_text(encoding="utf-8")
builder = Path("server/manager/build.py").read_text(encoding="utf-8")
package_builder = Path("server/packaging/package_builder.py").read_text(encoding="utf-8")
deployment = Path("server/manager/scblctl/deployment.py").read_text(encoding="utf-8")
provision = Path("server/manager/scblctl/provision.py").read_text(encoding="utf-8")
config = Path("server/manager/scblctl/config.py").read_text(encoding="utf-8")

assert "scbl-package.json" in bootstrap
assert "scbl-package.sig" not in bootstrap
assert "openssl pkeyutl -verify" not in bootstrap
assert '"integrityPolicy"' in bootstrap
assert "--public-host" in bootstrap
assert 'PUBLIC_HOST=""' in bootstrap
for forbidden_network_install_path in (
    "curl ",
    "wget ",
    "urllib",
    "requests.",
    "github.com",
    "download_online_package",
):
    assert forbidden_network_install_path not in bootstrap
    assert forbidden_network_install_path not in provision
assert "server/bootstrap/install.sh" in wrapper
assert 'interpreter="/usr/bin/env python3"' in builder
assert "raise SystemExit(main())" in builder
assert '"server.dedicated"' in package_builder
assert '"server.easytier-core"' not in package_builder
assert '"server.easytier-cli"' not in package_builder
assert "external/easytier/current" in Path(
    "server/manager/scblctl/templates.py"
).read_text(encoding="utf-8")
assert 'kind not in {"full", "update"}' in package_builder
assert 'suffix.lower() != ".zip"' in deployment
assert "signature_verifier" not in deployment
assert 'integrityPolicy' in deployment
assert "COMPONENT_RESTART_UNITS" in deployment
assert "activate_release" in provision
assert '(Path("/etc/scbl"), 0, 0, 0o711)' in provision
assert "os.chown(dedicated_config, game, 0)" in provision
assert "self._configure_firewall(config)" in provision
assert "_recover_failed_install" in provision
assert "_restore_units" in provision
assert "/etc/scbl/server.toml" in config or "server.toml" in bootstrap

for required in (
    "dedicated_server",
    "scbl_control_plane.pyz",
    "scbl_update_server.py",
    "data/mp_balancing.ini",
):
    assert f'"{required}"' in deployment

combined = "\n".join((bootstrap, wrapper, builder, deployment, provision, config))
assert "scbl.env" not in combined
assert "/opt/scbl-public" not in combined
assert "migrate_legacy" not in combined
for obsolete in (
    Path("server/packaging/build-runtime.ps1"),
    Path("server/packaging/build-runtime-wsl.sh"),
    Path("server/packaging/build-runtime.sh"),
):
    assert not obsolete.exists(), obsolete

print("SCBL 3.0 component ZIP integrity, clean-install and rollback routing checks passed")
