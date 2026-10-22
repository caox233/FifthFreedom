using System;
using System.Text.Json.Serialization;

namespace SplinterCellCNLauncher.Models;

public sealed class LauncherSettings
{
    public string Username { get; set; } = "";
    [JsonIgnore]
    public string Password { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    [JsonIgnore]
    public string RecoveryCode { get; set; } = "";
    public string RecoveryCodeProtected { get; set; } = "";
    public string RecoveryCodeUsername { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string GameExecutable { get; set; } = "Blacklist_DX11_game.exe";
    public string GameplayMode { get; set; } = GameplayModeNames.Official;
    public double ModFovScale { get; set; } = 1.2;
    public bool FovPromptHandled { get; set; }

    // User-configurable in the Settings menu. Accepts a domain or IP with a tunnel port.
    public string PublicEndpoint { get; set; } = "";
    public string TestPublicEndpoint { get; set; } = "";
    public string TunnelSecret { get; set; } = "";
    public string TunnelSecretProtected { get; set; } = "";

    // Persisted so DHCP and peer identity remain stable across restarts.
    public string EasyTierInstanceId { get; set; } = "";
    public string EasyTierNetworkName { get; set; } = "scbl-public";
    public string NetworkMode { get; set; } = ClientNetworkModeNames.Fast;
    // Hidden maintenance setting. Must match SCBL_WSS_PORT on the public server.
    public int EasyTierWssPort { get; set; } = 11010;
    // Hidden maintenance setting. WSS/TCP and QUIC/UDP share one server port.
    public int EasyTierQuicPort { get; set; } = 11010;

    public bool ForceGameVirtualAdapter { get; set; } = true;

    public bool SaveOverwritePromptHandled { get; set; }
    public string Language { get; set; } = "zh-CN";
    public bool MusicEnabled { get; set; } = true;
    public bool GuideCompleted { get; set; }
    public string ClientUpdateChannelPreference { get; set; } = "stable";

    // 启动器首先通过公网更新端口确认正式版本，版本不一致时必须更新。
    public int PublicUpdatePort { get; set; } = 18080;
    public int TestPublicUpdatePort { get; set; } = 18081;

    // Runtime network state cache. Startup still verifies the real adapter and route.
    public string LastAssignedVirtualIp { get; set; } = "";
    public string LastServerVirtualIp { get; set; } = "10.66.0.1";
    public string LastTunnelConnectedAt { get; set; } = "";
    public long? LastLatencyMs { get; set; }

    // "No longer prompt" is recorded for the startup dialog announcement.
    public string DismissedStartupAnnouncementId { get; set; } = "";

}
