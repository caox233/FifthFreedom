internal sealed class UpdaterApplication
{
    private readonly IUpdateInstaller _installer;
    private readonly IUpdaterProcessBoundary _processes;
    private readonly IRestartBoundary _restart;

    public UpdaterApplication(
        IUpdateInstaller installer,
        IUpdaterProcessBoundary processes,
        IRestartBoundary restart)
    {
        _installer = installer;
        _processes = processes;
        _restart = restart;
    }

    public int Run(string[] args)
    {
        string? target = null;
        string? restart = null;
        string[] launcherArguments = Array.Empty<string>();
        try
        {
            string? package = GetArg(args, "--package");
            target = GetArg(args, "--target");
            string? pidText = GetArg(args, "--pid");
            restart = GetArg(args, "--restart");
            string? requestedVersion = GetArg(args, "--version");
            string? packageType = GetArg(args, "--package-type");
            string? waitPidText = GetArg(args, "--wait-pid");
            string? handoffEventName = GetArg(args, "--handoff-event");
            launcherArguments = GetArgs(args, "--launcher-argument");
#if SCBL_TEST_CHANNEL_RESTART
            // Test-channel updater builds preserve the established bootstrap
            // behavior for launchers that cannot yet forward --test themselves.
            if (launcherArguments.Length == 0)
                launcherArguments = new[] { "--test" };
#endif

            if (args.Any(argument => argument.Equals(
                    "--restart-helper",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return _restart.RunHelper(
                    target,
                    restart,
                    waitPidText,
                    handoffEventName,
                    launcherArguments);
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                target = UpdaterPaths.InferTargetDirectory();
                UpdaterLog.Initialize(target);
                UpdaterLog.Write("Missing --target; inferred target=" + target);
            }

            target = UpdaterPaths.NormalizeTarget(target);
            UpdaterLog.Initialize(target);

            if (string.IsNullOrWhiteSpace(package))
            {
                if (_installer.HasPendingTransaction(target))
                {
                    if (!Directory.Exists(target))
                        throw new DirectoryNotFoundException(target);
                    if (_processes.IsGameRunning())
                    {
                        UpdaterLog.Write(
                            "Pending update recovery deferred because a Blacklist game process is running.");
                        if (!_restart.LaunchImmediately(target, restart, launcherArguments))
                            _restart.ReportFailure(target);
                        return 7;
                    }
                    bool runtimeStopped = false;
                    _installer.RecoverPendingTransaction(
                        target,
                        kind => StopRuntimeForMutationIfRequired(
                            kind,
                            target,
                            ref runtimeStopped));
                }

                UpdaterLog.Write("No --package specified; nothing to update.");
                return 0;
            }

            UpdaterLog.Write($"Update started. package={package}, target={target}");
            if (!Directory.Exists(target))
                throw new DirectoryNotFoundException(target);

            if (int.TryParse(pidText, out int pid) && pid > 0)
                _processes.WaitForProcessExit(pid);

            if (_processes.IsGameRunning())
            {
                UpdaterLog.Write(
                    "Update deferred because a Blacklist game process is still running. Runtime processes were not stopped.");
                if (!_restart.LaunchImmediately(target, restart, launcherArguments))
                    _restart.ReportFailure(target);
                return 7;
            }

            package = Path.GetFullPath(package);
            if (!File.Exists(package))
                throw new FileNotFoundException("Update package not found", package);

            string appliedVersion = NormalizeVersion(requestedVersion);
            if (string.IsNullOrWhiteSpace(appliedVersion))
            {
                throw new InvalidOperationException(
                    "Missing or invalid --version for full client update.");
            }

            UpdatePackageKind kind = string.Equals(
                packageType,
                "launcher-component",
                StringComparison.OrdinalIgnoreCase)
                ? UpdatePackageKind.LauncherComponent
                : UpdatePackageKind.FullPackage;

            if (kind == UpdatePackageKind.LauncherComponent)
            {
                UpdaterLog.Write(
                    "Applying isolated Launcher component; runtime tools will not be stopped or replaced.");
            }

            bool runtimeStoppedForMutation = false;
            _installer.Install(
                package,
                target,
                kind,
                appliedVersion,
                mutationKind => StopRuntimeForMutationIfRequired(
                    mutationKind,
                    target,
                    ref runtimeStoppedForMutation));

            CleanupAppliedPackage(package, target);
            UpdaterLog.Write("Update finished.");

            if (!_restart.ScheduleAfterUpdaterExit(
                    target,
                    restart,
                    launcherArguments))
            {
                UpdaterLog.Write(
                    "Launcher restart helper could not be scheduled; trying an immediate fallback.");
                if (!_restart.LaunchImmediately(target, restart, launcherArguments))
                {
                    UpdaterLog.Write(
                        "Update succeeded, but the launcher could not be restarted automatically.");
                    _restart.ReportFailure(target);
                    return 8;
                }
            }

            return 0;
        }
        catch (Exception exception)
        {
            UpdaterLog.Write("Update failed: " + exception);
            Console.Error.WriteLine(exception);
            if (!string.IsNullOrWhiteSpace(target))
            {
                bool restartBlocked;
                try
                {
                    restartBlocked = _installer.HasPendingTransaction(target);
                }
                catch
                {
                    restartBlocked = true;
                }

                if (restartBlocked)
                {
                    UpdaterLog.Write(
                        "Update transaction remains pending; automatic relaunch is blocked until recovery succeeds.");
                }
                else
                {
                    UpdaterLog.Write(
                        "Update failed; relaunching the existing client instead of leaving the user without a launcher.");
                    if (!_restart.LaunchImmediately(target, restart, launcherArguments))
                        _restart.ReportFailure(target);
                }
            }
            return 1;
        }
    }

    internal static string NormalizeVersion(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        string[] parts = normalized.Split('.');
        return parts.Length == 3
               && parts.All(part => int.TryParse(part, out int number) && number >= 0)
            ? normalized
            : string.Empty;
    }

    private void StopRuntimeForMutationIfRequired(
        UpdatePackageKind kind,
        string target,
        ref bool runtimeStopped)
    {
        if (kind != UpdatePackageKind.FullPackage || runtimeStopped)
            return;
        _processes.StopRuntimeProcessesForUpdate(target);
        runtimeStopped = true;
    }

    internal static string? GetArg(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    internal static string[] GetArgs(string[] args, string name)
    {
        var values = new List<string>();
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                values.Add(args[++index]);
        }
        return values.ToArray();
    }

    private static void CleanupAppliedPackage(string package, string target)
    {
        try
        {
            string fullPackage = Path.GetFullPath(package);
            string updatesRoot = Path.GetFullPath(
                    UpdaterPaths.GetUpdatesDirectory(target))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPackage.StartsWith(
                    updatesRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                UpdaterLog.Write(
                    "Update package cleanup skipped because it is outside the launcher update workspace: "
                    + fullPackage);
                return;
            }

            if (File.Exists(fullPackage))
            {
                File.Delete(fullPackage);
                UpdaterLog.Write("Deleted applied update package: " + fullPackage);
            }

            string? parent = Path.GetDirectoryName(fullPackage);
            if (!string.IsNullOrWhiteSpace(parent)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception exception)
        {
            UpdaterLog.Write(
                "Applied update package cleanup skipped: " + exception.Message);
        }
    }
}
