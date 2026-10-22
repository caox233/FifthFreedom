using System.Diagnostics;

namespace SplinterCellCNLauncher.Services;

public sealed class LauncherProcessCleanupService
{
    public void CloseOriginalLaunchers(string reason)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == current.Id || !LooksLikeOriginalLauncher(process))
                        continue;
                    LogService.Info(
                        $"Detected original 5th Echelon launcher, PID={process.Id}, reason={reason}");
                    if (process.MainWindowHandle != IntPtr.Zero
                        && process.CloseMainWindow()
                        && process.WaitForExit(2000))
                    {
                        continue;
                    }
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error($"Original launcher process check failed: {ex.Message}");
                }
                finally { process.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to scan original launcher processes: {ex.Message}");
        }
    }

    internal static bool LooksLikeOriginalLauncher(Process process)
    {
        string text = "";
        try { text += process.ProcessName + " "; } catch { }
        try { text += process.MainWindowTitle + " "; } catch { }
        try
        {
            string path = process.MainModule?.FileName ?? "";
            if (!string.IsNullOrWhiteSpace(path))
            {
                text += path + " " + Path.GetFileNameWithoutExtension(path) + " ";
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                text += info.FileDescription + " " + info.ProductName + " " + info.OriginalFilename;
            }
        }
        catch { }

        string processName = "";
        try { processName = process.ProcessName; } catch { }
        if (new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" }
            .Any(candidate => processName.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            return false;

        return (text.Contains("github.com/unixoide/5th-echelon", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unixoide/5th-echelon", StringComparison.OrdinalIgnoreCase)
                || text.Contains("5th-echelon", StringComparison.OrdinalIgnoreCase)
                || text.Contains("5th Echelon", StringComparison.OrdinalIgnoreCase))
            && !text.Contains("SplinterCellCNLauncher", StringComparison.OrdinalIgnoreCase)
            && !processName.Equals("SCBL", StringComparison.OrdinalIgnoreCase);
    }
}
