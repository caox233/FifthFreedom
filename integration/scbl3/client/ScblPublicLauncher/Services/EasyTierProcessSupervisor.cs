using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

internal interface IEasyTierCoreProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void BeginRead(Action<string?, bool> observer);
    void KillTree();
    bool WaitForExit(int milliseconds);
}

internal interface IEasyTierCoreProcessFactory
{
    IEasyTierCoreProcess Start(ProcessStartInfo startInfo);
}

internal sealed record EasyTierNetstatCommandResult(bool Completed, string StandardOutput);

internal interface IEasyTierNetstatCommandRunner
{
    EasyTierNetstatCommandResult Run(TimeSpan timeout);
}

internal interface IEasyTierNetstatProcess : IDisposable
{
    Task<string> ReadOutputAsync();
    bool WaitForExit(int milliseconds);
    void KillTree();
}

internal interface IEasyTierNetstatProcessFactory
{
    IEasyTierNetstatProcess Start();
}

internal sealed class SystemEasyTierNetstatProcessFactory : IEasyTierNetstatProcessFactory
{
    public IEasyTierNetstatProcess Start()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano -p tcp",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        return new SystemEasyTierNetstatProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("netstat.exe failed to start."));
    }
}

internal sealed class SystemEasyTierNetstatProcess : IEasyTierNetstatProcess
{
    private readonly Process _process;

    public SystemEasyTierNetstatProcess(Process process) => _process = process;
    public Task<string> ReadOutputAsync() => _process.StandardOutput.ReadToEndAsync();
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void KillTree() => _process.Kill(entireProcessTree: true);
    public void Dispose() => _process.Dispose();
}

internal sealed class SystemEasyTierNetstatCommandRunner : IEasyTierNetstatCommandRunner
{
    private readonly IEasyTierNetstatProcessFactory _factory;

    public SystemEasyTierNetstatCommandRunner(IEasyTierNetstatProcessFactory? factory = null)
        => _factory = factory ?? new SystemEasyTierNetstatProcessFactory();

    public EasyTierNetstatCommandResult Run(TimeSpan timeout)
    {
        using IEasyTierNetstatProcess process = _factory.Start();
        Task<string> outputTask = process.ReadOutputAsync();
        int timeoutMilliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.KillTree(); }
            catch (Exception ex)
            {
                LogService.Info(
                    $"netstat timeout cleanup: kill failed ({ex.GetType().Name}).");
            }
            try
            {
                if (!process.WaitForExit(500))
                    LogService.Info("netstat timeout cleanup: process did not exit within 500ms.");
            }
            catch (Exception ex)
            {
                LogService.Info(
                    $"netstat timeout cleanup: bounded wait failed ({ex.GetType().Name}).");
            }
            try { outputTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
            return new EasyTierNetstatCommandResult(false, string.Empty);
        }

        return new EasyTierNetstatCommandResult(
            true,
            outputTask.GetAwaiter().GetResult());
    }
}

internal sealed class SystemEasyTierCoreProcessFactory : IEasyTierCoreProcessFactory
{
    public IEasyTierCoreProcess Start(ProcessStartInfo startInfo)
        => new SystemEasyTierCoreProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("EasyTier 网络核心启动失败。"));
}

internal sealed class SystemEasyTierCoreProcess : IEasyTierCoreProcess
{
    private readonly Process _process;

    public SystemEasyTierCoreProcess(Process process) => _process = process;
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public void BeginRead(Action<string?, bool> observer)
    {
        _process.OutputDataReceived += (_, args) => observer(args.Data, false);
        _process.ErrorDataReceived += (_, args) => observer(args.Data, true);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void KillTree() => _process.Kill(entireProcessTree: true);
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void Dispose() => _process.Dispose();
}

internal sealed class EasyTierProcessSupervisor
{
    private readonly object _sync = new();
    private readonly object _lifecycleSync = new();
    private readonly ManualResetEventSlim _callbacksDrained = new(initialState: true);
    private readonly IEasyTierCoreProcessFactory _factory;
    private IEasyTierCoreProcess? _process;
    private long _generation;
    private int _activeCallbacks;

