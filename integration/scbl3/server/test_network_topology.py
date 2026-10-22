#!/usr/bin/env python3
from pathlib import Path

settings = Path("client/ScblPublicLauncher/Models/LauncherSettings.cs").read_text(encoding="utf-8")
settings_service = Path("client/ScblPublicLauncher/Services/LauncherSettingsService.cs").read_text(encoding="utf-8")
tunnel = Path("client/ScblPublicLauncher/Services/PublicTunnelService.cs").read_text(encoding="utf-8")
tunnel_store = Path("client/ScblPublicLauncher/Services/EasyTierConfigStore.cs").read_text(encoding="utf-8")
tunnel_config = Path("client/ScblPublicLauncher/Services/PublicTunnelConfig.cs").read_text(encoding="utf-8")
window = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
services = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(Path("client/ScblPublicLauncher/Services").rglob("*.cs"))
)
server = Path("server/manager/scblctl/templates.py").read_text(encoding="utf-8")
control = Path("server/scbl_control_plane.py").read_text(encoding="utf-8")
update = Path("server/scbl_update_server.py").read_text(encoding="utf-8")

assert "EasyTierLatencyFirst" not in settings
assert "EasyTierEnableP2P" not in settings
assert "NetworkMode { get; set; } = ClientNetworkModeNames.Fast" in settings
assert "settings.EasyTierWssPort == 10443" not in settings_service
assert "internal const int RuntimeProfileRevision = 16;" in tunnel_store
assert "_profileStore.Matches(" in tunnel
assert "_profileStore.Write(" in tunnel
assert "_profileStore.ValidateDynamicDhcpConfig(" in tunnel
assert "bind_device = true" in tunnel_store
assert 'string protocol = options.NetworkMode == ClientNetworkMode.Compatible ? "wss" : "quic";' in tunnel_store
assert '"[\\"udp://0.0.0.0:0\\", \\"udp://[::]:0\\"]"' in tunnel_store
assert '"[\\"udp://0.0.0.0:0\\"]"' in tunnel_store
assert "tcp://0.0.0.0:0" not in tunnel_store
assert "disable_tcp_hole_punching = true" in tunnel_store
assert '"server-path-isolated-lazy-player-p2p-ipv6-first-ipv4-fallback"' in tunnel_store
assert "lazy_p2p = true" in tunnel_store
assert "need_p2p = false" in tunnel_store
assert "disable_relay_data = true" in tunnel_store
assert "public const int DefaultWssPort = DefaultTunnelPort;" in tunnel_config
assert 'f"wss://0.0.0.0:{config.network.public_port}"' in server
assert 'f"quic://0.0.0.0:{config.network.public_port}"' in server
assert 'f"udp://0.0.0.0:{config.network.public_port}"' not in server
assert "disable_p2p = true" in server
assert "need_p2p = false" in server
assert "disable_relay_data = false" in server
assert "disable_udp_hole_punching = true" in server
assert "disable_tcp_hole_punching = true" in server
assert "disable_sym_hole_punching = true" in server
assert "scbl_update_server.py" in server
assert "SESSION_REFRESH_SECONDS = 2.0" in control
assert "request_queue_size = 128" in control
assert "ScblThreadingHTTPServer" in control
assert "ScblUpdateServerV6" in update
assert "IPV6_V6ONLY" in update
assert "request_queue_size = 128" in update
assert "本机为房主，显示服务器延迟。" in window
assert "当前显示与房主的延迟" in window
assert "正在识别房主并检测延迟。" in window
assert "TryOpenTcpConnectionAsync" in services
assert "PublicTunnelConfig.ServerVirtualIp" in services and "50051" in services
assert 'return $"{family}/{mode}";' in window
assert "到房主{hostLabel}" not in window
print("SCBL QUIC/WSS modes, UDP P2P, host latency and dual-stack topology checks passed")
