using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;

namespace SCBL.Client.Tests;

public sealed class LauncherConfigurationAvailabilityServiceTests
{
    [Fact]
    public async Task Presentation_reads_only_the_last_completed_runtime_observation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LauncherConfigurationAvailabilityService(
            async cancellationToken =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new ModRuntimeObservation(true, false, "runtime-ready");
            });
        LauncherInteractionState interaction = new(
            LauncherPrimaryAction.Launch,
            IsLaunchEnabled: true,
            IsNetworkCheckEnabled: true,
            IsLaunchConfigurationEnabled: true);

        Task<ModRuntimeObservation> refresh = service.RefreshModRuntimeObservationAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(refresh.IsCompleted);

        LauncherConfigurationAvailability whileValidating = service.Get(
            interaction,
            GameplayModeNames.Mod,
            serverObservationId: "server-ready");
        Assert.True(whileValidating.IsModSelectable);
        Assert.False(whileValidating.IsModAvailable);
        Assert.Equal(GameplayMode.Mod, whileValidating.EffectiveGameplayMode);

        release.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        LauncherConfigurationAvailability completed = service.Get(
            interaction,
            GameplayModeNames.Mod,
            serverObservationId: "server-ready");
        Assert.True(completed.IsModSelectable);
        Assert.Equal(GameplayMode.Mod, completed.EffectiveGameplayMode);
        Assert.Equal("runtime-ready", completed.RuntimeObservationId);
    }
}
