using SplinterCellCNLauncher.Models;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SplinterCellCNLauncher.Services;

public sealed class LauncherSettingsService
{
    public const string DefaultGameExecutable = "Blacklist_DX11_game.exe";
    public const string Dx9GameExecutable = "Blacklist_game.exe";
    public const double MinimumModFovScale = 1.0;
    public const double MaximumModFovScale = 1.3;
    public const double DefaultModFovScale = 1.2;

    public string SettingsPath { get; }
    private string BackupPath => SettingsPath + ".bak";

    public LauncherSettingsService(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(LogService.ConfigDirectory, "launcher_settings.json")
            : Path.GetFullPath(settingsPath);
    }

    public LauncherSettings Load()
    {
        LauncherSettings? settings = TryLoadFile(SettingsPath, "primary");
        if (settings == null && File.Exists(BackupPath))
        {
            LogService.Warning("Primary launcher settings are unavailable; attempting backup recovery.");
            settings = TryLoadFile(BackupPath, "backup");
            if (settings != null)
                LogService.Info("Launcher settings recovered from backup: " + BackupPath);
        }

        settings ??= new LauncherSettings();
        try
        {
            settings = WithDefaults(settings);
            settings.Password = CredentialProtectionService.Unprotect(settings.PasswordProtected);
            settings.RecoveryCode = CredentialProtectionService.Unprotect(settings.RecoveryCodeProtected);

            // A server-generated bootstrap settings file may provide TunnelSecret once.
            // Save() immediately replaces it with the current-user DPAPI field.
            string plainSecret = !string.IsNullOrWhiteSpace(settings.TunnelSecretProtected)
                ? CredentialProtectionService.Unprotect(settings.TunnelSecretProtected)
                : settings.TunnelSecret;
            settings.TunnelSecret = PublicTunnelConfig.NormalizeTunnelSecret(plainSecret);
            return settings;
        }
        catch (Exception ex)
        {
            LogService.Error(ex);
            return WithDefaults(new LauncherSettings());
        }
    }

    public string LoadClientUpdateChannelPreference()
    {
        LauncherSettings? settings = TryLoadFile(SettingsPath, "primary");
        if (settings == null && File.Exists(BackupPath))
            settings = TryLoadFile(BackupPath, "backup");
        return settings?.ClientUpdateChannelPreference ?? "stable";
    }

