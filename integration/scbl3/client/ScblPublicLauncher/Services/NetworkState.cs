using System;

namespace SplinterCellCNLauncher.Services;

public interface INetworkOrchestrator : IDisposable
{
    event Action<NetworkStatusSnapshot>? StatusChanged;

    Task<NetworkReadyResult> EnsureReadyAsync(
        NetworkEnsureMode mode,
        string reason,
        CancellationToken cancellationToken = default);

    void StartWatchdog();

    Task ShutdownAsync(string reason, bool preserveTunnelRuntime = false);
}

public enum NetworkPhase
{
    Unknown,
    Preparing,
    TunnelConnecting,
    ServerConnecting,
    Connected,
    Reconnecting,
    NetworkFailed,
    TunnelFailed,
    ServerFailed
}

public enum NetworkFailureStage
{
    None,
    Network,
    Tunnel,
    Server
}

public enum NetworkEnsureMode
{
    SilentStartup,
    Automatic,
    Manual,
    BeforeLaunch,
    Repair
}

public sealed record NetworkStatusSnapshot(
    NetworkPhase Phase,
    long? LatencyMs = null,
    string Message = "",
    string TransportMode = "",
    string UnderlayAddressFamily = "",
    long RuntimeObservationId = 0,
    bool UserVisible = true)
{
    internal static string NormalizeTransportMode(
        NetworkPhase phase,
        string? confirmedTransportMode)
        => phase == NetworkPhase.Connected
            ? confirmedTransportMode?.Trim() ?? ""
            : "";

    internal static string NormalizeUnderlayAddressFamily(
        NetworkPhase phase,
        string? confirmedAddressFamily)
    {
        if (phase != NetworkPhase.Connected)
            return "";

        string family = confirmedAddressFamily?.Trim() ?? "";
        return family.Equals("IPv4", StringComparison.OrdinalIgnoreCase)
            ? "IPv4"
            : family.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
                ? "IPv6"
                : "";
    }

    internal static long NormalizeRuntimeObservationId(
        NetworkPhase phase,
        long runtimeObservationId)
        => phase == NetworkPhase.Connected && runtimeObservationId > 0
            ? runtimeObservationId
            : 0;

    internal static bool HasCompleteConnectionDetails(
        NetworkPhase phase,
        string? transportMode,
        string? underlayAddressFamily,
        long runtimeObservationId)
        => phase == NetworkPhase.Connected
            && !string.IsNullOrWhiteSpace(NormalizeTransportMode(phase, transportMode))
            && !string.IsNullOrWhiteSpace(NormalizeUnderlayAddressFamily(phase, underlayAddressFamily))
            && NormalizeRuntimeObservationId(phase, runtimeObservationId) > 0;
}

public sealed record NetworkReadyResult(
    bool Ok,
    NetworkPhase Phase,
    string AssignedIp = "",
    long? LatencyMs = null,
    string Message = "",
    string TransportMode = "",
    NetworkFailureStage FailureStage = NetworkFailureStage.None)
{
    public static NetworkReadyResult Success(string assignedIp, long? latencyMs, string message = "Server connected.", string transportMode = "")
        => new(true, NetworkPhase.Connected, assignedIp, latencyMs, message, transportMode, NetworkFailureStage.None);

    public static NetworkReadyResult Failed(NetworkPhase phase, NetworkFailureStage stage, string message)
        => new(false, phase, "", null, message, "", stage);
}
