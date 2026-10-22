using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;

namespace SCBL.Client.Tests;

public sealed class LauncherInteractionPolicyTests
{
    [Fact]
    public void ConnectedIdleClientCanLaunchAndChangeConfiguration()
    {
        LauncherInteractionState state = Evaluate(isNetworkReady: true, hasValidClientIp: true);

        Assert.Equal(LauncherPrimaryAction.Launch, state.PrimaryAction);
        Assert.True(state.IsLaunchEnabled);
        Assert.True(state.IsNetworkCheckEnabled);
        Assert.True(state.IsLaunchConfigurationEnabled);
    }

    [Fact]
    public void StartingClientCannotLaunchUntilRestartBecomesAvailable()
    {
        LauncherInteractionState waiting = Evaluate(isGameStarting: true);
        LauncherInteractionState restartable = Evaluate(
            isGameStarting: true,
            isStartupRestartAvailable: true);

        Assert.Equal(LauncherPrimaryAction.Starting, waiting.PrimaryAction);
        Assert.False(waiting.IsLaunchEnabled);
        Assert.False(waiting.IsLaunchConfigurationEnabled);
        Assert.Equal(LauncherPrimaryAction.Restart, restartable.PrimaryAction);
        Assert.True(restartable.IsLaunchEnabled);
    }

    [Fact]
    public void RunningGameCanAlwaysBeEndedAfterNetworkLoss()
    {
        LauncherInteractionState state = Evaluate(
            isGameRunning: true,
            isNetworkReady: false,
            hasValidClientIp: false);

        Assert.Equal(LauncherPrimaryAction.EndGame, state.PrimaryAction);
        Assert.True(state.IsLaunchEnabled);
        Assert.False(state.IsLaunchConfigurationEnabled);
    }

    [Fact]
    public void UpdateDisablesPrimaryActionAndConfiguration()
    {
        LauncherInteractionState state = Evaluate(
            isUpdating: true,
            isNetworkReady: true,
            hasValidClientIp: true);

        Assert.Equal(LauncherPrimaryAction.Updating, state.PrimaryAction);
        Assert.False(state.IsLaunchEnabled);
        Assert.False(state.IsLaunchConfigurationEnabled);
    }

    [Fact]
    public void NetworkCheckDoesNotDependOnGameOrBusyState()
    {
        LauncherInteractionState available = Evaluate(isBusy: true, isGameRunning: true);
        LauncherInteractionState coolingDown = Evaluate(
            isBusy: true,
            isGameRunning: true,
            isNetworkCheckCoolingDown: true);

        Assert.True(available.IsNetworkCheckEnabled);
        Assert.False(coolingDown.IsNetworkCheckEnabled);
    }

    [Fact]
    public void Renderer_and_gameplay_configuration_are_disabled_during_start_and_runtime()
    {
        var runtime = new ModRuntimeObservation(true, false, "runtime-1");

        LauncherConfigurationAvailability starting = LauncherConfigurationAvailabilityPolicy.Evaluate(
            Evaluate(isGameStarting: true), GameplayModeNames.Mod, "server-1", runtime);
        LauncherConfigurationAvailability running = LauncherConfigurationAvailabilityPolicy.Evaluate(
            Evaluate(isGameRunning: true), GameplayModeNames.Mod, "server-1", runtime);

        Assert.False(starting.IsRendererConfigurationEnabled);
        Assert.False(starting.IsGameplayConfigurationEnabled);
        Assert.False(starting.IsModSelectable);
        Assert.False(running.IsRendererConfigurationEnabled);
        Assert.False(running.IsGameplayConfigurationEnabled);
        Assert.False(running.IsModSelectable);
    }

    [Fact]
    public void Mod_selection_is_local_and_launch_requires_validated_source_files()
    {
        LauncherInteractionState idle = Evaluate();

        LauncherConfigurationAvailability ready = LauncherConfigurationAvailabilityPolicy.Evaluate(
            idle, GameplayModeNames.Mod, "server-1", new ModRuntimeObservation(true, true, "runtime-1"));
        LauncherConfigurationAvailability validatedButNotLoaded = LauncherConfigurationAvailabilityPolicy.Evaluate(
            idle, GameplayModeNames.Mod, "server-1", new ModRuntimeObservation(true, false, "runtime-validated"));
        LauncherConfigurationAvailability missing = LauncherConfigurationAvailabilityPolicy.Evaluate(
            idle, GameplayModeNames.Mod, "server-1", ModRuntimeObservation.Unavailable);

        Assert.True(ready.IsModAvailable);
        Assert.True(ready.IsModSelectable);
        Assert.Equal(GameplayMode.Mod, ready.EffectiveGameplayMode);
        Assert.True(validatedButNotLoaded.IsModAvailable);
        Assert.True(validatedButNotLoaded.IsModSelectable);
        Assert.False(validatedButNotLoaded.IsModRuntimeLoaded);
        Assert.Equal(GameplayMode.Mod, validatedButNotLoaded.EffectiveGameplayMode);
        Assert.False(missing.IsModAvailable);
        Assert.True(missing.IsModSelectable);
        Assert.Equal(GameplayMode.Mod, missing.EffectiveGameplayMode);
    }

    [Fact]
    public void Launch_resolution_migrates_legacy_to_official_and_rejects_ungated_mod()
    {
        Assert.Equal(
            GameplayMode.Official,
            LauncherConfigurationAvailabilityPolicy.ResolveForLaunch(
                "Casual", new ModRuntimeObservation(true, true, "runtime-1")));
        Assert.Equal(
            GameplayMode.Official,
            LauncherConfigurationAvailabilityPolicy.ResolveForLaunch(
                "Serious", new ModRuntimeObservation(true, true, "runtime-1")));
        Assert.Equal(
            GameplayMode.Mod,
            LauncherConfigurationAvailabilityPolicy.ResolveForLaunch(
                "MOD", new ModRuntimeObservation(true, false, "runtime-1")));
        Assert.Throws<InvalidOperationException>(() =>
            LauncherConfigurationAvailabilityPolicy.ResolveForLaunch(
                "MOD", ModRuntimeObservation.Unavailable));
    }

    private static LauncherInteractionState Evaluate(
        bool isBusy = false,
        bool isLaunchFlowActive = false,
        bool isUpdating = false,
        bool isGameStarting = false,
        bool isGameRunning = false,
        bool isStartupRestartAvailable = false,
        bool isEndingGame = false,
        bool isNetworkReady = false,
        bool hasValidClientIp = false,
        bool isNetworkCheckCoolingDown = false,
        bool isNetworkCheckInProgress = false)
        => LauncherInteractionPolicy.Evaluate(new LauncherInteractionInput(
            isBusy,
            isLaunchFlowActive,
            isUpdating,
            isGameStarting,
            isGameRunning,
            isStartupRestartAvailable,
            isEndingGame,
            isNetworkReady,
            hasValidClientIp,
            isNetworkCheckCoolingDown,
            isNetworkCheckInProgress));
}
