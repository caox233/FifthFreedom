using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class LauncherCompositionRootTests
{
    [Fact]
    public void MainWindowCompositionRootStaysBelowArchitectureLimit()
    {
        string path = FindRepositoryFile("client", "ScblPublicLauncher", "MainWindow.xaml.cs");
        string source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(File.ReadLines(path).Count() <= 1200, "MainWindow.xaml.cs must stay at or below 1200 lines.");
        Assert.DoesNotContain("ResetGameQualityState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteGameQualitySnapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LooksLikeOriginalLauncherProcess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcessesByName(processName)", source, StringComparison.Ordinal);
        Assert.Contains("new LauncherRuntimeContext(App.ComponentUpdateChannel)", source, StringComparison.Ordinal);
        Assert.Contains("new GameNetworkMonitorService(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesDoNotReachBackIntoWpfApplicationOrControls()
    {
        string services = FindRepositoryDirectory("client", "ScblPublicLauncher", "Services");
        string application = FindRepositoryDirectory("client", "ScblPublicLauncher", "Application");
        string[] sources = Directory.GetFiles(services, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(application, "*.cs", SearchOption.AllDirectories))
            .ToArray();

        var violations = new List<string>();
        foreach (string path in sources)
        {
            string source = File.ReadAllText(path);
            if (source.Contains("App.", StringComparison.Ordinal)
                || source.Contains("System.Windows", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(path));
            }
        }

        Assert.True(violations.Count == 0, "Reverse WPF dependencies: " + string.Join(", ", violations));
    }

    [Fact]
    public void RuntimeContextKeepsChannelSelectionImmutableAndCanonical()
    {
        var stable = new LauncherRuntimeContext(ClientUpdateChannel.Stable);
        var test = new LauncherRuntimeContext(ClientUpdateChannel.Test);

        Assert.Equal("stable", stable.UpdateChannelName);
        Assert.Equal("test", test.UpdateChannelName);
        Assert.Equal(ClientUpdateChannel.Test, test.UpdateChannel);
    }

    [Fact]
    public void Game_launch_request_carries_the_persisted_mod_fov_scale()
    {
        var request = new GameLaunchRequest(
            Username: "sam",
            Password: "secret",
            GameDirectory: "game",
            GameExecutable: "Blacklist_game.exe",
            GameplayMode: GameplayModeNames.Mod,
            LauncherVersion: "test",
            TunnelSecret: "tunnel",
            ModFovScale: 1.27);

        Assert.Equal(1.27, request.ModFovScale, precision: 6);
    }

    [Fact]
    public void Resolved_gameplay_mode_is_passed_to_the_single_hooks_configuration_path()
    {
        string path = FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "Application",
            "GameLaunchCoordinator.cs");
        string source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "GameplayMode gameplayMode = LauncherConfigurationAvailabilityPolicy.ResolveForLaunch(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bindIp,\n            gameplayMode,\n            request.ModFovScale);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HookFeatureSettings.ForGameplayMode(request.GameplayMode)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GameNetworkQualityPercentilesRemainDeterministic()
    {
        Assert.Null(GameNetworkMonitorService.Percentile(Array.Empty<long>(), 0.95));
        Assert.Equal(20, GameNetworkMonitorService.Percentile([10, 20, 30, 40], 0.50));
        Assert.Equal(40, GameNetworkMonitorService.Percentile([10, 20, 30, 40], 0.95));
    }

    [Fact]
    public void Platform_process_invocations_do_not_bypass_execution_policy()
    {
        string adapter = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "ScblTunnelAdapterService.cs"));
        string diagnostics = File.ReadAllText(FindRepositoryFile(
            "client", "ScblPublicLauncher", "Services", "DiagnosticExportService.cs"));

        Assert.DoesNotContain("ExecutionPolicy", adapter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bypass", adapter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecutionPolicy", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bypass", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        string path = FindRepositoryPath(relativeSegments);
        if (File.Exists(path))
            return path;
        throw new FileNotFoundException("Could not locate repository source file.", path);
    }

    private static string FindRepositoryDirectory(params string[] relativeSegments)
    {
        string path = FindRepositoryPath(relativeSegments);
        if (Directory.Exists(path))
            return path;
        throw new DirectoryNotFoundException(path);
    }

    private static string FindRepositoryPath(string[] relativeSegments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(relativeSegments);
    }
}
