using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class NetworkSessionCoordinatorTests
{
    [Fact]
    public async Task SuccessfulCheckPublishesReadyState()
    {
        var orchestrator = new FakeNetworkOrchestrator
        {
            Result = NetworkReadyResult.Success("10.144.0.8", 12)
        };
        var coordinator = new NetworkSessionCoordinator(orchestrator, () => false);

        NetworkReadyResult result = await coordinator.EnsureReadyAsync(
            NetworkEnsureMode.BeforeLaunch,
            "test");

        Assert.True(result.Ok);
        Assert.True(coordinator.State.IsReady);
        Assert.Equal("10.144.0.8", coordinator.State.AssignedIp);
        Assert.Equal(12, coordinator.State.LatencyMs);
    }

    [Fact]
    public async Task StartupIsSkippedWhenLauncherIsClosing()
    {
        var orchestrator = new FakeNetworkOrchestrator();
        var coordinator = new NetworkSessionCoordinator(orchestrator, () => true);

        await coordinator.StartAsync("closing");

        Assert.Equal(0, orchestrator.EnsureCalls);
    }

    private sealed class FakeNetworkOrchestrator : INetworkOrchestrator
    {
        public NetworkReadyResult Result { get; set; } =
            NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "failed");
        public int EnsureCalls { get; private set; }
        public event Action<NetworkStatusSnapshot>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<NetworkReadyResult> EnsureReadyAsync(
            NetworkEnsureMode mode,
            string reason,
            CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            return Task.FromResult(Result);
        }

        public void StartWatchdog() { }
        public Task ShutdownAsync(string reason, bool preserveTunnelRuntime = false) => Task.CompletedTask;
        public void Dispose() { }
    }
}
