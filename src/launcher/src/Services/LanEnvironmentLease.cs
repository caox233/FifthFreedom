namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Owns one launcher LAN role's resources. Only the dispatcher-bound runtime may
/// publish capability observations. Disposal closes exactly that generation and
/// retains ownership on failure, so the controller can retry it.
/// </summary>
public sealed class LanEnvironmentLease(bool configuredJoin, Func<Task> closeOwned, Func<bool>? runtimeAlive = null) : IConfiguredLanJoinLease
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _dispose = new(1, 1);
    private bool _ready;
    private bool _closing;
    private bool _disposed;
    private long? _latency;
    public bool IsConfigured { get { lock (_sync) return configuredJoin && !_closing && !_disposed; } }
    public bool IsReady { get { lock (_sync) return _ready && !_closing && !_disposed && (runtimeAlive?.Invoke() ?? true); } }
    public long? LatencyMilliseconds { get { lock (_sync) return _ready && !_closing && !_disposed && (runtimeAlive?.Invoke() ?? true) ? _latency : null; } }

    public void ReportCapabilities(bool ready, long? latencyMilliseconds = null)
    {
        lock (_sync)
        {
            if (_closing || _disposed) return;
            _ready = ready;
            _latency = ready && latencyMilliseconds is >= 0 ? latencyMilliseconds : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _closing = true; _ready = false; _latency = null; }
        await _dispose.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) if (_disposed) return;
            await closeOwned().ConfigureAwait(false);
            lock (_sync) _disposed = true;
        }
        finally { _dispose.Release(); }
    }
}
