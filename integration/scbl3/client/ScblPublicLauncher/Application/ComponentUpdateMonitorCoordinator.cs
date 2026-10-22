using SplinterCellCNLauncher.Services;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.ApplicationLayer;

/// <summary>
/// Periodically checks the component manifest without owning any UI. Checks
/// are serialized and notifications are deferred while a game session or
/// launch workflow is active.
/// </summary>
public sealed class ComponentUpdateMonitorCoordinator : IDisposable
{
    private readonly IClientComponentUpdateReader _service;
    private readonly Func<bool> _isClosing;
    private readonly Func<bool> _shouldDefer;
    private readonly Func<bool> _isUiBusy;
    private readonly Func<CancellationToken, Task> _onUpdatesDetected;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _monitorCts;
    private int _checkRunning;
    private int _deferred;

    public ComponentUpdateMonitorCoordinator(
        IClientComponentUpdateReader service,
        Func<bool> isClosing,
        Func<bool> shouldDefer,
        Func<bool> isUiBusy,
        Func<CancellationToken, Task> onUpdatesDetected,
        TimeSpan? pollInterval = null)
    {
        _service = service;
        _isClosing = isClosing;
        _shouldDefer = shouldDefer;
        _isUiBusy = isUiBusy;
        _onUpdatesDetected = onUpdatesDetected;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(60);
    }

    public bool HasDeferredCheck => Volatile.Read(ref _deferred) != 0;

    public void Start()
    {
        Stop();
        _monitorCts = new CancellationTokenSource();
        _ = MonitorAsync(_monitorCts.Token);
    }

    public void Stop()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _checkRunning, 1) != 0)
            return;

        try
        {
            if (_isClosing() || _isUiBusy())
                return;
            if (_shouldDefer())
            {
                Interlocked.Exchange(ref _deferred, 1);
                return;
            }

            IReadOnlyList<PendingComponentUpdate> pending = await _service
                .GetPendingComponentUpdatesAsync(cancellationToken);
            if (pending.Count == 0)
            {
                Interlocked.Exchange(ref _deferred, 0);
                return;
            }
            if (_shouldDefer())
            {
                Interlocked.Exchange(ref _deferred, 1);
                LogService.Info("Component update detected while game is active; notification deferred until game exit.");
                return;
            }

            LogService.Info(
                $"Background component monitor detected {pending.Count} pending update(s); preparing the authoritative prompt.");
            await _onUpdatesDetected(cancellationToken);
            Interlocked.Exchange(ref _deferred, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Warning("Background component update check failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _checkRunning, 0);
        }
    }

    public void NotifyDeferredWorkCanResume()
    {
        if (!HasDeferredCheck || _isClosing())
            return;

        CancellationToken token = _monitorCts?.Token ?? CancellationToken.None;
        _ = ResumeAfterGameExitAsync(token);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_pollInterval, cancellationToken);
            while (!cancellationToken.IsCancellationRequested && !_isClosing())
            {
                await CheckAsync(cancellationToken);
                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ResumeAfterGameExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1500, cancellationToken);
            await CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose() => Stop();
}
