using System.Diagnostics;

namespace Scbl.Server.Update;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ServerPaths? paths = null;
        try
        {
            Dictionary<string, string> options = Parse(args);
            await WaitForProcessAsync(options).ConfigureAwait(false);
            paths = new(options.GetValueOrDefault("program-root"), options.GetValueOrDefault("data-root"));
            if (options.TryGetValue("uninstall", out string? uninstallMode))
            {
                await UninstallAsync(paths, uninstallMode).ConfigureAwait(false);
                return 0;
            }
            string packagePath = Require(options, "package");
            paths.EnsureLayout();
            ServerLog log = new(paths, "updater");
            BackupManager backup = new(paths, log);
            PackageDeployment deployment = new(paths, log);
            bool serviceExisted = await WindowsServiceInstaller.ExistsAsync().ConfigureAwait(false);
            ServiceState serviceBefore = serviceExisted ? await WindowsServiceInstaller.QueryStateAsync().ConfigureAwait(false) : ServiceState.Missing;
            bool wasRunning = serviceBefore is not (ServiceState.Missing or ServiceState.Stopped);
            bool shouldStart = !serviceExisted || wasRunning;
            FileStateSnapshot[] stateBefore =
            [
                FileStateSnapshot.Capture(paths.CurrentReleasePointer),
                FileStateSnapshot.Capture(paths.PreviousReleasePointer),
            ];
            string backupPath = "";
            string? bootstrapSnapshot = null;
            try
            {
                // Validation and extraction do not mutate the installed server. Complete
                // them before stopping a healthy service so an invalid ZIP cannot cause
                // avoidable downtime or create an unnecessary backup.
                using StagedPackage package = deployment.Stage(packagePath);
                if (wasRunning) await WindowsServiceInstaller.StopAsync().ConfigureAwait(false);
                backupPath = backup.Create();
                if (package.Manifest.Components.Any(component => component.Target == "bootstrap"))
                {
                    bootstrapSnapshot = deployment.SnapshotBootstrap();
                    deployment.InstallBootstrap(package);
                }
                if (package.Manifest.Components.Any(component => component.Target == "runtime")) deployment.ActivateRuntime(package);
                ServerConfiguration activeConfig = ServerConfiguration.LoadOrCreate(paths);
                await FirewallManager.ApplyAsync(activeConfig).ConfigureAwait(false);
                string host = Path.Combine(paths.Bootstrap, "SCBL.Server.Host.exe");
                await WindowsServiceInstaller.InstallAsync(host, activeConfig.StartAutomatically).ConfigureAwait(false);
                paths.EnsureLayout();
                if (shouldStart) await WindowsServiceInstaller.StartAsync().ConfigureAwait(false);
                PackageDeployment.DiscardBootstrapSnapshot(bootstrapSnapshot);
                AtomicJson.Write(Path.Combine(paths.DataRoot, "state", "last-update.json"), new UpdateReceipt(true, package.Manifest.ReleaseVersion, packagePath, backupPath, DateTimeOffset.UtcNow, null));
                RelaunchManager(paths);
                return 0;
            }
            catch (Exception error)
            {
                List<Exception> rollbackErrors = [];
                try { deployment.RestoreBootstrap(bootstrapSnapshot); } catch (Exception rollback) { rollbackErrors.Add(rollback); }
                foreach (FileStateSnapshot snapshot in stateBefore)
                {
                    try { snapshot.Restore(); } catch (Exception rollback) { rollbackErrors.Add(rollback); }
                }
                try { await FirewallManager.ApplyAsync(ServerConfiguration.LoadOrCreate(paths)).ConfigureAwait(false); }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
                try
                {
                    if (serviceExisted)
                    {
                        string restoredHost = Path.Combine(paths.Bootstrap, "SCBL.Server.Host.exe");
                        await WindowsServiceInstaller.InstallAsync(restoredHost, ServerConfiguration.LoadOrCreate(paths).StartAutomatically).ConfigureAwait(false);
                        if (wasRunning) await WindowsServiceInstaller.StartAsync().ConfigureAwait(false);
                    }
                    else if (await WindowsServiceInstaller.ExistsAsync().ConfigureAwait(false))
                    {
                        await WindowsServiceInstaller.UninstallAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
                string restoredVersion = AtomicJson.Read<ReleasePointer>(paths.CurrentReleasePointer)?.Version ?? "unknown";
                AtomicJson.Write(Path.Combine(paths.DataRoot, "state", "last-update.json"), new UpdateReceipt(false, restoredVersion, packagePath, backupPath, DateTimeOffset.UtcNow, error.Message));
                if (rollbackErrors.Count != 0) throw new AggregateException("更新失败且回滚未完全成功。", [error, .. rollbackErrors]);
                throw;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (paths is not null)
            {
                try { RelaunchManager(paths); }
                catch (Exception relaunchError) { Console.Error.WriteLine($"无法重新打开管理器：{relaunchError.Message}"); }
            }
            return 1;
        }
    }

    private static void RelaunchManager(ServerPaths paths)
    {
        string manager = Path.Combine(paths.Bootstrap, "SCBL.Server.exe");
        if (!File.Exists(manager)) throw new FileNotFoundException("更新结束后找不到 SCBL 管理器。", manager);
        _ = Process.Start(new ProcessStartInfo(manager) { UseShellExecute = true })
            ?? throw new InvalidOperationException("更新结束后无法重新打开 SCBL 管理器。");
    }

    private static async Task WaitForProcessAsync(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("wait-pid", out string? processId) || !int.TryParse(processId, out int pid)) return;
        try
        {
            using Process process = Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (ArgumentException) { }
    }

    private static async Task UninstallAsync(ServerPaths paths, string mode)
    {
        if (mode is not ("keep-data" or "remove-data")) throw new ArgumentException("未知卸载方式。");
        ValidateRemovalRoot(paths.ProgramRoot);
        if (mode == "remove-data") ValidateRemovalRoot(paths.DataRoot);
        await WindowsServiceInstaller.UninstallAsync().ConfigureAwait(false);
        await FirewallManager.RemoveAsync().ConfigureAwait(false);
        if (Directory.Exists(paths.ProgramRoot)) Directory.Delete(paths.ProgramRoot, recursive: true);
        if (mode == "remove-data" && Directory.Exists(paths.DataRoot)) Directory.Delete(paths.DataRoot, recursive: true);
        Console.WriteLine(mode == "remove-data" ? "SCBL 服务端及数据已删除。" : $"SCBL 服务端已卸载；数据保留在 {paths.DataRoot}");
    }

    private static void ValidateRemovalRoot(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) || Directory.GetParent(full) is null)
        {
            throw new InvalidOperationException($"拒绝删除不安全的目录：{full}");
        }
        string[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        if (protectedRoots.Any(item => !string.IsNullOrWhiteSpace(item) && string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(item)), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"拒绝删除系统目录：{full}");
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) throw new ArgumentException("参数必须使用 --name value。");
            result.Add(args[index][2..], args[index + 1]);
        }
        return result;
    }

    private static string Require(Dictionary<string, string> options, string name) => options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"缺少 --{name}。");
}

public sealed record UpdateReceipt(bool Success, string Version, string Package, string Backup, DateTimeOffset CompletedAtUtc, string? Error);
