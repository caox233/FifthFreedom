using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Text.Json;

namespace SCBL.Client.Tests;

public sealed class LauncherSettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scbl-settings-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadRecoversPreviousSettingsFromAtomicBackup()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);

        service.Save(CreateSettings("first-user"));
        service.Save(CreateSettings("second-user"));
        File.WriteAllText(path, "{broken-json");

        LauncherSettings recovered = service.Load();

        Assert.Equal("first-user", recovered.Username);
        Assert.Equal("10.66.0.25", recovered.LastAssignedVirtualIp);
        Assert.Equal(PublicTunnelConfig.DefaultWssPort, recovered.EasyTierWssPort);
        Assert.Equal(PublicTunnelConfig.DefaultQuicPort, recovered.EasyTierQuicPort);
    }

    [Fact]
    public void DefaultSettingsUseCurrentWssPortWithoutLegacyMigration()
    {
        Directory.CreateDirectory(_directory);
        var service = new LauncherSettingsService(Path.Combine(_directory, "missing.json"));

        LauncherSettings settings = service.Load();

        Assert.Equal(PublicTunnelConfig.DefaultWssPort, settings.EasyTierWssPort);
        Assert.Equal(PublicTunnelConfig.DefaultQuicPort, settings.EasyTierQuicPort);
        Assert.Equal(PublicTunnelConfig.DefaultTestPublicEndpoint, settings.TestPublicEndpoint);
        Assert.Equal(PublicTunnelConfig.DefaultTestPublicUpdatePort, settings.TestPublicUpdatePort);
        Assert.Equal("test.example.invalid:11011", settings.TestPublicEndpoint);
        Assert.Equal(18081, settings.TestPublicUpdatePort);
        Assert.Equal(ClientNetworkModeNames.Fast, settings.NetworkMode);
        Assert.Equal(GameplayModeNames.Official, settings.GameplayMode);
        Assert.Equal(LauncherSettingsService.DefaultModFovScale, settings.ModFovScale);
        Assert.False(settings.FovPromptHandled);
        Assert.Equal(LauncherSettingsService.DefaultGameExecutable, settings.GameExecutable);
    }

    [Fact]
    public void Renderer_defaults_to_dx11_and_only_explicit_dx9_is_preserved()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);

        File.WriteAllText(path, "{\"GameExecutable\":\"unsupported.exe\"}");
        Assert.Equal(LauncherSettingsService.DefaultGameExecutable, service.Load().GameExecutable);

        File.WriteAllText(path, "{\"GameExecutable\":\"Blacklist_game.exe\"}");
        Assert.Equal(LauncherSettingsService.Dx9GameExecutable, service.Load().GameExecutable);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.23, 1.23)]
    [InlineData(1.3, 1.3)]
    [InlineData(2.0, 1.3)]
    public void Mod_fov_scale_is_persisted_and_clamped(double requested, double expected)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        LauncherSettings settings = CreateSettings("sam");
        settings.ModFovScale = requested;

        service.Save(settings);

        Assert.Equal(expected, service.Load().ModFovScale, precision: 6);
    }

    [Fact]
    public void First_run_fov_prompt_completion_is_persisted()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        LauncherSettings settings = CreateSettings("sam");
        settings.FovPromptHandled = true;

        service.Save(settings);

        Assert.True(service.Load().FovPromptHandled);
    }

    [Fact]
    public void Non_finite_mod_fov_scale_fails_closed_to_default()
    {
        Assert.Equal(
            LauncherSettingsService.DefaultModFovScale,
            LauncherSettingsService.NormalizeModFovScale(double.NaN));
        Assert.Equal(
            LauncherSettingsService.DefaultModFovScale,
            LauncherSettingsService.NormalizeModFovScale(double.PositiveInfinity));
    }

    [Fact]
    public void NetworkModePersistsAndInvalidValuesFallBackToFast()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        LauncherSettings settings = CreateSettings("sam");
        settings.NetworkMode = ClientNetworkModeNames.Compatible;
        settings.EasyTierQuicPort = 12012;

        service.Save(settings);
        Assert.Equal(ClientNetworkModeNames.Compatible, service.Load().NetworkMode);
        Assert.Equal(12012, service.Load().EasyTierQuicPort);

        settings.NetworkMode = "unsupported";
        settings.EasyTierQuicPort = -1;
        service.Save(settings);
        Assert.Equal(ClientNetworkModeNames.Fast, service.Load().NetworkMode);
        Assert.Equal(PublicTunnelConfig.DefaultQuicPort, service.Load().EasyTierQuicPort);
    }

    [Fact]
    public void GameplayModeLoadsOnlyModAndUnknownValuesFailClosedToOfficial()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        File.WriteAllText(path, "{\"GameplayMode\":\"MOD\"}");
        Assert.Equal(GameplayModeNames.Mod, service.Load().GameplayMode);

        foreach (string unknown in new[] { "modified", "unsupported", "" })
        {
            File.WriteAllText(path, $"{{\"GameplayMode\":\"{unknown}\"}}");
            Assert.Equal(GameplayModeNames.Official, service.Load().GameplayMode);
        }
    }

    [Fact]
    public void RecoveryCodeIsEncryptedAtRestAndRestoredForCurrentUser()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        LauncherSettings settings = CreateSettings("sam");
        settings.RecoveryCodeUsername = "sam";
        settings.RecoveryCode = "0ABC-1DEF-2GHI-3JKL-4MNO-5PQR";

        service.Save(settings);

        string persisted = File.ReadAllText(path);
        Assert.DoesNotContain(settings.RecoveryCode, persisted);
        LauncherSettings restored = service.Load();
        Assert.Equal("sam", restored.RecoveryCodeUsername);
        Assert.Equal(settings.RecoveryCode, restored.RecoveryCode);
    }

    [Fact]
    public void Update_channel_preference_persists_and_invalid_values_fail_closed_to_stable()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        LauncherSettings settings = CreateSettings("sam");
        settings.ClientUpdateChannelPreference = "test";

        service.Save(settings);
        Assert.Equal("test", service.Load().ClientUpdateChannelPreference);

        settings.ClientUpdateChannelPreference = "preview";
        service.Save(settings);
        Assert.Equal("stable", service.Load().ClientUpdateChannelPreference);
    }

    [Fact]
    public void Stable_and_test_server_profiles_are_persisted_independently()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        var service = new LauncherSettingsService(path);
        LauncherSettings settings = CreateSettings("sam");
        settings.PublicEndpoint = "stable.example:11010";
        settings.PublicUpdatePort = 18080;
        settings.TestPublicEndpoint = "test.example:11011";
        settings.TestPublicUpdatePort = 18081;

        service.Save(settings);
        LauncherSettings restored = service.Load();

        Assert.Equal("stable.example:11010", restored.PublicEndpoint);
        Assert.Equal(18080, restored.PublicUpdatePort);
        Assert.Equal("test.example:11011", restored.TestPublicEndpoint);
        Assert.Equal(18081, restored.TestPublicUpdatePort);
    }

    [Fact]
    public void Startup_channel_read_does_not_decrypt_unrelated_credentials()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        File.WriteAllText(
            path,
            """
            {
              "ClientUpdateChannelPreference": "test",
              "PasswordProtected": "not-valid-dpapi",
              "TunnelSecretProtected": "not-valid-dpapi"
            }
            """);
        var service = new LauncherSettingsService(path);

        string preference = service.LoadClientUpdateChannelPreference();

        Assert.Equal("test", preference);
    }

    [Fact]
    public void Explicit_channel_persistence_changes_only_the_channel_field()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "launcher_settings.json");
        File.WriteAllText(
            path,
            """
            {
              "ClientUpdateChannelPreference": "stable",
              "PasswordProtected": "opaque-dpapi-value",
              "FutureSetting": { "keep": true }
            }
            """);
        var service = new LauncherSettingsService(path);

        service.SaveClientUpdateChannelPreference(ClientUpdateChannel.Test);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            "test",
            document.RootElement.GetProperty("ClientUpdateChannelPreference").GetString());
        Assert.Equal(
            "opaque-dpapi-value",
            document.RootElement.GetProperty("PasswordProtected").GetString());
        Assert.True(
            document.RootElement.GetProperty("FutureSetting").GetProperty("keep").GetBoolean());
    }

    private static LauncherSettings CreateSettings(string username) => new()
    {
        Username = username,
        Password = "test-password",
        TunnelSecret = "test-tunnel-secret",
        LastAssignedVirtualIp = "10.66.0.25",
        EasyTierWssPort = PublicTunnelConfig.DefaultWssPort
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
