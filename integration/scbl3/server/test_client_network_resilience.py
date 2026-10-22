#!/usr/bin/env python3
import re
from pathlib import Path

version = Path("VERSION_CLIENT").read_text(encoding="utf-8").strip()
control = Path("client/ScblPublicLauncher/Services/ControlPlaneService.cs").read_text(encoding="utf-8")
window = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
router = Path("client/ScblPublicLauncher/Services/ProcessRouterService.cs").read_text(encoding="utf-8")
probe = Path("client/ScblPublicLauncher/Services/PeerProbeService.cs").read_text(encoding="utf-8")
game_monitor = Path("client/ScblPublicLauncher/Services/GameNetworkMonitorService.cs").read_text(encoding="utf-8")
diagnostic = Path("client/ScblPublicLauncher/Services/DiagnosticExportService.cs").read_text(encoding="utf-8")
maintenance = Path("client/ScblPublicLauncher/Services/ClientStorageMaintenanceService.cs").read_text(encoding="utf-8")
xaml = Path("client/ScblPublicLauncher/MainWindow.xaml").read_text(encoding="utf-8")

assert re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version)
assert "private const int MaxAttempts = 2;" in control
assert '"heartbeat"' in control and '"peers"' in control and '"game-session"' in control
assert "InvalidateClient(localBindIp, channel, client)" in control
assert "PooledConnectionLifetime = TimeSpan.FromSeconds(45)" in control
assert "request.Headers.ConnectionClose = true;" in control
assert 'x:Name="txtBottomNetwork" Text="检测网络"' in xaml
assert 'x:Name="btnCheckNetwork"' in xaml and 'Click="CheckNetworkButton_Click"' in xaml
assert 'x:Name="txtServerStatus"' in xaml and 'Text="正在连接服务器"' in xaml
assert "本机为房主，显示服务器延迟。" in window
assert "当前显示与房主的延迟" in window
assert "正在识别房主并检测延迟。" in window
assert '本机房主 · {_gameActivePeerCount}名玩家' not in window
assert "TryOpenTcpConnectionAsync" in game_monitor and "50051" in game_monitor
assert "PublicServerAddress" in window
assert 'return $"{family}/{mode}";' in window
assert "PeriodicTimer(TimeSpan.FromSeconds(5))" in window
assert ".Concat(routeIps)" in window
assert 'source={source}, registryOnline=' in window
tunnel_cli = Path("client/ScblPublicLauncher/Services/EasyTierCliClient.cs").read_text(encoding="utf-8")
assert "ReadIpv4Value" in tunnel_cli and "numeric.TryGetUInt32(out uint raw)" in tunnel_cli
assert 'ReadConnectionId(peer, "default_conn_id")' in tunnel_cli
assert "hasDefaultConnection" in tunnel_cli and "connectionId.Equals(" in tunnel_cli
assert "if (!hasDefaultConnection || selectedConnection is null)" in tunnel_cli
assert "latencyMicroseconds < bestLatencyMicroseconds" not in tunnel_cli
assert "Process router exited unexpectedly" in router
assert "subnetFallback={scanFallback}" in probe
assert "Peer refresh used server registry" not in window
assert "bool isServerPeer = peerIp.Equals(" in tunnel
assert "_getServerVirtualIp()," in tunnel
assert 'SetBusy(true, L("正在准备启动器...", "Preparing launcher..."));' in window
assert "Task.WhenAll(" in window and "Launcher bootstrap checks completed" in window
network = Path("client/ScblPublicLauncher/Services/NetworkOrchestrator.cs").read_text(encoding="utf-8")
assert "WaitForActiveGameRuntimeRecoveryAsync" in network
assert "TryBeginBoundedRuntimeRebuild" in network
assert "performing one controlled runtime rebuild" in network
assert "EnsureRouteBindingBestEffort(ip)" in network
assert network.count("_adapterService.EnsureRouteBindingBestEffort(ip);") >= 1
assert "Silent network reuse passed the read-only route check; route rebinding was skipped." in network
assert "ApplyNetworkStatusSnapshot" in window
assert "snapshot.UnderlayAddressFamily" in window
assert "snapshot.RuntimeObservationId" in window
assert "ServerStatusKind.ServerConnecting" in window
assert "CleanupLegacyRouteHistoryArtifacts" not in diagnostic
assert "LegacyGameRouteHistoryIncluded=False" not in diagnostic
assert 'candidates.Add((Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-route-history.jsonl")' not in diagnostic
assert "ClientStorageMaintenanceService.RunStartupCleanup();" in Path("client/ScblPublicLauncher/App.xaml.cs").read_text(encoding="utf-8")
assert "PruneComponentVersions" in maintenance and "RotateIfOversized" in maintenance
print("SCBL client control-plane, route-display, and diagnostic resilience checks passed")
