using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher.ApplicationLayer;

/// <summary>
/// Single platform entry used by presentation code. UI supplies interaction
/// state and the selected game directory; it never reads server flags or DLLs.
/// </summary>
public sealed class LauncherConfigurationAvailabilityService
{
    private readonly Func<CancellationToken, Task<ModRuntimeObservation>> _observeModRuntimeFilesAsync;
    private ModRuntimeObservation _lastObservation = ModRuntimeObservation.Unavailable;

    public LauncherConfigurationAvailabilityService(
        HookDllService hookDllService)
        : this((hookDllService ?? throw new ArgumentNullException(nameof(hookDllService)))
            .ObserveModRuntimeFilesAsync)
    {
    }

    internal LauncherConfigurationAvailabilityService(
        Func<CancellationToken, Task<ModRuntimeObservation>> observeModRuntimeFilesAsync)
    {
        _observeModRuntimeFilesAsync = observeModRuntimeFilesAsync
            ?? throw new ArgumentNullException(nameof(observeModRuntimeFilesAsync));
    }

    public async Task<ModRuntimeObservation> RefreshModRuntimeObservationAsync(
        CancellationToken cancellationToken = default)
    {
        ModRuntimeObservation observation = await _observeModRuntimeFilesAsync(cancellationToken)
            .ConfigureAwait(false) ?? ModRuntimeObservation.Unavailable;
        Volatile.Write(ref _lastObservation, observation);
        return observation;
    }

    public LauncherConfigurationAvailability Get(
        LauncherInteractionState interaction,
        string? requestedGameplayMode,
        string serverObservationId)
    {
        ModRuntimeObservation runtime = Volatile.Read(ref _lastObservation);
        return LauncherConfigurationAvailabilityPolicy.Evaluate(
            interaction,
            requestedGameplayMode,
            serverObservationId,
            runtime);
    }
}
