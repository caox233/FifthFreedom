#!/usr/bin/env python3
import json
from pathlib import Path

program = Path("client/SCBL.Updater/Program.cs").read_text(encoding="utf-8")
updater_progress = Path("client/SCBL.Updater/UpdaterProgressWindow.cs").read_text(encoding="utf-8")
updater_application = Path("client/SCBL.Updater/UpdaterApplication.cs").read_text(encoding="utf-8")
updater_contracts = Path("client/SCBL.Updater/UpdateContracts.cs").read_text(encoding="utf-8")
updater_planner = Path("client/SCBL.Updater/PackageInstallPlanner.cs").read_text(encoding="utf-8")
updater_transaction = Path("client/SCBL.Updater/FileInstallTransaction.cs").read_text(encoding="utf-8")
updater_processes = Path("client/SCBL.Updater/WindowsUpdaterProcessBoundary.cs").read_text(encoding="utf-8")
updater_sources = "\n".join(
    (program, updater_application, updater_contracts, updater_planner, updater_transaction, updater_processes)
)
restart = Path("client/SCBL.Updater/RestartCoordinator.cs").read_text(encoding="utf-8")
local = Path("client/ScblPublicLauncher/Services/LocalClientUpdateService.cs").read_text(encoding="utf-8")
components = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(
        Path("client/ScblPublicLauncher/Services").glob("ClientComponentUpdateService*.cs")
    )
)
component_ui = Path("client/ScblPublicLauncher/MainWindow.ClientUpdates.cs").read_text(encoding="utf-8")
component_monitor = Path("client/ScblPublicLauncher/Application/ComponentUpdateMonitorCoordinator.cs").read_text(encoding="utf-8")
component_coordinator = Path("client/ScblPublicLauncher/Application/ClientUpdateCoordinator.cs").read_text(encoding="utf-8")
window_main = Path("client/ScblPublicLauncher/MainWindow.xaml.cs").read_text(encoding="utf-8")
app = Path("client/ScblPublicLauncher/App.xaml.cs").read_text(encoding="utf-8")

assert "new UpdaterApplication(" in program
assert "new TransactionalUpdateInstaller()" in program
assert "new WindowsUpdaterProcessBoundary()" in program
assert "new UpdaterProgressWindow()" in program
assert "Task.Run(() => updater.Run(args))" in program
assert '"--restart-helper"' in program
assert "正在更新 / Updating" in updater_progress
assert "UpdateActivityBar" in updater_progress
assert "OptimizedDoubleBuffer" in updater_progress
assert "OnFormClosing" in updater_progress
assert "ShowFailure" in updater_progress
assert '"--restart-helper"' in updater_application
assert "_restart.RunHelper(" in updater_application
assert "_restart.ScheduleAfterUpdaterExit(" in updater_application
assert "_restart.LaunchImmediately(" in updater_application
assert "Environment.ProcessId.ToString()" in restart
assert 'Path.Combine(target, "SCBL.exe")' in restart
assert "WaitForExit(ParentExitTimeoutMs)" in restart
assert "LaunchAttempts = 20" in restart
assert "Thread.Sleep(LaunchRetryDelayMs)" in restart
assert "LaunchSurvivalCheckMs = 3000" in restart
assert "Updated launcher remained running" in restart
assert 'string launcherExe = Path.Combine(baseDir, "SCBL.exe");' in local
assert "Environment.ProcessPath" in local

assert 'LauncherComponentName = "launcher"' in components
component_catalog = json.loads(
    Path("server/manager/scblctl/client_component_catalog.json").read_text(encoding="utf-8")
)["components"]
assert component_catalog["client-files"] == {
    "fileName": "client-files.zip",
    "section": "files",
    "updateMode": "immediate",
}
assert "manifest.Files.OrderBy" in components
assert "StagedComponentBootstrapService.ApplyClientFiles(bundle)" in components
assert "GetPendingComponentUpdatesAsync" in components
assert "GetComponentSourceAsync" in components
assert "_getServerDescriptorAsync" in components
assert 'new(updateBase, "scbl-server.json")' in components
assert "client_components_v2.json" not in components
assert "pollInterval ?? TimeSpan.FromSeconds(60)" in component_monitor
assert "IsGameSessionActiveForNetworkControl() || _isLaunchFlowActive" in window_main
assert "Component update detected while game is active" in component_monitor
assert "EnsureLatestComponentsBeforeLaunchAsync" in component_ui
assert "ClientUpdateHostCommandKind.RestartLauncher" in component_ui
assert "Component activation restart requested" in component_ui
assert "HttpCompletionOption.ResponseHeadersRead" in components
assert "CalculateArtifactDownloadTimeout" in components
assert "Timeout.InfiniteTimeSpan" in components

