using SplinterCellCNLauncher;

namespace SCBL.Client.Tests;

public sealed class NetworkReadyRemoteServicesGateTests
{
    [Fact]
    public void StatusThenSessionReadyStartsExactlyOnce()
    {
        var gate = new NetworkReadyRemoteServicesGate();

        Assert.True(gate.TryStart(isReady: true, isClosing: false));
        Assert.False(gate.TryStart(isReady: true, isClosing: false));
    }

    [Fact]
    public void SessionReadyWithoutStatusSnapshotStillStarts()
    {
        var gate = new NetworkReadyRemoteServicesGate();

        Assert.False(gate.TryStart(isReady: false, isClosing: false));
        Assert.True(gate.TryStart(isReady: true, isClosing: false));
    }

    [Fact]
    public void DisconnectAndReconnectDoNotStartAnotherRefreshLoop()
    {
        var gate = new NetworkReadyRemoteServicesGate();

        Assert.True(gate.TryStart(isReady: true, isClosing: false));
        Assert.False(gate.TryStart(isReady: false, isClosing: false));
        Assert.False(gate.TryStart(isReady: true, isClosing: false));
    }

    [Fact]
    public void ClosingDoesNotConsumeTheReadyEdge()
    {
        var gate = new NetworkReadyRemoteServicesGate();

        Assert.False(gate.TryStart(isReady: true, isClosing: true));
        Assert.True(gate.TryStart(isReady: true, isClosing: false));
    }

    [Fact]
    public void ConcurrentReadySignalsHaveOneWinner()
    {
        var gate = new NetworkReadyRemoteServicesGate();
        int starts = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.TryStart(isReady: true, isClosing: false))
                Interlocked.Increment(ref starts);
        });

        Assert.Equal(1, starts);
    }
}
