using System;
using System.IO;
using System.Threading.Tasks;
using SplinterCellCNLauncher.ApplicationLayer;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Applies a staged Updater component before any update check. The canonical Updater lives
/// in the client root; full-package updates run from a temporary copy so this file is never asked
/// to overwrite itself while running.
/// </summary>
public sealed class UpdaterBootstrapService
{
    private readonly LauncherRuntimeContext _runtimeContext;

    public UpdaterBootstrapService(LauncherRuntimeContext runtimeContext)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
    }

    public const string UpdaterRelativePath = ClientInstallLayout.UpdaterRelativePath;

    public async Task EnsureCurrentUpdaterAsync()
    {
        try
        {
            await Task.Run(() => StagedComponentBootstrapService.ApplyUpdaterAndEasyTier(
                _runtimeContext.UpdateChannelName)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Error("Staged Updater/EasyTier component application failed; packaged files remain active: " + ex.Message);
        }

        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string updater = ClientInstallLayout.Resolve(baseDir, UpdaterRelativePath);
        if (File.Exists(updater))
            LogService.Info("Canonical root Updater is ready.");
        else
            LogService.Error("Canonical root Updater is missing: " + updater);
    }
}
