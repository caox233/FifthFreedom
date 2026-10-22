using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.ApplicationLayer;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace SplinterCellCNLauncher.Services;

public sealed class LocalClientUpdateService
{
    private readonly LauncherRuntimeContext _runtimeContext;

    public LocalClientUpdateService(LauncherRuntimeContext runtimeContext)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
    }

    private const int RestartHandoffTimeoutMs = 10000;
    private const string RestartHandoffEventPrefix = @"Local\SCBL_Updater_Restart_";

    public sealed class UpdatePackageInfo
    {
        public string PackagePath { get; init; } = "";
        public string Version { get; init; } = "";
        public string PackageType { get; init; } = "client_update";
        public string[] ReleaseNotes { get; init; } = Array.Empty<string>();
        public DateTime LastWriteTimeUtc { get; init; }
    }

    public void StartUpdater(UpdatePackageInfo package, int launcherPid)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string updater = PrepareUpdaterRunner(baseDir, LogService.UpdatesDirectory);

        string launcherExe = Path.Combine(baseDir, "SCBL.exe");
        if (!File.Exists(launcherExe))
            launcherExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? launcherExe;
        string restartArguments = string.Concat(
            GetLauncherRestartArguments(_runtimeContext.UpdateChannel)
                .Select(argument => $" --launcher-argument {Quote(argument)}"));
        var psi = new ProcessStartInfo
        {
            FileName = updater,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = baseDir,
            Arguments = $"--package {Quote(package.PackagePath)} --package-type {Quote(package.PackageType)} --version {Quote(package.Version)} --target {Quote(baseDir)} --pid {launcherPid} --restart {Quote(launcherExe)}{restartArguments}"
        };
        Process.Start(psi);
    }

    public static bool IsLauncherReplacementRequired(VerifiedClientComponent component)
    {
        if (!component.Name.Equals(ClientComponentUpdateService.LauncherComponentName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The component is not a Launcher component.", nameof(component));
        string current = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "SCBL.exe");
        if (!File.Exists(current))
            return true;
        using FileStream stream = File.OpenRead(current);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        return !hash.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static UpdatePackageInfo CreateLauncherComponentPackage(VerifiedClientComponent component)
    {
        if (!IsLauncherReplacementRequired(component))
            throw new InvalidOperationException("The installed Launcher already matches the component.");

        string directory = Path.Combine(LogService.UpdatesDirectory, "component-packages");
        Directory.CreateDirectory(directory);
        string package = Path.Combine(directory, $"launcher-{component.Version}-{Guid.NewGuid():N}.zip");
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("SCBL.exe", CompressionLevel.Optimal);
            using Stream input = File.OpenRead(component.FilePath);
            using Stream output = entry.Open();
            input.CopyTo(output);
        }
        return new UpdatePackageInfo
        {
            PackagePath = package,
            Version = component.Version,
            PackageType = "launcher-component",
            ReleaseNotes = Array.Empty<string>(),
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(package)
        };
    }

    public bool ScheduleLauncherRestartAfterExit(int launcherPid)
        => ScheduleLauncherRestartAfterExit(launcherPid, _runtimeContext.UpdateChannel);

    public bool ScheduleLauncherRestartAfterExit(
        int launcherPid,
        ClientUpdateChannel channel)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string launcher = Path.Combine(baseDir, "SCBL.exe");
        if (!File.Exists(GetCanonicalUpdaterPath(baseDir)) || !File.Exists(launcher))
            return false;

        string updater;
        try
        {
            updater = PrepareUpdaterRunner(baseDir, LogService.UpdatesDirectory);
        }
        catch (Exception ex)
        {
            LogService.Error("Unable to prepare restart helper: " + ex.Message);
            return false;
        }

        string handoffEventName = RestartHandoffEventPrefix + Guid.NewGuid().ToString("N");
        using var handoff = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            handoffEventName,
            out bool createdNew);
        if (!createdNew)
        {
            LogService.Error("Unable to schedule restart helper because its handoff event already exists.");
            return false;
        }
        ProcessStartInfo psi = CreateRestartHelperStartInfo(
            updater,
            baseDir,
            launcher,
            launcherPid,
            handoffEventName,
            channel);
        using Process? helper = Process.Start(psi);
        if (helper == null)
            return false;
        if (!WaitForRestartHandoff(helper, handoff))
        {
            LogService.Error(
                $"Launcher restart helper did not confirm handoff: helperPid={helper.Id}, waitPid={launcherPid}");
            StopUnconfirmedRestartHelper(helper);
            return false;
        }
        LogService.Info(
            $"Launcher restart helper confirmed handoff: helperPid={helper.Id}, waitPid={launcherPid}");
        return true;
    }

    internal static ProcessStartInfo CreateRestartHelperStartInfo(
        string updater,
        string baseDir,
        string launcher,
        int launcherPid,
        string handoffEventName,
        ClientUpdateChannel channel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = updater,
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--restart-helper");
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(launcherPid.ToString());
        psi.ArgumentList.Add("--target");
        psi.ArgumentList.Add(baseDir);
        psi.ArgumentList.Add("--restart");
        psi.ArgumentList.Add(launcher);
        psi.ArgumentList.Add("--handoff-event");
        psi.ArgumentList.Add(handoffEventName);
        foreach (string argument in GetLauncherRestartArguments(channel))
        {
            psi.ArgumentList.Add("--launcher-argument");
            psi.ArgumentList.Add(argument);
        }
        return psi;
    }

    private static bool WaitForRestartHandoff(Process helper, EventWaitHandle handoff)
    {
        using var exited = new ManualResetEvent(false);
        EventHandler handler = (_, _) => exited.Set();
        helper.EnableRaisingEvents = true;
        helper.Exited += handler;
        try
        {
            if (helper.HasExited)
                exited.Set();
            return WaitForRestartHandoffSignal(handoff, exited, RestartHandoffTimeoutMs);
        }
        finally
        {
            helper.Exited -= handler;
        }
    }

    internal static bool WaitForRestartHandoffSignal(
        WaitHandle handoff,
        WaitHandle helperExited,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(helperExited);
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        return WaitHandle.WaitAny([handoff, helperExited], timeoutMs) == 0;
    }

    private static void StopUnconfirmedRestartHelper(Process helper)
    {
        try
        {
            if (helper.HasExited)
                return;
            helper.Kill(entireProcessTree: true);
            helper.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            LogService.Warning("Unable to stop unconfirmed restart helper: " + ex.Message);
        }
    }

    internal static string[] GetLauncherRestartArguments(ClientUpdateChannel channel)
        => ClientUpdateChannelArguments.Create(channel);

    internal static string PrepareUpdaterRunner(string baseDir, string updatesDirectory)
    {
        string canonical = GetCanonicalUpdaterPath(baseDir);
        if (!File.Exists(canonical))
            throw new FileNotFoundException("没有找到客户端更新程序。", canonical);

        string runnerDirectory = Path.Combine(updatesDirectory, "runner", Guid.NewGuid().ToString("N"));
        string runner = Path.Combine(runnerDirectory, "SCBL.Updater.exe");
        Directory.CreateDirectory(runnerDirectory);
        try
        {
            File.Copy(canonical, runner, overwrite: false);
            if (!FilesAreIdentical(canonical, runner))
                throw new IOException("临时更新程序复制后校验失败。");
            return runner;
        }
        catch
        {
            try { Directory.Delete(runnerDirectory, recursive: true); } catch { }
            throw;
        }
    }

    internal static string GetCanonicalUpdaterPath(string baseDir)
        => ClientInstallLayout.Resolve(baseDir, ClientInstallLayout.UpdaterRelativePath);

    private static bool FilesAreIdentical(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length)
            return false;
        using FileStream firstStream = File.OpenRead(first);
        using FileStream secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).AsSpan().SequenceEqual(SHA256.HashData(secondStream));
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
