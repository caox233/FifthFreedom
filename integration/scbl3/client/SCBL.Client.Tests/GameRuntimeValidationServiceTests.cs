using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class GameRuntimeValidationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "scbl-runtime-validation",
        Guid.NewGuid().ToString("N"));
    [Fact]
    public void Official_runtime_requires_only_the_canonical_core_module()
    {
        string game = Directory.CreateDirectory(Path.Combine(_root, "SYSTEM")).FullName;
        string core = Write(Path.Combine(game, "uplay_r1_loader.dll"), "core");
        var modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uplay_r1_loader.dll"] = core
        };

        Assert.True(GameRuntimeValidationService.ValidateLoadedModules(
            core, requireGameplay: false, modules, out string? warning));
        Assert.Null(warning);
    }

    [Fact]
    public void Mod_runtime_requires_the_canonical_gameplay_module()
    {
        string game = Directory.CreateDirectory(Path.Combine(_root, "SYSTEM")).FullName;
        string core = Write(Path.Combine(game, "uplay_r1_loader.dll"), "core");
        string gameplay = Write(Path.Combine(game, "scbl_gameplay.dll"), "gameplay");
        var modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uplay_r1_loader.dll"] = core,
            ["scbl_gameplay.dll"] = gameplay
        };

        Assert.True(GameRuntimeValidationService.ValidateLoadedModules(
            core, requireGameplay: true, modules, out string? warning));
        Assert.Null(warning);

        modules["scbl_gameplay.dll"] = Write(Path.Combine(_root, "foreign-gameplay.dll"), "other");
        Assert.False(GameRuntimeValidationService.ValidateLoadedModules(
            core, requireGameplay: true, modules, out string? mismatch));
        Assert.Contains("不是当前游戏目录", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void Mod_runtime_rejects_missing_gameplay_module()
    {
        string game = Directory.CreateDirectory(Path.Combine(_root, "SYSTEM")).FullName;
        string core = Write(Path.Combine(game, "uplay_r1_loader.dll"), "core");
        Write(Path.Combine(game, "scbl_gameplay.dll"), "gameplay");
        var modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uplay_r1_loader.dll"] = core
        };

        Assert.False(GameRuntimeValidationService.ValidateLoadedModules(
            core, requireGameplay: true, modules, out string? warning));
        Assert.Contains("没有检测到", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_wait_retries_until_matching_runtime_is_observed()
    {
        int calls = 0;
        int waits = 0;
        GameRuntimeValidationResult result = await GameRuntimeValidationService.WaitForValidationAsync(
            () => ++calls < 3
                ? new GameRuntimeValidationResult(false, "pending")
                : new GameRuntimeValidationResult(true, null),
            maxAttempts: 5,
            _ =>
            {
                waits++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits);
    }

    [Fact]
    public async Task Readiness_wait_returns_last_failure_after_the_bound()
    {
        int calls = 0;
        int waits = 0;
        GameRuntimeValidationResult result = await GameRuntimeValidationService.WaitForValidationAsync(
            () =>
            {
                calls++;
                return new GameRuntimeValidationResult(false, $"pending-{calls}");
            },
            maxAttempts: 3,
            _ =>
            {
                waits++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("pending-3", result.Warning);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits);
    }

    [Fact]
    public async Task Readiness_wait_honors_cancellation_before_another_probe()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GameRuntimeValidationService.WaitForValidationAsync(
                () =>
                {
                    calls++;
                    return new GameRuntimeValidationResult(false, "pending");
                },
                maxAttempts: 3,
                _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(1, calls);
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
