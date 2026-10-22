using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Collections.Concurrent;

namespace SCBL.Client.Tests;

public sealed class GameNetworkMonitorServiceTests
{
    [Theory]
    [InlineData(4, 4, false, false)]
    [InlineData(5, 1, false, false)]
    [InlineData(5, 2, true, false)]
    [InlineData(5, 2, false, true)]
    [InlineData(5, 3, false, true)]
    public void ServerPathLossRequiresFiveSamplesFortyPercentAndLatestFailure(
        int sampleCount,
        int failedSamples,
        bool latestSucceeded,
        bool expected)
    {
        Assert.Equal(
            expected,
            GameNetworkMonitorService.ShouldRequestServerPathValidation(
                sampleCount,
                failedSamples,
                latestSucceeded));
    }

    [Fact]
    public async Task StopRejectsPathResultThatCompletesAfterCancellation()
    {
        var pathStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pathResult = new TaskCompletionSource<EasyTierPeerPath?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new ConcurrentQueue<GameNetworkObservation>();
        var events = new ConcurrentQueue<GameNetworkObservation>();
        GameNetworkMonitorService service = CreateService(
            isGameSessionActive: () => true,
            getSessionAsync: (_, _, _) => Task.FromResult<ControlPlaneGameSession?>(HostSession()),
            detectPeerPathAsync: (_, _, _) =>
            {
                pathStarted.TrySetResult();
                return pathResult.Task;
            },
            writes.Enqueue);
        service.ObservationChanged += (_, observation) => events.Enqueue(observation);

        service.Start();
        Task loop = service.CurrentLoopTask ?? throw new InvalidOperationException("Monitor loop did not start.");
        await pathStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.Stop();
        Assert.False(service.Current.Active);
        int writesAfterStop = writes.Count;
        int eventsAfterStop = events.Count;

        pathResult.SetResult(new EasyTierPeerPath(
            PublicTunnelConfig.ServerVirtualIp,
            18,
            "quic",
            "1",
            UnderlayAddressFamily: "IPv6"));
        await loop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(writesAfterStop, writes.Count);
        Assert.Equal(eventsAfterStop, events.Count);
        Assert.False(service.Current.Active);
        service.Dispose();
    }

    [Fact]
    public async Task NaturalLoopCompletionClearsGenerationAndAllowsRestart()
    {
        bool active = false;
        var writes = new ConcurrentQueue<GameNetworkObservation>();
        GameNetworkMonitorService service = CreateService(
            () => active,
            (_, _, _) => Task.FromResult<ControlPlaneGameSession?>(null),
            (_, _, _) => Task.FromResult<EasyTierPeerPath?>(null),
            writes.Enqueue);

        service.Start();
        await WaitUntilAsync(
            () => service.CurrentLoopTask == null && !service.Current.Active,
            TimeSpan.FromSeconds(2));

        active = true;
        service.Start();
        Task restartedLoop = service.CurrentLoopTask
            ?? throw new InvalidOperationException("Monitor did not restart after natural completion.");
        Assert.True(service.Current.Active);

        service.Stop();
        await restartedLoop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.Current.Active);
        service.Dispose();
    }

    [Fact]
    public void RepeatedStopAndDisposeAreIdempotent()
    {
        GameNetworkMonitorService service = CreateService(
            () => false,
            (_, _, _) => Task.FromResult<ControlPlaneGameSession?>(null),
            (_, _, _) => Task.FromResult<EasyTierPeerPath?>(null),
            _ => { });

        service.Stop();
        service.Stop();
        service.Dispose();
        service.Dispose();

        Assert.False(service.Current.Active);
        Assert.Throws<ObjectDisposedException>(service.Start);
    }

    [Fact]
    public async Task StartCapturesWpfOwnedInputsOnceOnTheCallerThread()
    {
        int callerThreadId = Environment.CurrentManagedThreadId;
        var usernameThreads = new ConcurrentQueue<int>();
        var peerThreads = new ConcurrentQueue<int>();
        GameNetworkMonitorService service = CreateService(
            () => false,
            (_, _, _) => Task.FromResult<ControlPlaneGameSession?>(null),
            (_, _, _) => Task.FromResult<EasyTierPeerPath?>(null),
            _ => { },
            getUsername: () =>
            {
                usernameThreads.Enqueue(Environment.CurrentManagedThreadId);
                return "player";
            },
            getPeers: () =>
            {
                peerThreads.Enqueue(Environment.CurrentManagedThreadId);
                return new[]
                {
                    new PeerInfo
                    {
                        Username = "peer",
                        VirtualIp = "10.66.0.3",
                        LatencyMs = 12,
                        IsReachable = true,
                    },
                };
            });

        service.Start();
        await WaitUntilAsync(
            () => service.CurrentLoopTask == null,
            TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { callerThreadId }, usernameThreads);
        Assert.Equal(new[] { callerThreadId }, peerThreads);
        service.Dispose();
    }

    private static GameNetworkMonitorService CreateService(
        Func<bool> isGameSessionActive,
        Func<string, string, CancellationToken, Task<ControlPlaneGameSession?>> getSessionAsync,
        Func<string, string, TimeSpan, Task<EasyTierPeerPath?>> detectPeerPathAsync,
        Action<GameNetworkObservation> writeSnapshot,
        Func<string>? getUsername = null,
        Func<IReadOnlyList<PeerInfo>>? getPeers = null)
    {
        var dependencies = new GameNetworkMonitorDependencies(
            getSessionAsync,
            detectPeerPathAsync,
            (_, _, _) => { },
            (_, _, _) => Task.FromResult<(bool Ok, long? LatencyMs)>((false, null)),
            (_, _, _, _) => Task.FromResult<(bool Ok, long? LatencyMs)>((true, 12)),
            _ => { },
            writeSnapshot);
        return new GameNetworkMonitorService(
            dependencies,
            isGameSessionActive,
            () => "10.66.0.2",
            () => "secret",
            () => AppContext.BaseDirectory,
            getUsername ?? (() => "player"),
            getPeers ?? (() => Array.Empty<PeerInfo>()),
            () => 20,
            "test-version");
    }

    private static ControlPlaneGameSession HostSession()
        => new()
        {
            Active = true,
            Authoritative = true,
            SessionId = 77,
            HostUsername = "player",
            HostVirtualIp = "10.66.0.2",
            RequesterIsHost = true,
            ParticipantCount = 1
        };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not satisfied before the test timeout.");
            await Task.Delay(10);
        }
    }
}
