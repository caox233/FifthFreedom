using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher.ApplicationLayer;

public sealed record NetworkSessionState(
    bool IsReady,
    bool IsLifecycleRunning,
    string AssignedIp,
    long? LatencyMs,
    NetworkReadyResult? LastResult);

/// <summary>
/// Owns network lifecycle concurrency and readiness state. It deliberately has
/// no WPF dependency; consumers decide how state changes are rendered.
/// </summary>
public sealed class NetworkSessionCoordinator
{
    private readonly INetworkOrchestrator _orchestrator;
    private readonly Func<bool> _isClosing;
    private int _lifecycleRunning;
    private NetworkSessionState _state = new(false, false, "", null, null);

    public NetworkSessionCoordinator(INetworkOrchestrator orchestrator, Func<bool> isClosing)
    {
        _orchestrator = orchestrator;
        _isClosing = isClosing;
    }

    public event Action<NetworkSessionState>? StateChanged;

    public NetworkSessionState State => Volatile.Read(ref _state);

    public void StartWatchdog() => _orchestrator.StartWatchdog();

    public async Task StartAsync(string reason)
    {
        if (_isClosing() || Interlocked.CompareExchange(ref _lifecycleRunning, 1, 0) != 0)
            return;

        Publish(State with { IsLifecycleRunning = true });
        try
        {
            await Task.Delay(80);
            if (_isClosing())
                return;

            NetworkReadyResult result = await _orchestrator.EnsureReadyAsync(
                NetworkEnsureMode.SilentStartup,
                reason);
            PublishResult(result, lifecycleRunning: true);
        }
        catch (Exception ex)
        {
            LogService.Error("Network lifecycle failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _lifecycleRunning, 0);
            Publish(State with { IsLifecycleRunning = false });
        }
    }

    public async Task<NetworkReadyResult> EnsureReadyAsync(
        NetworkEnsureMode mode,
        string reason,
        bool runCoreOffCallingContext = false)
    {
        NetworkReadyResult result = runCoreOffCallingContext
            ? await Task.Run(() => _orchestrator.EnsureReadyAsync(mode, reason))
            : await _orchestrator.EnsureReadyAsync(mode, reason);
        PublishResult(result, State.IsLifecycleRunning);
        return result;
    }

    public Task ShutdownAsync(string reason, bool preserveTunnelRuntime)
        => _orchestrator.ShutdownAsync(reason, preserveTunnelRuntime);

    private void PublishResult(NetworkReadyResult result, bool lifecycleRunning)
    {
        Publish(new NetworkSessionState(
            result.Ok,
            lifecycleRunning,
            result.Ok ? result.AssignedIp : "",
            result.Ok ? result.LatencyMs : null,
            result));
    }

    private void Publish(NetworkSessionState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(state);
    }
}
