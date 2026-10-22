using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.ApplicationLayer;

public sealed record ModRuntimeObservation(
    bool FilesValidated,
    bool RuntimeLoaded,
    string ObservationId)
{
    public static ModRuntimeObservation Unavailable { get; } = new(false, false, "");
}

public sealed record LauncherConfigurationAvailability(
    bool IsRendererConfigurationEnabled,
    bool IsGameplayConfigurationEnabled,
    bool IsModAvailable,
    bool IsModSelectable,
    bool IsModRuntimeLoaded,
    GameplayMode EffectiveGameplayMode,
    string ServerObservationId,
    string RuntimeObservationId);

public static class LauncherConfigurationAvailabilityPolicy
{
    public static GameplayMode ResolveForLaunch(
        string? requestedGameplayMode,
        ModRuntimeObservation modRuntime)
    {
        ArgumentNullException.ThrowIfNull(modRuntime);
        GameplayMode requested = GameplayModeNames.ParseOrOfficial(requestedGameplayMode);
        if (requested == GameplayMode.Mod && !modRuntime.FilesValidated)
        {
            throw new InvalidOperationException(
                "MOD mode requires a complete validated local gameplay DLL.");
        }
        return requested;
    }

    public static LauncherConfigurationAvailability Evaluate(
        LauncherInteractionState interaction,
        string? requestedGameplayMode,
        string serverObservationId,
        ModRuntimeObservation modRuntime)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(modRuntime);

        bool configurationEnabled = interaction.IsLaunchConfigurationEnabled;
        bool modAvailable = modRuntime.FilesValidated;
        bool modSelectable = configurationEnabled;
        GameplayMode requested = GameplayModeNames.ParseOrOfficial(requestedGameplayMode);
        GameplayMode effective = requested;

        return new LauncherConfigurationAvailability(
            IsRendererConfigurationEnabled: configurationEnabled,
            IsGameplayConfigurationEnabled: configurationEnabled,
            IsModAvailable: modAvailable,
            IsModSelectable: modSelectable,
            IsModRuntimeLoaded: modAvailable && modRuntime.RuntimeLoaded,
            EffectiveGameplayMode: effective,
            ServerObservationId: serverObservationId,
            RuntimeObservationId: modRuntime.ObservationId);
    }
}
