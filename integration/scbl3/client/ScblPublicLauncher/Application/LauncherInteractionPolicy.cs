namespace SplinterCellCNLauncher.ApplicationLayer;

public enum LauncherPrimaryAction
{
    Launch,
    Starting,
    Restart,
    EndGame,
    Updating
}

public sealed record LauncherInteractionInput(
    bool IsBusy,
    bool IsLaunchFlowActive,
    bool IsUpdating,
    bool IsGameStarting,
    bool IsGameRunning,
    bool IsStartupRestartAvailable,
    bool IsEndingGame,
    bool IsNetworkReady,
    bool HasValidClientIp,
    bool IsNetworkCheckCoolingDown,
    bool IsNetworkCheckInProgress);

public sealed record LauncherInteractionState(
    LauncherPrimaryAction PrimaryAction,
    bool IsLaunchEnabled,
    bool IsNetworkCheckEnabled,
    bool IsLaunchConfigurationEnabled);

/// <summary>
/// Owns the launcher interaction rules without referencing WPF controls.
/// The presentation layer renders the returned state and remains free to
/// choose typography, colors, animations and localized labels.
/// </summary>
public static class LauncherInteractionPolicy
{
    public static LauncherInteractionState Evaluate(LauncherInteractionInput input)
    {
        LauncherPrimaryAction primaryAction = input.IsUpdating
            ? LauncherPrimaryAction.Updating
            : input.IsGameStarting
                ? input.IsStartupRestartAvailable
                    ? LauncherPrimaryAction.Restart
                    : LauncherPrimaryAction.Starting
                : input.IsGameRunning
                    ? LauncherPrimaryAction.EndGame
                    : LauncherPrimaryAction.Launch;

        bool canEndRunningGame = input.IsGameRunning;
        bool canRestartStartingGame = input.IsGameStarting && input.IsStartupRestartAvailable;
        bool canStartGame = input.IsNetworkReady
            && input.HasValidClientIp
            && !input.IsBusy
            && !input.IsLaunchFlowActive
            && !input.IsGameStarting;

        bool isLaunchEnabled = !input.IsEndingGame
            && !input.IsUpdating
            && (canEndRunningGame || canRestartStartingGame || canStartGame);

        bool isNetworkCheckEnabled = !input.IsNetworkCheckCoolingDown
            && !input.IsNetworkCheckInProgress
            && !input.IsEndingGame;

        bool isLaunchConfigurationEnabled = !input.IsLaunchFlowActive
            && !input.IsGameStarting
            && !input.IsGameRunning
            && !input.IsUpdating
            && !input.IsEndingGame;

        return new LauncherInteractionState(
            primaryAction,
            isLaunchEnabled,
            isNetworkCheckEnabled,
            isLaunchConfigurationEnabled);
    }
}
