from __future__ import annotations

import json
import re
import sys
import tomllib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")


versions = json.loads((ROOT / "COMPONENT_VERSIONS.json").read_text(encoding="utf-8"))
assert versions["schemaVersion"] == 2
for name in (
    "clientVersion",
    "launcherVersion",
    "serverReleaseVersion",
    "routeGuardVersion",
    "hooksVersion",
    "updaterVersion",
    "clientFilesVersion",
    "sharedApiVersion",
):
    assert SEMVER.fullmatch(str(versions[name])), f"{name} is not strict semver"

assert set(versions["serverComponents"]) == {
    "manager",
    "dedicated",
    "controlPlane",
    "updateServer",
    "assets",
}
for name, version in versions["serverComponents"].items():
    assert SEMVER.fullmatch(str(version)), f"serverComponents.{name} is not strict semver"
assert versions["serverComponents"]["dedicated"] == versions["dedicatedServer"]["version"]

assert not (ROOT / "server/packaging/easytier-inputs.json").exists()

client_version = (ROOT / "VERSION_CLIENT").read_text(encoding="utf-8").strip()
assert client_version == versions["clientVersion"]
assert client_version == versions["launcherVersion"]

version_props = (ROOT / "client/SCBL.Version.props").read_text(encoding="utf-8")
assert "VERSION_CLIENT" in version_props
assert client_version not in version_props
for project in (
    ROOT / "client/ScblPublicLauncher/SplinterCellCNLauncher.csproj",
    ROOT / "client/SCBL.Updater/SCBL.Updater.csproj",
):
    assert 'Import Project="..\\SCBL.Version.props"' in project.read_text(encoding="utf-8")

sys.path.insert(0, str(ROOT / "server/manager"))
from scblctl import __version__ as source_manager_version  # noqa: E402

assert source_manager_version == versions["serverComponents"]["manager"]
manager_build = (ROOT / "server/manager/build.py").read_text(encoding="utf-8")
assert "COMPONENT_VERSIONS.json" in manager_build
assert 'components.get("manager")' in manager_build

with (ROOT / "client/hooks/Cargo.toml").open("rb") as stream:
    hooks_version = tomllib.load(stream)["package"]["version"]
assert hooks_version == versions["hooksVersion"]

router = (ROOT / "client/scbl-process-router/main.go").read_text(encoding="utf-8")
assert 'var routerVersion = "development"' in router
assert not re.search(r'routerVersion\s*=\s*"[0-9]+\.[0-9]+\.[0-9]+"', router)
version_flag = router.index('flag.Bool("version"')
version_return = router.index("if *showVersion")
side_effect = router.index("if cfg.LogFile")
run_call = router.index("if err := run(cfg)")
assert version_flag < version_return < side_effect < run_call

router_build = (ROOT / "client/scbl-process-router/build_windows.ps1").read_text(encoding="utf-8")
assert "COMPONENT_VERSIONS.json" in router_build
assert "routeGuardVersion" in router_build
assert "main.routerVersion=$RouteGuardVersion" in router_build
assert "-version" in router_build and "ReportedVersion" in router_build

build_workflow = (ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8")
ci_workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
assert "Online component build" in build_workflow
assert "cargo build --locked --release -p dedicated_server" in build_workflow
assert "Test Route Guard" in ci_workflow
assert "go test ./..." in ci_workflow
assert "Manager unit tests" in ci_workflow

catalog = json.loads(
    (ROOT / "server/manager/scblctl/client_component_catalog.json").read_text(encoding="utf-8")
)
assert set(catalog["components"]) == {
    "launcher",
    "hooks",
    "route-guard",
    "easytier",
    "updater",
    "client-files",
}

assert versions["easyTierComponentVersion"] == versions["easyTierVersion"]

print("SCBL authoritative version source and build injection checks passed.")
