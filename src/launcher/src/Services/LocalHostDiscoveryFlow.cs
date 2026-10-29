using System.Net.Sockets;

namespace SplinterCellCNLauncher.Services;

public sealed record LocalHostDiscoveryResult(long Generation, string BindIp, LocalHostDiscoveryStatus Status,
    IReadOnlyList<LocalDiscoveredHost> Hosts);

/// <summary>Owns one UI discovery generation. It never selects, joins or starts a host/game.</summary>
public sealed class LocalHostDiscoveryFlow : IDisposable
{
    private readonly object gate = new();
    private readonly ILocalHostDiscoveryTransport transport;
    private readonly TimeSpan observationWindow, operationTimeout;
    private CancellationTokenSource? active;
    private CancellationToken callerCancellation;
    private long generation;
    private bool disposed;

    public LocalHostDiscoveryFlow(ILocalHostDiscoveryTransport? transport = null,
        TimeSpan? observationWindow = null, TimeSpan? operationTimeout = null)
    {
        this.transport = transport ?? new UdpLocalHostDiscoveryTransport();
        this.observationWindow = observationWindow ?? TimeSpan.FromSeconds(2);
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(4);
        if (this.observationWindow <= TimeSpan.Zero || this.observationWindow > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(observationWindow));
        if (this.operationTimeout <= this.observationWindow || this.operationTimeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public async Task<LocalHostDiscoveryResult> SearchAsync(string bindIp, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? predecessor;
        CancellationTokenSource owner;
        long current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            predecessor = active;
            active = owner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callerCancellation = cancellationToken;
            current = ++generation;
        }
        CancelOwner(predecessor);
        LocalHostDiscoveryScan scan;
        Task<LocalHostDiscoveryScan>? operation = null;
        try
        {
            owner.Token.ThrowIfCancellationRequested();
            if (!LocalHostDiscoveryProtocol.TryAddress(bindIp, out var address)) scan = new(LocalHostDiscoveryStatus.InvalidEndpoint, []);
            else
            {
                bindIp = address.ToString();
                operation = transport.SearchAsync(bindIp, observationWindow, owner.Token);
                scan = await operation.WaitAsync(operationTimeout, owner.Token).ConfigureAwait(false);
            }
        }
        catch (TimeoutException) { scan = new(LocalHostDiscoveryStatus.TimedOut, []); }
        catch (OperationCanceledException) { scan = new(owner.IsCancellationRequested ? LocalHostDiscoveryStatus.Cancelled : LocalHostDiscoveryStatus.TimedOut, []); }
        catch (SocketException error) { scan = new(error.SocketErrorCode == SocketError.TimedOut ? LocalHostDiscoveryStatus.TimedOut : LocalHostDiscoveryStatus.NetworkError, []); }
        catch (IOException) { scan = new(LocalHostDiscoveryStatus.NetworkError, []); }
        finally
        {
            // The real transport registers this cancellation to dispose its
            // owned socket. A late test/custom transport cannot publish results.
            CancelOwner(owner);
            if (operation != null) _ = ObserveCompletion(operation);
            lock (gate)
            {
                if (ReferenceEquals(active, owner)) active = null;
                owner.Dispose();
            }
        }
        lock (gate)
        {
            bool stale = disposed || generation != current;
            if (stale) return new(current, bindIp, LocalHostDiscoveryStatus.Superseded, []);
            if (cancellationToken.IsCancellationRequested) return new(current, bindIp, LocalHostDiscoveryStatus.Cancelled, []);
            return new(current, bindIp, scan.Status, scan.Status == LocalHostDiscoveryStatus.Completed ? scan.Hosts.ToArray() : []);
        }
    }

    public bool IsCurrent(LocalHostDiscoveryResult result)
    {
        lock (gate) return !disposed && !callerCancellation.IsCancellationRequested && result.Generation == generation;
    }

    public void Cancel()
    {
        CancellationTokenSource? owner;
        lock (gate) { ++generation; owner = active; active = null; }
        CancelOwner(owner);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        Cancel();
    }

    private static void CancelOwner(CancellationTokenSource? owner)
    {
        try { owner?.Cancel(); }
        catch (ObjectDisposedException) { } // The completing predecessor already released it.
    }

    private static async Task ObserveCompletion(Task<LocalHostDiscoveryScan> operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { } // Its caller already received cancellation/failure; never reapply a late result.
    }
}