    public void SaveClientUpdateChannelPreference(ClientUpdateChannel channel)
    {
        LogService.InitializeStorage();
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8));
            root = parsed as JsonObject
                ?? throw new InvalidDataException("Launcher settings root must be an object.");
        }
        else
        {
            root = new JsonObject();
        }
        root[nameof(LauncherSettings.ClientUpdateChannelPreference)] =
            ClientUpdateChannelNames.ToName(channel);
        AtomicWriteText(
            SettingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static LauncherSettings? TryLoadFile(string path, string source)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<LauncherSettings>(json);
        }
        catch (Exception ex)
        {
            LogService.Warning($"Launcher settings {source} file is unreadable: path={path}, error={ex.Message}");
            return null;
        }
    }

    public void Save(LauncherSettings settings)
    {
        LogService.InitializeStorage();
        Directory.CreateDirectory(LogService.ConfigDirectory);
        string effectiveSecret = PublicTunnelConfig.NormalizeTunnelSecret(settings.TunnelSecret);
        var copy = WithDefaults(new LauncherSettings
        {
            Username = settings.Username,
            GameDirectory = settings.GameDirectory,
            GameExecutable = settings.GameExecutable,
            GameplayMode = settings.GameplayMode,
            ModFovScale = settings.ModFovScale,
            FovPromptHandled = settings.FovPromptHandled,
            Password = string.Empty,
            PasswordProtected = CredentialProtectionService.Protect(settings.Password),
            RecoveryCode = string.Empty,
            RecoveryCodeProtected = CredentialProtectionService.Protect(settings.RecoveryCode),
            RecoveryCodeUsername = settings.RecoveryCodeUsername,
            PublicEndpoint = settings.PublicEndpoint,
            TestPublicEndpoint = settings.TestPublicEndpoint,
            TunnelSecret = string.Empty,
            TunnelSecretProtected = CredentialProtectionService.Protect(effectiveSecret),
            EasyTierInstanceId = settings.EasyTierInstanceId,
            EasyTierNetworkName = settings.EasyTierNetworkName,
            NetworkMode = settings.NetworkMode,
            EasyTierWssPort = settings.EasyTierWssPort,
            EasyTierQuicPort = settings.EasyTierQuicPort,
            ForceGameVirtualAdapter = settings.ForceGameVirtualAdapter,
            SaveOverwritePromptHandled = settings.SaveOverwritePromptHandled,
            Language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language,
            MusicEnabled = settings.MusicEnabled,
            GuideCompleted = settings.GuideCompleted,
            ClientUpdateChannelPreference = settings.ClientUpdateChannelPreference,
            PublicUpdatePort = settings.PublicUpdatePort,
            TestPublicUpdatePort = settings.TestPublicUpdatePort,
            LastAssignedVirtualIp = settings.LastAssignedVirtualIp,
            LastServerVirtualIp = string.IsNullOrWhiteSpace(settings.LastServerVirtualIp) ? PublicTunnelConfig.ServerVirtualIp : settings.LastServerVirtualIp,
            LastTunnelConnectedAt = settings.LastTunnelConnectedAt,
            LastLatencyMs = settings.LastLatencyMs,
            DismissedStartupAnnouncementId = settings.DismissedStartupAnnouncementId
        });

        // Keep the in-memory settings usable after saving, but never write plaintext.
        copy.TunnelSecret = string.Empty;

        string json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
        AtomicWriteText(SettingsPath, json);
    }

    private void AtomicWriteText(string path, string text)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);

        try
        {
            using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Best effort on older Windows / file systems.
        }

        try
        {
            if (File.Exists(path))
            {
                File.Replace(tmp, path, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            if (File.Exists(path))
                File.Copy(path, BackupPath, overwrite: true);
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { }
        }
    }

    private static LauncherSettings WithDefaults(LauncherSettings settings)
    {
        settings.GameExecutable = settings.GameExecutable.Equals(
            Dx9GameExecutable,
            StringComparison.OrdinalIgnoreCase)
            ? Dx9GameExecutable
            : DefaultGameExecutable;
        // Only the server-gated MOD value can survive; every other value is
        // normalized fail-closed to Official.
        settings.GameplayMode = GameplayModeNames.NormalizeOrOfficial(settings.GameplayMode);
        settings.ModFovScale = NormalizeModFovScale(settings.ModFovScale);
        if (string.IsNullOrWhiteSpace(settings.Language))
            settings.Language = "zh-CN";
        settings.ClientUpdateChannelPreference = ClientUpdateChannelNames.ToName(
            ClientUpdateChannelNames.ParseOrStable(settings.ClientUpdateChannelPreference));

        if (string.IsNullOrWhiteSpace(settings.PublicEndpoint))
            settings.PublicEndpoint = PublicTunnelConfig.DefaultPublicEndpoint;
        else
            settings.PublicEndpoint = PublicTunnelConfig.NormalizePublicEndpoint(settings.PublicEndpoint);

        if (string.IsNullOrWhiteSpace(settings.TestPublicEndpoint))
            settings.TestPublicEndpoint = PublicTunnelConfig.DefaultTestPublicEndpoint;
        else
            settings.TestPublicEndpoint = PublicTunnelConfig.NormalizePublicEndpoint(settings.TestPublicEndpoint);

        if (string.IsNullOrWhiteSpace(settings.TunnelSecret))
            settings.TunnelSecret = PublicTunnelConfig.NormalizeTunnelSecret(Environment.GetEnvironmentVariable("SCBL_TUNNEL_SECRET"));
        if (string.IsNullOrWhiteSpace(settings.LastServerVirtualIp))
            settings.LastServerVirtualIp = PublicTunnelConfig.ServerVirtualIp;
        if (string.IsNullOrWhiteSpace(settings.EasyTierNetworkName))
            settings.EasyTierNetworkName = PublicTunnelConfig.EasyTierNetworkName;
        if (!Guid.TryParse(settings.EasyTierInstanceId, out _))
            settings.EasyTierInstanceId = Guid.NewGuid().ToString("D");
        settings.NetworkMode = ClientNetworkModeNames.ToName(
            ClientNetworkModeNames.ParseOrFast(settings.NetworkMode));
        if (settings.EasyTierWssPort is <= 0 or > 65535)
            settings.EasyTierWssPort = PublicTunnelConfig.DefaultWssPort;
        if (settings.EasyTierQuicPort is <= 0 or > 65535)
            settings.EasyTierQuicPort = PublicTunnelConfig.DefaultQuicPort;
        if (settings.PublicUpdatePort is <= 0 or > 65535)
            settings.PublicUpdatePort = PublicTunnelConfig.DefaultPublicUpdatePort;
        if (settings.TestPublicUpdatePort is <= 0 or > 65535)
            settings.TestPublicUpdatePort = PublicTunnelConfig.DefaultTestPublicUpdatePort;

        return settings;
    }

    public static double NormalizeModFovScale(double value)
        => double.IsFinite(value)
            ? Math.Clamp(value, MinimumModFovScale, MaximumModFovScale)
            : DefaultModFovScale;
}