    public EasyTierProcessSupervisor(IEasyTierCoreProcessFactory? factory = null)
        => _factory = factory ?? new SystemEasyTierCoreProcessFactory();

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public bool TrackedProcessHasExited
    {
        get
        {
            lock (_sync)
            {
                try { return _process is { HasExited: true }; }
                catch { return true; }
            }
        }
    }

    public static string GetCorePath(string launcherBaseDir)
        => ClientInstallLayout.Resolve(launcherBaseDir, ClientInstallLayout.EasyTierCoreRelativePath);

    public IEasyTierCoreProcess Start(
        string coreExe,
        string configPath,
        Action<string?, bool> lineObserver)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = coreExe,
            WorkingDirectory = Path.GetDirectoryName(coreExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--config-file");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--rpc-portal");
        startInfo.ArgumentList.Add(PublicTunnelConfig.EasyTierRpcEndpoint);
        // The launcher owns log rotation. EasyTier's full INFO stream is intentionally disabled.
        startInfo.ArgumentList.Add("--console-log-level");
        startInfo.ArgumentList.Add("warn");
        startInfo.ArgumentList.Add("--file-log-level");
        startInfo.ArgumentList.Add("off");

        lock (_lifecycleSync)
        {
            lock (_sync)
            {
                if (_process != null)
                {
                    throw new InvalidOperationException(
                        "EasyTier supervisor already tracks a core process; stop it before starting another one.");
                }
            }

            IEasyTierCoreProcess process = _factory.Start(startInfo);
            long generation;
            lock (_sync)
            {
                _process = process;
                generation = ++_generation;
            }

            try
            {
                process.BeginRead((line, isError) =>
                {
                    lock (_sync)
                    {
                        if (_generation != generation || !ReferenceEquals(_process, process))
                            return;
                        _activeCallbacks++;
                        _callbacksDrained.Reset();
                    }

                    try
                    {
                        // Do not invoke product code while holding the supervisor state lock.
                        lineObserver(line, isError);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _activeCallbacks--;
                            if (_activeCallbacks == 0)
                                _callbacksDrained.Set();
                        }
                    }
                });
                return process;
            }
            catch
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                        _generation++;
                    }
                }
                _callbacksDrained.Wait();
                TryTerminateBounded(process);
                throw;
            }
        }
    }

    public bool TryGetExitCode(out int exitCode)
    {
        lock (_sync)
        {
            try
            {
                if (_process is { HasExited: true } process)
                {
                    exitCode = process.ExitCode;
                    return true;
                }
            }
            catch
            {
                // Treat an inaccessible/disposed handle as not reportable here.
            }
        }
        exitCode = 0;
        return false;
    }

    public void Stop(string reason)
    {
        lock (_lifecycleSync)
        {
            IEasyTierCoreProcess? process;
            lock (_sync)
            {
                process = _process;
                _process = null;
                _generation++;
            }

            // Generation is invalid before waiting. A callback that already acquired a lease
            // must finish; a later callback observes the new generation and never enters the observer.
            _callbacksDrained.Wait();
            if (process == null)
                return;
            try
            {
                if (!process.HasExited)
                {
                    LogService.Info($"Stopping EasyTier, reason={reason}, pid={process.Id}");
                    process.KillTree();
                    process.WaitForExit(500);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to stop EasyTier: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryTerminateBounded(IEasyTierCoreProcess process)
    {
        try
        {
            try { process.KillTree(); }
            catch (Exception ex)
            {
                LogService.Info(
                    $"EasyTier failed-start cleanup: kill failed ({ex.GetType().Name}).");
            }
            try
            {
                if (!process.WaitForExit(500))
                    LogService.Info("EasyTier failed-start cleanup: process did not exit within 500ms.");
            }
            catch (Exception ex)
            {
                LogService.Info(
                    $"EasyTier failed-start cleanup: bounded wait failed ({ex.GetType().Name}).");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public static async Task<FileStream> AcquireStartupLockAsync(TimeSpan timeout)
    {
        string directory = LogService.NetworkDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "easytier-start.lock");
        DateTime deadline = DateTime.UtcNow
            + (timeout < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : timeout);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception)
            {
                last = exception;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        throw new IOException("另一个启动器正在启动 EasyTier，等待启动锁超时。", last);
    }

    public static async Task EnsureRpcPortalReleasedAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            HashSet<int> processIds = GetTcpListenerProcessIds(PublicTunnelConfig.EasyTierRpcPort);
            if (processIds.Count == 0)
                return;

            foreach (int processId in processIds)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    if (!process.ProcessName.Equals("easytier-core", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"EasyTier RPC端口 {PublicTunnelConfig.EasyTierRpcPort} 被其他进程占用：{process.ProcessName} PID={processId}。");
                    }
                    LogService.Warning(
                        $"Releasing stale SCBL EasyTier RPC listener. PID={processId}, port={PublicTunnelConfig.EasyTierRpcPort}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1200);
                }
                catch (ArgumentException)
                {
                    // Process already exited.
                }
            }
            await Task.Delay(180).ConfigureAwait(false);
        }

        string owners = string.Join(",", GetTcpListenerProcessIds(PublicTunnelConfig.EasyTierRpcPort));
        throw new IOException(
            $"EasyTier RPC端口 {PublicTunnelConfig.EasyTierRpcPort} 未释放，PID={owners}。无需重启电脑，请关闭残留EasyTier后重试。");
    }

    public static bool HasOwnedProcess()
    {
        List<Process> processes = FindOwnedProcesses();
        try
        {
            return processes.Any(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            });
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    public static void StopAllOwnedProcesses(string reason)
    {
        LogService.Info($"Stopping packaged EasyTier processes, reason={reason}");
        foreach (Process process in FindOwnedProcesses())
        {
            try
            {
                LogService.Info($"Killing packaged EasyTier PID={process.Id}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to stop EasyTier PID={process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static List<Process> FindOwnedProcesses()
    {
        HashSet<int> rpcOwners = GetTcpListenerProcessIds(PublicTunnelConfig.EasyTierRpcPort);
        Process[] processes;
        try { processes = Process.GetProcessesByName("easytier-core"); }
        catch { return new List<Process>(); }

        var ownedProcesses = new List<Process>();
        foreach (Process process in processes)
        {
            bool owned = rpcOwners.Contains(process.Id);
            try
            {
                owned = owned || EasyTierOwnedProcessPolicy.IsScblPackagedPath(
                    process.MainModule?.FileName,
                    AppContext.BaseDirectory);
            }
            catch
            {
                // A process owning the fixed SCBL RPC portal is ours even when its path cannot be read.
            }

            if (owned)
                ownedProcesses.Add(process);
            else
                process.Dispose();
        }
        return ownedProcesses;
    }

    internal static HashSet<int> GetTcpListenerProcessIds(
        int port,
        IEasyTierNetstatCommandRunner? runner = null)
    {
        try
        {
            EasyTierNetstatCommandResult result = (runner
                    ?? new SystemEasyTierNetstatCommandRunner())
                .Run(TimeSpan.FromMilliseconds(2500));
            if (!result.Completed)
                return new HashSet<int>();
            return EasyTierNetstatParser.ParseTcpListenerProcessIds(
                result.StandardOutput,
                port);
        }
        catch (Exception ex)
        {
            LogService.Info("RPC listener PID scan skipped: " + ex.Message);
            return new HashSet<int>();
        }
    }
}

internal static class EasyTierOwnedProcessPolicy
{
    public static bool IsScblPackagedPath(string? path, string launcherBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string expected = ClientInstallLayout.Resolve(
                launcherBaseDirectory,
                ClientInstallLayout.EasyTierCoreRelativePath);
            if (fullPath.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;

            string normalized = fullPath.Replace('/', '\\');
            return normalized.Contains("\\SCBL_Public\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\SCBL_Public_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

internal static class EasyTierNetstatParser
{
    public static HashSet<int> ParseTcpListenerProcessIds(string output, int port)
    {
        var result = new HashSet<int>();
        if (port is <= 0 or > 65535 || string.IsNullOrWhiteSpace(output))
            return result;

        foreach (string raw in output.Split(
                     new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] parts = Regex.Split(line, @"\s+");
            if (parts.Length < 5
                || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                || !parts[1].EndsWith(":" + port, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (int.TryParse(parts[^1], out int processId) && processId > 0)
                result.Add(processId);
        }
        return result;
    }
}
