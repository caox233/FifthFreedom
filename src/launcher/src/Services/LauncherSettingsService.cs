using SplinterCellCNLauncher.Models;
using System.Text.Json;

using System;
using System.IO;
namespace SplinterCellCNLauncher.Services;

public sealed class LauncherSettingsService
{
    public string SettingsPath { get; } = Path.Combine(LogService.AppDataDir, "launcher_settings.json");

    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new LauncherSettings();

            string json = File.ReadAllText(SettingsPath);
            return LauncherSettingsCodec.Deserialize(json);
        }
        catch (Exception ex)
        {
            LogService.Error(ex);
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(LogService.AppDataDir);

        ScblLan.LocalData.AtomicWrite(SettingsPath, LauncherSettingsCodec.Serialize(settings));
    }
}