route_guard = Path("client/scbl-process-router/main.go").read_text(encoding="utf-8")
launcher = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
router_service = Path("client/ScblPublicLauncher/Services/ProcessRouterService.cs").read_text(encoding="utf-8")
control_models = Path("client/ScblPublicLauncher/Models/ControlPlaneModels.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
tunnel_store = Path("client/ScblPublicLauncher/Services/EasyTierConfigStore.cs").read_text(encoding="utf-8")
assert "traffic-fallback" not in launcher
assert "TryReadGameRouteStatus" not in launcher
assert "TryReadGameRouteStatus" not in router_service
assert "game-route-status.json" not in router_service
assert "game-route-history.jsonl" not in router_service
assert "shouldConvertToVirtualBroadcast" not in route_guard
assert "sendBroadcastFanout" not in route_guard
assert "BROADCAST-FANOUT" not in route_guard
assert "HOST-DETECT" not in route_guard
assert "10.66.0.255 broadcasts remain unchanged" in route_guard
assert "public int? TcpPort" in control_models
assert "bind_device = true" in tunnel_store

updater_project = Path("client/SCBL.Updater/SCBL.Updater.csproj").read_text(encoding="utf-8")
updater_manifest = Path("client/SCBL.Updater/app.manifest").read_text(encoding="utf-8")
window = launcher
driver_bootstrap = Path("client/ScblPublicLauncher/Services/WinDivertBootstrapService.cs").read_text(encoding="utf-8")
client_layout = Path("client/ScblPublicLauncher/Services/ClientInstallLayout.cs").read_text(encoding="utf-8")
build_script = Path("client/build_all_windows.ps1").read_text(encoding="utf-8")
launcher_build_script = Path("client/ScblPublicLauncher/build_publish.ps1").read_text(encoding="utf-8")
package_script = Path("client/create_client_full_package.ps1").read_text(encoding="utf-8")
component_assembly = Path("scripts/release/assemble-client-components.ps1").read_text(encoding="utf-8")
component_bootstrap = Path("client/ScblPublicLauncher/Services/StagedComponentBootstrapService.cs").read_text(encoding="utf-8")
component_receipts = Path("client/ScblPublicLauncher/Services/InstalledComponentReceiptService.cs").read_text(encoding="utf-8")
easytier_download = Path("client/easytier/download_easytier_windows.ps1").read_text(encoding="utf-8")
assert "COMPONENT_VERSIONS.json" in easytier_download
assert "easyTierVersion" in easytier_download
assert "ReleaseWinDivertDriverServices(target)" in updater_processes
assert "NormalizeSafeRelativePath" in updater_planner
assert "AtomicJsonFile.Write" in updater_transaction
assert "TransactionPhase.RollingBack" in updater_transaction
assert "beforeRecoveryMutation" in updater_transaction
assert "Update failed; relaunching the existing client" in updater_application
assert "ApplicationManifest>app.manifest" in updater_project
assert "<OutputType>WinExe</OutputType>" in updater_project
assert "<UseWindowsForms>true</UseWindowsForms>" in updater_project
assert 'requestedExecutionLevel level="requireAdministrator"' in updater_manifest
assert 'Verb = "runas"' in local
assert '"component update activation restart"' in component_coordinator
assert "preserveTunnelRuntime: false" in window
assert "EnsureCurrentDriverAsync" in window
assert "WinDivertPayloadRelativePath" in driver_bootstrap
assert 'UpdaterRelativePath = "SCBL.Updater.exe"' in client_layout
assert 'HooksRelativePath = "components/hooks/uplay_r1_loader.dll"' in client_layout
assert 'EasyTierCoreRelativePath = "components/network/easytier-core.exe"' in client_layout
assert 'WinDivertPayloadRelativePath = "components/route-guard/WinDivert64.payload.sys"' in client_layout
assert 'Copy-Item -Force $WinDivertSys (Join-Path $RouteGuardComponent "WinDivert64.payload.sys")' in build_script
assert 'Copy-Item -Force $UpdaterBuild (Join-Path $Publish "SCBL.Updater.exe")' in build_script
assert '& $HooksScript -Release -HooksOnly' in build_script
assert 'Join-Path $Publish "tools"' in build_script
assert '"/p:EnableCompressionInSingleFile=true"' in launcher_build_script
assert "components/route-guard/WinDivert64.payload.sys" in package_script
assert "'SCBL.Updater.exe'" in package_script
assert "legacy flat tools directory" in package_script
assert 'Join-Path $PublishRoot "SCBL.Updater.exe"' in component_assembly
assert 'Write-Component "updater"' in component_assembly
assert 'SpecialFolder.CommonApplicationData' in updater_processes
assert '"RouteGuard"' in updater_processes
for easytier_dependency in ("Packet.dll", "wintun.dll"):
    assert easytier_dependency in component_assembly
    assert easytier_dependency in component_bootstrap
    assert easytier_dependency in easytier_download
    assert easytier_dependency in package_script
assert 'Path.GetFileName(file).Equals("SCBL.Updater.exe"' not in updater_sources
assert 'Path.Combine(updatesDirectory, "runner", Guid.NewGuid().ToString("N"))' in local
assert "ScheduleDeferredRunnerCleanup" in app

print("SCBL client update restart and version-check retry checks passed")
