namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Serializes explicit deployment changes. Previous owned resources are stopped
/// before another environment starts. Failure reconnects the previous selection;
/// cancellation/close cannot publish a late candidate as ready. No persistence,
/// networking, credentials or process-global shutdown lives in this controller.
/// </summary>
public sealed class NetworkEnvironmentController : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<NetworkEnvironmentKind, INetworkEnvironmentAdapter> _adapters;
    private readonly Func<NetworkEnvironmentActivity> _readActivity;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly List<INetworkEnvironmentLease> _pendingCleanup = new();
    private INetworkEnvironmentLease? _owned;
    private bool _closed;
    private NetworkEnvironmentSnapshot _state = new(null, false, false, false, NetworkEnvironmentStatus.Idle, null, false, false);

    public NetworkEnvironmentController(
        INetworkEnvironmentAdapter lan,
        Func<NetworkEnvironmentActivity> readActivity,
        INetworkEnvironmentAdapter? publicAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(lan);
        ArgumentNullException.ThrowIfNull(readActivity);
        if (lan.Environment != NetworkEnvironmentKind.Lan)
            throw new ArgumentException("A LAN adapter is required.", nameof(lan));
        publicAdapter ??= new UnavailablePublicNetworkEnvironmentAdapter();
        if (publicAdapter.Environment != NetworkEnvironmentKind.Public)
            throw new ArgumentException("A Public adapter is required.", nameof(publicAdapter));
        _adapters = new Dictionary<NetworkEnvironmentKind, INetworkEnvironmentAdapter>
        {
            [NetworkEnvironmentKind.Lan] = lan,
            [NetworkEnvironmentKind.Public] = publicAdapter
        };
        _readActivity = readActivity;
    }

    public NetworkEnvironmentSnapshot Snapshot
    {
        get
        {
            NetworkEnvironmentActivity activity = _readActivity();
            lock (_sync)
            {
                bool ready = !_state.IsClosed && !_state.IsSwitching && _owned?.IsReady == true;
                bool disconnected = _state.IsReady && !ready && !_state.IsClosed && !_state.IsSwitching;
                return _state with
                {
                    IsReady = ready,
                    Status = disconnected ? NetworkEnvironmentStatus.Disconnected : _state.Status,
                    LatencyMilliseconds = ready ? SafeLatency(_owned!) : null,
                    GameActive = activity.GameActive,
                    OwnedLaunch = activity.OwnedLaunch
                };
            }
        }
    }

    private NetworkEnvironmentChangeResult Result(NetworkEnvironmentChangeOutcome outcome,
        NetworkEnvironmentSelection requested, NetworkEnvironmentRollback rollback = NetworkEnvironmentRollback.NotNeeded)
        => new(outcome, rollback, requested, Snapshot);

    public async Task<NetworkEnvironmentChangeResult> ChangeAsync(NetworkEnvironmentSelection requested,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await _changes.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return Result(IsClosed ? NetworkEnvironmentChangeOutcome.Closed : NetworkEnvironmentChangeOutcome.Cancelled, requested);
        }
        try
        {
            if (IsClosed) return Result(NetworkEnvironmentChangeOutcome.Closed, requested);
            if (linked.IsCancellationRequested) return Result(NetworkEnvironmentChangeOutcome.Cancelled, requested);
            NetworkEnvironmentActivity activity = _readActivity();
            if (activity.GameActive) return Result(NetworkEnvironmentChangeOutcome.RejectedGameActive, requested);
            if (activity.OwnedLaunch) return Result(NetworkEnvironmentChangeOutcome.RejectedOwnedLaunch, requested);
            INetworkEnvironmentAdapter adapter = _adapters[requested.Environment];
            if (!adapter.IsAvailable)
            {
                SetStatus(NetworkEnvironmentStatus.Unavailable);
                return Result(NetworkEnvironmentChangeOutcome.Unavailable, requested);
            }
            if (_owned is not null && _state.Selection == requested && _state.IsReady && _owned.IsReady)
                return Result(NetworkEnvironmentChangeOutcome.Unchanged, requested);
            // An incompletely stopped lease must not be overlapped by a new host.
            if (_pendingCleanup.Count != 0)
            {
                SetStatus(NetworkEnvironmentStatus.CleanupFailed);
                return Result(NetworkEnvironmentChangeOutcome.ConnectionFailed, requested, NetworkEnvironmentRollback.Failed);
            }

            NetworkEnvironmentSelection? previous;
            INetworkEnvironmentLease? previousLease;
            lock (_sync)
            {
                if (_closed) return Result(NetworkEnvironmentChangeOutcome.Closed, requested);
                previous = _state.Selection;
                previousLease = _owned;
                _owned = null;
                _state = _state with { IsSwitching = true, IsReady = false, LatencyMilliseconds = null, Status = NetworkEnvironmentStatus.Switching };
            }
            if (previousLease is not null && !await ReleaseAsync(previousLease).ConfigureAwait(false))
            {
                Finish(NetworkEnvironmentStatus.CleanupFailed, previous, null);
                return Result(IsClosed ? NetworkEnvironmentChangeOutcome.Closed : NetworkEnvironmentChangeOutcome.ConnectionFailed,
                    requested, NetworkEnvironmentRollback.Failed);
            }

            INetworkEnvironmentLease? candidate = null;
            NetworkEnvironmentChangeOutcome failure = NetworkEnvironmentChangeOutcome.ConnectionFailed;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                candidate = await adapter.ConnectAsync(requested, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                activity = _readActivity();
                if (activity.GameActive) failure = NetworkEnvironmentChangeOutcome.RejectedGameActive;
                else if (activity.OwnedLaunch) failure = NetworkEnvironmentChangeOutcome.RejectedOwnedLaunch;
                else if (candidate is not null && candidate.IsReady)
                {
                    lock (_sync)
                    {
                        if (!_closed && !linked.IsCancellationRequested)
                        {
                            _owned = candidate;
                            _state = _state with { Selection = requested, IsReady = true, IsSwitching = false,
                                Status = NetworkEnvironmentStatus.Ready, LatencyMilliseconds = SafeLatency(candidate) };
                            candidate = null;
                            return Result(NetworkEnvironmentChangeOutcome.Applied, requested);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { failure = NetworkEnvironmentChangeOutcome.Cancelled; }
            catch (Exception) { /* Return a bounded status, never raw errors containing secrets. */ }

            bool candidateReleased = candidate is null || await ReleaseAsync(candidate).ConfigureAwait(false);
            if (IsClosed) return Result(NetworkEnvironmentChangeOutcome.Closed, requested);
            if (linked.IsCancellationRequested) failure = NetworkEnvironmentChangeOutcome.Cancelled;
            if (!candidateReleased)
            {
                Finish(NetworkEnvironmentStatus.CleanupFailed, previous, null);
                return Result(failure, requested, NetworkEnvironmentRollback.Failed);
            }
            NetworkEnvironmentRollback rollback = NetworkEnvironmentRollback.NotNeeded;
            INetworkEnvironmentLease? restored = null;
            if (previousLease is not null && previous is not null)
            {
                rollback = NetworkEnvironmentRollback.Failed;
                try
                {
                    // Caller cancellation stops the requested change, not restoration.
                    // Closing the controller still cancels restoration through its lifetime.
                    restored = await _adapters[previous.Environment].ConnectAsync(previous, _lifetime.Token).ConfigureAwait(false);
                    if (restored is not null && restored.IsReady && !IsClosed)
                        rollback = NetworkEnvironmentRollback.Restored;
                }
                catch (Exception) { /* Rollback outcome remains explicit and not-ready. */ }
                if (rollback != NetworkEnvironmentRollback.Restored && restored is not null)
                {
                    await ReleaseAsync(restored).ConfigureAwait(false);
                    restored = null;
                }
            }
            // Close can race completion even after a successful rollback response.
            if (IsClosed)
            {
                if (restored is not null) await ReleaseAsync(restored).ConfigureAwait(false);
                return Result(NetworkEnvironmentChangeOutcome.Closed, requested, rollback);
            }
            NetworkEnvironmentStatus status = rollback == NetworkEnvironmentRollback.Failed
                ? NetworkEnvironmentStatus.RollbackFailed
                : failure == NetworkEnvironmentChangeOutcome.Cancelled ? NetworkEnvironmentStatus.Cancelled
                : NetworkEnvironmentStatus.ConnectionFailed;
            if (!Finish(status, previous, restored) && restored is not null)
                await ReleaseAsync(restored).ConfigureAwait(false);
            return Result(IsClosed ? NetworkEnvironmentChangeOutcome.Closed : failure, requested, rollback);
        }
        finally { _changes.Release(); }
    }

    private bool IsClosed { get { lock (_sync) return _closed; } }
    private static long? SafeLatency(INetworkEnvironmentLease lease)
    {
        long? latency = lease.LatencyMilliseconds;
        return latency is >= 0 ? latency : null;
    }

    private void SetStatus(NetworkEnvironmentStatus status)
    {
        lock (_sync) if (!_closed) _state = _state with { Status = status };
    }

    private bool Finish(NetworkEnvironmentStatus status, NetworkEnvironmentSelection? selection, INetworkEnvironmentLease? lease)
    {
        lock (_sync)
        {
            if (_closed) return false;
            _owned = lease;
            _state = _state with { Selection = selection, IsSwitching = false, IsReady = lease?.IsReady == true,
                Status = status, LatencyMilliseconds = lease is null ? null : SafeLatency(lease) };
            return true;
        }
    }

    private async Task<bool> ReleaseAsync(INetworkEnvironmentLease lease)
    {
        try { await lease.DisposeAsync().ConfigureAwait(false); return true; }
        catch (Exception) { _pendingCleanup.Add(lease); return false; }
    }

    public async ValueTask DisposeAsync()
    {
        bool firstClose;
        lock (_sync)
        {
            firstClose = !_closed;
            _closed = true;
            _state = _state with { IsClosed = true, IsSwitching = false, IsReady = false,
                Status = NetworkEnvironmentStatus.Closed, LatencyMilliseconds = null };
        }
        if (firstClose)
        {
            try { _lifetime.Cancel(); }
            catch (AggregateException) { /* Cancellation is set; still close owned resources. */ }
        }
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            INetworkEnvironmentLease? owned = _owned;
            _owned = null;
            // Retry only failed owned disposals; never enumerate or kill other processes.
            INetworkEnvironmentLease[] pending = _pendingCleanup.ToArray();
            _pendingCleanup.Clear();
            if (owned is not null) await ReleaseAsync(owned).ConfigureAwait(false);
            foreach (INetworkEnvironmentLease lease in pending) await ReleaseAsync(lease).ConfigureAwait(false);
            if (_pendingCleanup.Count != 0)
                throw new InvalidOperationException("Owned network resources could not be closed; retry disposal.");
        }
        finally { _changes.Release(); }
    }
}
