using System.Diagnostics;

namespace SplinterCellCNLauncher.Services;

public sealed record GameRuntimeValidationResult(bool IsValid, string? Warning);

public sealed class GameRuntimeValidationService
{
    private const int RuntimeReadyMaxAttempts = 21;
    private static readonly TimeSpan RuntimeReadyPollInterval = TimeSpan.FromMilliseconds(250);

    public Task<GameRuntimeValidationResult> ValidateAsync(
        int processId,
        string expectedGamePath,
        string expectedHookPath,
        CancellationToken cancellationToken = default)
        => WaitForValidationAsync(
            () => ValidateOnce(processId, expectedGamePath, expectedHookPath),
            RuntimeReadyMaxAttempts,
            token => Task.Delay(RuntimeReadyPollInterval, token),
            cancellationToken);

    private static GameRuntimeValidationResult ValidateOnce(
        int processId,
        string expectedGamePath,
        string expectedHookPath)
    {
        try
        {
            expectedGamePath = Path.GetFullPath(expectedGamePath);
            expectedHookPath = Path.GetFullPath(expectedHookPath);
            using Process process = Process.GetProcessById(processId);
            string? actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath)
                || !Path.GetFullPath(actualPath).Equals(
                    expectedGamePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new GameRuntimeValidationResult(false, "未找到可校验的游戏进程。");
            }

            var loadedModules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessModule module in process.Modules)
            {
                string moduleName = module.ModuleName ?? "";
                if (!string.IsNullOrWhiteSpace(moduleName))
                    loadedModules[moduleName] = module.FileName;
            }

            string gameDirectory = Path.GetDirectoryName(expectedHookPath) ?? "";
            bool requireGameplay = File.Exists(Path.Combine(gameDirectory, "scbl_gameplay.dll"));
            bool valid = ValidateLoadedModules(
                expectedHookPath,
                requireGameplay,
                loadedModules,
                out string? warning);
            return new GameRuntimeValidationResult(valid, warning);
        }
        catch (ArgumentException)
        {
            return new GameRuntimeValidationResult(false, "未找到可校验的游戏进程。");
        }
        catch (InvalidOperationException)
        {
            return new GameRuntimeValidationResult(false, "未找到可校验的游戏进程。");
        }
        catch (Exception ex)
        {
            LogService.Error($"Runtime validation process check failed PID={processId}: {ex.Message}");
            return new GameRuntimeValidationResult(false, "游戏联机组件运行时校验失败：" + ex.Message);
        }
    }

    internal static async Task<GameRuntimeValidationResult> WaitForValidationAsync(
        Func<GameRuntimeValidationResult> validateOnce,
        int maxAttempts,
        Func<CancellationToken, Task> waitBeforeRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateOnce);
        ArgumentNullException.ThrowIfNull(waitBeforeRetry);
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        GameRuntimeValidationResult result = new(false, "运行时尚未就绪。");
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = validateOnce();
            if (result.IsValid || attempt == maxAttempts)
                return result;
            await waitBeforeRetry(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    internal static bool ValidateLoadedModules(
        string expectedHookPath,
        bool requireGameplay,
        IReadOnlyDictionary<string, string> loadedModules,
        out string? warning)
    {
        ArgumentNullException.ThrowIfNull(loadedModules);
        string gameDirectory = Path.GetDirectoryName(Path.GetFullPath(expectedHookPath))
            ?? throw new InvalidDataException("Hooks runtime path has no parent directory.");

        if (!ValidateCanonicalModule("uplay_r1_loader.dll", expectedHookPath, loadedModules, out warning))
            return false;

        if (!requireGameplay)
        {
            warning = null;
            return true;
        }

        string gameplayPath = Path.Combine(gameDirectory, "scbl_gameplay.dll");
        if (!ValidateCanonicalModule("scbl_gameplay.dll", gameplayPath, loadedModules, out warning))
            return false;
        warning = null;
        return true;
    }

    private static bool ValidateCanonicalModule(
        string moduleName,
        string expectedPath,
        IReadOnlyDictionary<string, string> loadedModules,
        out string? warning)
    {
        if (!loadedModules.TryGetValue(moduleName, out string? loadedPath)
            || string.IsNullOrWhiteSpace(loadedPath))
        {
            warning = $"游戏已经启动，但没有检测到游戏进程加载 {moduleName}。";
            return false;
        }

        if (!Path.GetFullPath(loadedPath).Equals(
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            warning = $"游戏加载的 {moduleName} 不是当前游戏目录下的已校验组件。";
            return false;
        }

        warning = null;
        return true;
    }

}
