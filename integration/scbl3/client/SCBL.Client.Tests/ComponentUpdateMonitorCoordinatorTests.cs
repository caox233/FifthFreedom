using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class ComponentUpdateMonitorCoordinatorTests
{
    [Fact]
    public async Task ActiveGameDefersManifestRead()
    {
        var reader = new FakeReader("hooks");
        bool gameActive = true;
        int notifications = 0;
        using var coordinator = new ComponentUpdateMonitorCoordinator(
            reader,
            () => false,
            () => gameActive,
            () => false,
            _ =>
            {
                notifications++;
                return Task.CompletedTask;
            });

        await coordinator.CheckAsync();

        Assert.True(coordinator.HasDeferredCheck);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task PendingComponentsAreDeliveredOnce()
    {
        var reader = new FakeReader("launcher", "hooks");
        int notifications = 0;
        using var coordinator = new ComponentUpdateMonitorCoordinator(
            reader,
            () => false,
            () => false,
            () => false,
            _ =>
            {
                notifications++;
                return Task.CompletedTask;
            });

        await coordinator.CheckAsync();

        Assert.Equal(1, reader.Reads);
        Assert.Equal(1, notifications);
        Assert.False(coordinator.HasDeferredCheck);
    }

    private sealed class FakeReader(params string[] pending) : IClientComponentUpdateReader
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<PendingComponentUpdate>> GetPendingComponentUpdatesAsync(
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<PendingComponentUpdate>>(
                pending.Select(name => new PendingComponentUpdate(
                    name,
                    "2.0.0",
                    [name == "launcher" ? "启动器说明" : "组件说明"],
                    ["Component note"])).ToArray());
        }
    }
}
