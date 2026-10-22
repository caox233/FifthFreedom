using Microsoft.Win32;
using System.Diagnostics;

internal sealed class WindowsUpdaterProcessBoundary : IUpdaterProcessBoundary
{
    private const int EasyTierRpcPort = 15966;

    public void WaitForProcessExit(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The final bounded wait below determines whether update can continue.
                }

                if (!process.WaitForExit(10_000))
                    throw new IOException($"Launcher process {pid} did not exit before update.");
            }
        }
        catch (ArgumentException)
        {
            // The launcher may already have exited.
        }
    }

    public bool IsGameRunning()
    {
        foreach (string name in new[] { "Blacklist_game", "Blacklist_DX11_game" })
        {
            Process[] processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                    return true;
            }
            catch
            {
                // Detection uncertainty is treated as running so no runtime is stopped.
                return true;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        return false;
    }

    public void StopRuntimeProcessesForUpdate(string target)
    {
        string targetRoot = UpdaterPaths.NormalizeTarget(target)
            + Path.DirectorySeparatorChar;
        HashSet<int> rpcOwners = GetTcpListenerPids(EasyTierRpcPort);
        var failures = new List<string>();

        foreach (string name in new[]
                 {
                     "scbl-process-router",
                     "easytier-core",
                     "scbl-tunnel-client"
                 })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception exception)
            {
                failures.Add($"scan {name}: {exception.Message}");
                continue;
            }

            foreach (Process process in processes)
            {
                try
                {
                    string? executablePath = null;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Existing scope rules use RPC ownership when the path is unavailable.
                    }

                    if (name.Equals("easytier-core", StringComparison.OrdinalIgnoreCase)
                        && !rpcOwners.Contains(process.Id)
                        && !IsScblPackagedRuntimePath(executablePath, targetRoot))
                    {
                        UpdaterLog.Write(
                            $"Skip unrelated EasyTier process before update: PID={process.Id}, path={executablePath ?? "unknown"}");
                        continue;
                    }

                    UpdaterLog.Write(
                        $"Stopping runtime process before update: {name} PID={process.Id}");
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5_000))
                        failures.Add($"{name} PID={process.Id} did not exit");
                }
                catch (Exception exception)
                {
                    failures.Add($"{name} PID={process.Id}: {exception.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && GetTcpListenerPids(EasyTierRpcPort).Count > 0)
        {
            Thread.Sleep(150);
        }

        HashSet<int> remaining = GetTcpListenerPids(EasyTierRpcPort);
        if (remaining.Count > 0)
        {
            failures.Add(
                $"EasyTier RPC port {EasyTierRpcPort} remains occupied by PID(s): {string.Join(",", remaining)}");
        }

        try
        {
            ReleaseWinDivertDriverServices(target);
        }
        catch (Exception exception)
        {
            failures.Add(exception.Message);
        }

        if (failures.Count > 0)
        {
            throw new IOException(
                "Runtime processes could not be safely stopped: "
                + string.Join("; ", failures));
        }
    }

    private static HashSet<int> GetTcpListenerPids(int port)
    {
        var result = new HashSet<int>();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(startInfo);
            if (process == null)
                return result;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2_500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                return result;
            }

            foreach (string raw in output.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5
                    || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                    || !parts[1].EndsWith(":" + port, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(parts[^1], out int pid) && pid > 0)
                    result.Add(pid);
            }
        }
        catch (Exception exception)
        {
            UpdaterLog.Write("RPC listener scan skipped: " + exception.Message);
        }
        return result;
    }

    private static bool IsScblPackagedRuntimePath(
        string? executablePath,
        string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        try
        {
            string fullPath = Path.GetFullPath(executablePath);
            if (fullPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
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

    private static void ReleaseWinDivertDriverServices(string target)
    {
        string expectedDriver = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SCBL",
            "RouteGuard",
            "WinDivert64.sys"));
        foreach (string serviceName in FindDriverServices(expectedDriver))
        {
            UpdaterLog.Write(
                $"Stopping WinDivert driver service before update: {serviceName}");
            RunScCommand("stop", serviceName);
            Thread.Sleep(300);
            RunScCommand("delete", serviceName);
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && IsFileExclusivelyLocked(expectedDriver))
            Thread.Sleep(200);

        if (IsFileExclusivelyLocked(expectedDriver))
        {
            throw new IOException(
                "WinDivert64.sys remains locked after runtime and driver-service shutdown.");
        }
        UpdaterLog.Write("WinDivert driver file is released for update.");
    }

    private static IReadOnlyList<string> FindDriverServices(string expectedDriver)
    {
        var result = new List<string>();
        RegistryKey? services;
        try
        {
            services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
        }
        catch (Exception exception)
        {
            UpdaterLog.Write("Driver service registry scan skipped: " + exception.Message);
            return result;
        }

        using (services)
        {
            if (services == null)
                return result;

            string[] serviceNames;
            try
            {
                serviceNames = services.GetSubKeyNames();
            }
            catch (Exception exception)
            {
                UpdaterLog.Write("Driver service registry enumeration skipped: " + exception.Message);
                return result;
            }

            foreach (string name in serviceNames)
            {
                try
                {
                    using RegistryKey? service = services.OpenSubKey(name);
                    string imagePath = Convert.ToString(service?.GetValue("ImagePath")) ?? "";
                    string normalized = NormalizeServiceImagePath(imagePath);
                    if (!string.IsNullOrWhiteSpace(normalized)
                        && normalized.Equals(expectedDriver, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(name);
                    }
                }
                catch
                {
                    // Ignore unrelated or protected service keys.
                }
            }
        }
        return result;
    }

    private static string NormalizeServiceImagePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
            value = value[4..];
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            value = value[4..];
        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                value[12..]);
        }
        if (!Path.IsPathRooted(value))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                value);
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return "";
        }
    }

    private static void RunScCommand(string action, string serviceName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(action);
        startInfo.ArgumentList.Add(serviceName);
        using Process? process = Process.Start(startInfo);
        if (process == null)
            throw new IOException($"sc.exe {action} {serviceName} did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw new IOException($"sc.exe {action} {serviceName} timed out.");
        }
        UpdaterLog.Write(
            $"sc.exe {action} {serviceName} exit={process.ExitCode}: {(output + " " + error).Trim()}");
    }

    private static bool IsFileExclusivelyLocked(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
