using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class RestartCoordinator
{
    private const int ParentExitTimeoutMs = 60000;
    private const int LaunchAttempts = 20;
    private const int LaunchRetryDelayMs = 500;
    private const int LaunchSurvivalCheckMs = 3000;
    private const int HelperHandoffTimeoutMs = 10000;
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbTopmost = 0x00040000;
    private const string HandoffEventPrefix = @"Local\SCBL_Updater_Restart_";
    private static string? _targetDirectoryForLog;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string text,
        string caption,
        uint type);

    public static int RunHelper(
        string? target,
        string? restart,
        string? waitPidText,
        string? handoffEventName,
        IReadOnlyCollection<string>? launcherArguments = null)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            _targetDirectoryForLog = targetDirectory;
            string launcher = ResolveLauncherPath(targetDirectory, restart);
            SignalHandoff(handoffEventName);
            if (int.TryParse(waitPidText, out int waitPid) && waitPid > 0)
                WaitForParentExit(waitPid);

            Log($"Restart helper is launching the client. target={targetDirectory}, launcher={launcher}");
            if (LaunchWithRetry(launcher, targetDirectory, launcherArguments))
                return 0;

            ReportFailure(targetDirectory);
            return 8;
        }
        catch (Exception ex)
        {
            Log("Restart helper failed: " + ex);
            return 8;
        }
    }

    public static bool ScheduleAfterUpdaterExit(string target, string? restart, IReadOnlyCollection<string>? launcherArguments = null)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            _targetDirectoryForLog = targetDirectory;
            string launcher = ResolveLauncherPath(targetDirectory, restart);
            string updater = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(updater) || !File.Exists(updater))
            {
                Log("Cannot schedule launcher restart because the updater path is unavailable.");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = updater,
                WorkingDirectory = Path.GetDirectoryName(updater) ?? targetDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--restart-helper");
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--target");
            psi.ArgumentList.Add(targetDirectory);
            psi.ArgumentList.Add("--restart");
            psi.ArgumentList.Add(launcher);
            string handoffEventName = HandoffEventPrefix + Guid.NewGuid().ToString("N");
            using var handoff = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                handoffEventName,
                out bool createdNew);
            if (!createdNew)
                throw new InvalidOperationException("The restart handoff event already exists.");
            psi.ArgumentList.Add("--handoff-event");
            psi.ArgumentList.Add(handoffEventName);
            foreach (string argument in launcherArguments ?? Array.Empty<string>())
            {
                psi.ArgumentList.Add("--launcher-argument");
                psi.ArgumentList.Add(argument);
            }

            using Process? helper = Process.Start(psi);
            if (helper == null)
            {
                Log("Process.Start returned null while scheduling the launcher restart helper.");
                return false;
            }

            if (!WaitForHandoff(helper, handoff))
            {
                Log(
                    $"Launcher restart helper did not confirm handoff. helperPid={helper.Id}, " +
                    $"waitForUpdaterPid={Environment.ProcessId}");
                StopUnconfirmedHelper(helper);
                return false;
            }

            Log($"Launcher restart helper accepted handoff. helperPid={helper.Id}, waitForUpdaterPid={Environment.ProcessId}, launcher={launcher}");
            return true;
        }
        catch (Exception ex)
        {
            Log("Launcher restart helper scheduling failed: " + ex);
            return false;
        }
    }

    public static bool LaunchImmediately(string target, string? restart, IReadOnlyCollection<string>? launcherArguments = null)
    {
        try
        {
            string targetDirectory = NormalizeTargetDirectory(target);
            _targetDirectoryForLog = targetDirectory;
            string launcher = ResolveLauncherPath(targetDirectory, restart);
            return LaunchWithRetry(launcher, targetDirectory, launcherArguments);
        }
        catch (Exception ex)
        {
            Log("Immediate launcher restart failed: " + ex);
            return false;
        }
    }

    public static void ReportFailure(string target)
    {
        try
        {
            _targetDirectoryForLog = NormalizeTargetDirectory(target);
        }
        catch
        {
            // The visible failure must not depend on log-path normalization.
        }

        Log("Automatic Launcher restart failed and requires manual action.");
        try
        {
            MessageBoxW(
                IntPtr.Zero,
                "更新已经处理，但启动器未能自动重新打开。请手动运行 SCBL.exe。\n\n" +
                "The update was processed, but Launcher could not reopen automatically. Please start SCBL.exe manually.",
                "SCBL Updater",
                MbOk | MbIconError | MbTopmost);
        }
        catch (Exception ex)
        {
            Log("Restart failure notification could not be displayed: " + ex.Message);
        }
    }

    private static bool WaitForHandoff(Process helper, EventWaitHandle handoff)
    {
        using var exited = new ManualResetEvent(false);
        EventHandler handler = (_, _) => exited.Set();
        helper.EnableRaisingEvents = true;
        helper.Exited += handler;
        try
        {
            if (helper.HasExited)
                exited.Set();
            return WaitForHandoffSignal(
                handoff,
                exited,
                HelperHandoffTimeoutMs);
        }
        finally
        {
            helper.Exited -= handler;
        }
    }

    internal static bool WaitForHandoffSignal(
        WaitHandle handoff,
        WaitHandle helperExited,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(helperExited);
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        return WaitHandle.WaitAny(
            new[] { handoff, helperExited },
            timeoutMs) == 0;
    }

    private static void StopUnconfirmedHelper(Process helper)
    {
        try
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                helper.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log("Unconfirmed restart helper cleanup failed: " + ex.Message);
        }
    }

    private static void SignalHandoff(string? handoffEventName)
    {
        if (string.IsNullOrWhiteSpace(handoffEventName))
        {
            Log("Restart helper is running without a handoff event for backward compatibility.");
            return;
        }

        if (!handoffEventName.StartsWith(HandoffEventPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(handoffEventName[HandoffEventPrefix.Length..], "N", out _))
        {
            throw new InvalidDataException("The restart handoff event name is invalid.");
        }

        using EventWaitHandle handoff = EventWaitHandle.OpenExisting(handoffEventName);
        handoff.Set();
        Log("Restart helper confirmed handoff before waiting for the updater to exit.");
    }

    private static string NormalizeTargetDirectory(string? target)
    {
        string value = string.IsNullOrWhiteSpace(target) ? AppContext.BaseDirectory : target;
        return Path.GetFullPath(value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveLauncherPath(string target, string? requestedRestart)
    {
        string canonical = Path.Combine(target, "SCBL.exe");
        if (File.Exists(canonical))
            return canonical;

        if (!string.IsNullOrWhiteSpace(requestedRestart))
        {
            string candidate = Path.GetFullPath(requestedRestart);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("The updated launcher executable was not found.", canonical);
    }

    private static void WaitForParentExit(int pid)
    {
        try
        {
            using Process parent = Process.GetProcessById(pid);
            if (!parent.WaitForExit(ParentExitTimeoutMs))
                Log($"Restart helper timed out waiting for updater PID {pid}; continuing with launcher retries.");
            else
                Log($"Updater PID {pid} exited; starting the updated launcher.");
        }
        catch (ArgumentException)
        {
            Log($"Updater PID {pid} had already exited before the restart helper attached.");
        }
        catch (Exception ex)
        {
            Log($"Waiting for updater PID {pid} failed: {ex.Message}; continuing with launcher retries.");
        }
    }

    private static bool LaunchWithRetry(
        string launcher,
        string workingDirectory,
        IReadOnlyCollection<string>? arguments = null)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= LaunchAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(launcher))
                    throw new FileNotFoundException("Launcher executable is not available yet.", launcher);

                var psi = new ProcessStartInfo
                {
                    FileName = launcher,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                foreach (string argument in arguments ?? Array.Empty<string>())
                    psi.ArgumentList.Add(argument);
                using Process? process = Process.Start(psi);
                if (process == null)
                    throw new InvalidOperationException("Process.Start returned null for the launcher.");

                if (process.WaitForExit(LaunchSurvivalCheckMs))
                    throw new InvalidOperationException($"Updated launcher exited during startup. exitCode={process.ExitCode}");

                Log($"Updated launcher remained running after {LaunchSurvivalCheckMs}ms on attempt {attempt}. pid={process.Id}, path={launcher}");
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"Launcher restart attempt {attempt}/{LaunchAttempts} failed: {ex.Message}");
                if (attempt < LaunchAttempts)
                    Thread.Sleep(LaunchRetryDelayMs);
            }
        }

        Log("Launcher restart retries exhausted: " + (lastError?.ToString() ?? "unknown error"));
        return false;
    }

    private static void Log(string message)
    {
        try
        {
            string target = _targetDirectoryForLog ?? NormalizeTargetDirectory(null);
            string logDirectory = Path.Combine(
                UpdaterPaths.GetClientDataDirectory(target),
                "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "updater.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Restart must not fail only because logging is unavailable.
        }
    }
}
