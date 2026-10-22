using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class NetworkModeCoordinatorTests
{
    [Fact]
    public async Task GameActiveRejectsManualModeChangeWithoutRebuildOrPersistence()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        int saves = 0;
        int rebuilds = 0;
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => saves++,
            () => true,
            _ =>
            {
                rebuilds++;
                return Task.FromResult(NetworkReadyResult.Success("10.66.0.2", 1));
            });

        NetworkModeChangeResult result = await coordinator.ChangeAsync(ClientNetworkMode.Compatible);

        Assert.Equal(NetworkModeChangeOutcome.RejectedGameActive, result.Outcome);
        Assert.Equal(ClientNetworkMode.Fast, mode);
        Assert.Equal(0, saves);
        Assert.Equal(0, rebuilds);
        Assert.True(coordinator.State.IsGameActive);
        Assert.False(coordinator.State.CanChange);
    }

    [Fact]
    public async Task SuccessfulChangeStagesRebuildThenPersists()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        var calls = new List<string>();
        var coordinator = Create(
            () => mode,
            value =>
            {
                mode = value;
                calls.Add("stage:" + value);
            },
            () => calls.Add("save:" + mode),
            () => false,
            _ =>
            {
                calls.Add("rebuild:" + mode);
                return Task.FromResult(NetworkReadyResult.Success("10.66.0.2", 1));
            });

        NetworkModeChangeResult result = await coordinator.ChangeAsync(ClientNetworkMode.Compatible);

        Assert.Equal(NetworkModeChangeOutcome.Applied, result.Outcome);
        Assert.Equal(ClientNetworkMode.Compatible, result.Mode);
        Assert.Equal(
            new[] { "stage:Compatible", "rebuild:Compatible", "save:Compatible" },
            calls);
    }

    [Fact]
    public async Task ConnectionFailureRestoresPreviousModeWithoutProtocolFallback()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        int saves = 0;
        int rebuilds = 0;
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => saves++,
            () => false,
            _ =>
            {
                rebuilds++;
                return Task.FromResult(NetworkReadyResult.Failed(
                    NetworkPhase.ServerFailed,
                    NetworkFailureStage.Server,
                    "unavailable"));
            });

        NetworkModeChangeResult result = await coordinator.ChangeAsync(ClientNetworkMode.Compatible);

        Assert.Equal(NetworkModeChangeOutcome.ConnectionFailed, result.Outcome);
        Assert.Equal(ClientNetworkMode.Fast, result.Mode);
        Assert.Equal(ClientNetworkMode.Fast, mode);
        Assert.Equal(0, saves);
        Assert.Equal(2, rebuilds);
    }

    [Fact]
    public async Task FailedTargetRestartsPreviousModeAndKeepsItsPersistedSelection()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        var calls = new List<string>();
        var coordinator = Create(
            () => mode,
            value =>
            {
                mode = value;
                calls.Add("stage:" + value);
            },
            () => calls.Add("save:" + mode),
            () => false,
            _ =>
            {
                calls.Add("rebuild:" + mode);
                return Task.FromResult(mode == ClientNetworkMode.Fast
                    ? NetworkReadyResult.Success("10.66.0.2", 1)
                    : NetworkReadyResult.Failed(
                        NetworkPhase.TunnelFailed,
                        NetworkFailureStage.Tunnel,
                        "target failed"));
            });

        NetworkModeChangeResult result = await coordinator.ChangeAsync(ClientNetworkMode.Compatible);

        Assert.Equal(NetworkModeChangeOutcome.ConnectionFailed, result.Outcome);
        Assert.Equal(ClientNetworkMode.Fast, result.Mode);
        Assert.Equal(
            new[]
            {
                "stage:Compatible",
                "rebuild:Compatible",
                "stage:Fast",
                "rebuild:Fast"
            },
            calls);
    }

    [Fact]
    public async Task GameBecomingActiveDuringFailedRebuildStillRestoresPreviousRuntime()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        bool gameActive = false;
        int saves = 0;
        int rebuilds = 0;
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => saves++,
            () => gameActive,
            _ =>
            {
                rebuilds++;
                gameActive = true;
                return Task.FromResult(NetworkReadyResult.Failed(
                    NetworkPhase.ServerFailed,
                    NetworkFailureStage.Server,
                    "game active"));
            });

        NetworkModeChangeResult result = await coordinator.ChangeAsync(ClientNetworkMode.Compatible);

        Assert.Equal(NetworkModeChangeOutcome.ConnectionFailed, result.Outcome);
        Assert.Equal(ClientNetworkMode.Fast, mode);
        Assert.Equal(0, saves);
        Assert.Equal(2, rebuilds);
    }

    [Fact]
    public async Task TargetRebuildExceptionRestoresPreviousRuntimeAndPropagatesOriginal()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        int saves = 0;
        var calls = new List<string>();
        var original = new InvalidOperationException("target failed");
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => saves++,
            () => false,
            token =>
            {
                calls.Add("rebuild:" + mode);
                if (mode == ClientNetworkMode.Compatible)
                {
                    throw original;
                }

                Assert.False(token.CanBeCanceled);
                return Task.FromResult(NetworkReadyResult.Success("10.66.0.2", 1));
            });

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ChangeAsync(ClientNetworkMode.Compatible));

        Assert.Same(original, thrown);
        Assert.Equal(ClientNetworkMode.Fast, mode);
        Assert.Equal(0, saves);
        Assert.Equal(new[] { "rebuild:Compatible", "rebuild:Fast" }, calls);
    }

    [Fact]
    public async Task RollbackFailureDoesNotReplaceTargetRebuildException()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        int saves = 0;
        var calls = new List<string>();
        var original = new InvalidOperationException("target failed");
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => saves++,
            () => false,
            _ =>
            {
                calls.Add("rebuild:" + mode);
                if (mode == ClientNetworkMode.Compatible)
                {
                    throw original;
                }

                throw new ApplicationException("rollback failed");
            });

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ChangeAsync(ClientNetworkMode.Compatible));

        Assert.Same(original, thrown);
        Assert.Equal(ClientNetworkMode.Fast, mode);
        Assert.Equal(0, saves);
        Assert.Equal(new[] { "rebuild:Compatible", "rebuild:Fast" }, calls);
    }

    [Fact]
    public async Task CancellationStillRestoresPreviousRuntimeWithNonCancelableToken()
    {
        ClientNetworkMode mode = ClientNetworkMode.Fast;
        int saves = 0;
        using var cancellation = new CancellationTokenSource();
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => saves++,
            () => false,
            token =>
            {
                if (mode == ClientNetworkMode.Compatible)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }

                Assert.False(token.CanBeCanceled);
                return Task.FromResult(NetworkReadyResult.Success("10.66.0.2", 1));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ChangeAsync(ClientNetworkMode.Compatible, cancellation.Token));

        Assert.Equal(ClientNetworkMode.Fast, mode);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task InvalidEnumValueNormalizesToFast()
    {
        ClientNetworkMode mode = ClientNetworkMode.Compatible;
        var coordinator = Create(
            () => mode,
            value => mode = value,
            () => { },
            () => false,
            _ => Task.FromResult(NetworkReadyResult.Success("10.66.0.2", 1)));

        NetworkModeChangeResult result = await coordinator.ChangeAsync((ClientNetworkMode)99);

        Assert.Equal(NetworkModeChangeOutcome.Applied, result.Outcome);
        Assert.Equal(ClientNetworkMode.Fast, mode);
    }

    private static NetworkModeCoordinator Create(
        Func<ClientNetworkMode> read,
        Action<ClientNetworkMode> stage,
        Action save,
        Func<bool> gameActive,
        Func<CancellationToken, Task<NetworkReadyResult>> rebuild)
        => new(read, stage, save, gameActive, rebuild);
}
