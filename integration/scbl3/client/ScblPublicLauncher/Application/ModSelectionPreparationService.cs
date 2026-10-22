using SplinterCellCNLauncher.Services;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.ApplicationLayer;

public sealed record ModSelectionPreparationResult(
    FusionFixDeploymentOutcome FusionFixOutcome,
    int InstalledFileCount);

/// <summary>
/// Single non-UI entry for preparing a server-enabled MOD selection. UI only
/// supplies the already evaluated availability projection and game directory.
/// </summary>
public sealed class ModSelectionPreparationService
{
    private readonly FusionFixDeploymentService _fusionFixDeploymentService;

    public ModSelectionPreparationService(
        FusionFixDeploymentService? fusionFixDeploymentService = null)
    {
        _fusionFixDeploymentService = fusionFixDeploymentService
            ?? new FusionFixDeploymentService();
    }

    public async Task<ModSelectionPreparationResult> PrepareAsync(
        string gameDirectory,
        LauncherConfigurationAvailability availability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (!availability.IsModSelectable)
            throw new InvalidOperationException("MOD cannot be changed while launch configuration is locked.");

        FusionFixDeploymentResult result = await _fusionFixDeploymentService
            .EnsureInstalledAsync(gameDirectory, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfInstallationWasPreserved(result);
        return new ModSelectionPreparationResult(
            result.Outcome,
            result.InstalledFileCount);
    }

    internal Task<FusionFixDeploymentResult> EnsureForLaunchAsync(
        string gameDirectory,
        GameplayMode gameplayMode,
        CancellationToken cancellationToken = default)
        => gameplayMode == GameplayMode.Mod
            ? EnsureModReadyForLaunchAsync(gameDirectory, cancellationToken)
            : Task.FromResult(new FusionFixDeploymentResult(
                FusionFixDeploymentOutcome.AlreadyPresent,
                InstalledFileCount: 0));

    private async Task<FusionFixDeploymentResult> EnsureModReadyForLaunchAsync(
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        FusionFixDeploymentResult result = await _fusionFixDeploymentService
            .EnsureInstalledAsync(gameDirectory, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfInstallationWasPreserved(result);
        return result;
    }

    private static void ThrowIfInstallationWasPreserved(FusionFixDeploymentResult result)
    {
        if (result.Outcome != FusionFixDeploymentOutcome.ExistingModPreserved)
            return;

        string conflict = string.IsNullOrWhiteSpace(result.ConflictRelativePath)
            ? "unknown"
            : result.ConflictRelativePath;
        throw new InvalidOperationException(
            "FusionFix was not installed because an existing MOD file must be preserved. " +
            $"reason={result.ReasonCode}, conflict={conflict}.");
    }
}
