using System.Text.Json;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

public static class NetworkEnvironmentPreferences
{
    public static NetworkEnvironmentKind Environment(string? value)
        => string.Equals(value, "Lan", StringComparison.OrdinalIgnoreCase) ? NetworkEnvironmentKind.Lan : NetworkEnvironmentKind.Public;
    public static PublicTransport Transport(string? value)
        => string.Equals(value, "Compatible", StringComparison.OrdinalIgnoreCase) ? PublicTransport.Compatible : PublicTransport.Fast;
    public static void Normalize(LauncherSettings settings)
    {
        settings.NetworkEnvironment = Environment(settings.NetworkEnvironment).ToString();
        settings.NetworkMode = Transport(settings.NetworkMode).ToString();
        // Legacy DX9 selections migrate on load, before any UI or launch reads them.
        settings.GameExecutable = "Blacklist_DX11_game.exe";
        settings.PublicEndpoint ??= "";
        settings.TestPublicEndpoint ??= "";
        settings.PublicDeploymentConfigurationPath ??= "";
    }
}

public static class LauncherSettingsCodec
{
    private static readonly JsonSerializerOptions NumberOptions = new()
    { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static LauncherSettings Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<LauncherSettings>(json, NumberOptions) ?? new LauncherSettings();
        NetworkEnvironmentPreferences.Normalize(settings);
        if (settings.CredentialStorageVersion == 0) settings.RememberPassword = true;
        return settings;
    }

    public static string Serialize(LauncherSettings settings)
    {
        // Work on a copy; persistence failures must not rewrite the caller's preference.
        var copy = Deserialize(JsonSerializer.Serialize(settings, NumberOptions));
        copy.CredentialStorageVersion = 1;
        copy.Password = "";
        copy.GameplayMode = string.Equals(settings.GameplayMode, "MOD", StringComparison.OrdinalIgnoreCase) ? "MOD" : "Official";
        copy.FovScale = double.IsFinite(settings.FovScale) ? Math.Clamp(settings.FovScale, 1.0, 1.3) : 1.2;
        copy.Language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
        // Preserve an explicit current remember-password choice during first save.
        copy.RememberPassword = settings.RememberPassword;
        return JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
    }
}
