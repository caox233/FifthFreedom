namespace Scbl.Server.Manager;

public static class FirstInstall
{
    public static async Task InstallAsync(ServerPaths paths, ServerConfiguration config, string packagePath, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        if (!WindowsServiceInstaller.IsAdministrator()) throw new UnauthorizedAccessException("首次安装需要管理员权限。");
        paths.EnsureLayout();
        ServerLog log = new(paths, "installer");
        PackageDeployment deployment = new(paths, log);
        bool serviceExisted = await WindowsServiceInstaller.ExistsAsync(cancellationToken).ConfigureAwait(false);
        ServiceState serviceBefore = serviceExisted
            ? await WindowsServiceInstaller.QueryStateAsync(cancellationToken).ConfigureAwait(false)
            : ServiceState.Missing;
        bool wasRunning = serviceBefore is not (ServiceState.Missing or ServiceState.Stopped);
        ServerConfiguration? previousConfig = AtomicJson.Read<ServerConfiguration>(paths.Config);
        FileStateSnapshot[] stateBefore =
        [
            FileStateSnapshot.Capture(paths.Config),
            FileStateSnapshot.Capture(paths.Secrets),
            FileStateSnapshot.Capture(paths.CurrentReleasePointer),
            FileStateSnapshot.Capture(paths.PreviousReleasePointer),
        ];
        string? bootstrapSnapshot = null;
        try
        {
            progress?.Report("正在准备首次安装……");
            if (wasRunning) await WindowsServiceInstaller.StopAsync(cancellationToken).ConfigureAwait(false);
            if (File.Exists(paths.CurrentReleasePointer)) _ = new BackupManager(paths, log).Create();
            config.Save(paths);
            progress?.Report("正在验证并安装服务端完整包……");
            bootstrapSnapshot = deployment.SnapshotBootstrap();
            using StagedPackage package = deployment.Stage(packagePath, requireFull: true);
            deployment.ActivateRuntime(package);
            deployment.InstallBootstrap(package);
            ExternalComponentManager external = new(paths, log);
            progress?.Report("正在准备 EasyTier……");
            string easyTier = await external.EnsureEasyTierAsync(cancellationToken, progress: progress).ConfigureAwait(false);
            progress?.Report("正在验证 EasyTier 虚拟网卡……");
            await EasyTierRuntimePreflight.RunAsync(paths, config, ServerSecrets.Load(paths), easyTier, cancellationToken).ConfigureAwait(false);
            if (config.DdnsEnabled)
            {
                progress?.Report("正在准备 DDNS-Go……");
                await external.EnsureDdnsGoAsync(cancellationToken, progress: progress).ConfigureAwait(false);
            }
            progress?.Report("正在配置 Windows 防火墙……");
            await FirewallManager.ApplyAsync(config, cancellationToken).ConfigureAwait(false);
            string host = Path.Combine(paths.Bootstrap, "SCBL.Server.Host.exe");
            progress?.Report("正在注册并启动 SCBL Windows 服务……");
            await WindowsServiceInstaller.InstallAsync(host, config.StartAutomatically, cancellationToken).ConfigureAwait(false);
            paths.EnsureLayout();
            if (!serviceExisted || wasRunning) await WindowsServiceInstaller.StartAsync(cancellationToken).ConfigureAwait(false);
            PackageDeployment.DiscardBootstrapSnapshot(bootstrapSnapshot);
            progress?.Report("首次安装完成，服务已经启动。");
        }
        catch (Exception original)
        {
            List<Exception> rollbackErrors = [];
            if (!serviceExisted && await WindowsServiceInstaller.ExistsAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // Remove the newly registered service before restoring bootstrap files. A failed
                // service start can have a scheduled recovery restart that still holds Host open.
                await TryRollbackAsync(() => WindowsServiceInstaller.UninstallAsync(CancellationToken.None), rollbackErrors).ConfigureAwait(false);
            }
            else if (serviceExisted)
            {
                await TryRollbackAsync(() => WindowsServiceInstaller.StopAsync(CancellationToken.None), rollbackErrors).ConfigureAwait(false);
            }
            TryRollback(() => deployment.RestoreBootstrap(bootstrapSnapshot), rollbackErrors);
            foreach (FileStateSnapshot snapshot in stateBefore) TryRollback(snapshot.Restore, rollbackErrors);
            if (previousConfig is null)
            {
                await TryRollbackAsync(() => FirewallManager.RemoveAsync(CancellationToken.None), rollbackErrors).ConfigureAwait(false);
            }
            else
            {
                await TryRollbackAsync(() => FirewallManager.ApplyAsync(previousConfig, CancellationToken.None), rollbackErrors).ConfigureAwait(false);
            }
            if (serviceExisted && Directory.Exists(paths.Bootstrap))
            {
                await TryRollbackAsync(async () =>
                {
                    await WindowsServiceInstaller.InstallAsync(Path.Combine(paths.Bootstrap, "SCBL.Server.Host.exe"), previousConfig?.StartAutomatically ?? true, CancellationToken.None).ConfigureAwait(false);
                    if (wasRunning) await WindowsServiceInstaller.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }, rollbackErrors).ConfigureAwait(false);
            }
            if (rollbackErrors.Count != 0) throw new AggregateException("安装失败且回滚未完全成功。", [original, .. rollbackErrors]);
            throw;
        }
    }

    private static void TryRollback(Action action, ICollection<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static async Task TryRollbackAsync(Func<Task> action, ICollection<Exception> errors)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }
}
