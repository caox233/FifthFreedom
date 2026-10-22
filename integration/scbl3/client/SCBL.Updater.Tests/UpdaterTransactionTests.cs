using System.IO.Compression;

namespace SCBL.Updater.Tests;

[Collection("Updater transaction tests")]
public sealed class UpdaterTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "scbl-updater-tests",
        Guid.NewGuid().ToString("N"));

    public UpdaterTransactionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Restart_helper_forwards_canonical_channel_arguments_unchanged()
    {
        var restart = new FakeRestart();
        var application = new UpdaterApplication(
            new FakeInstaller(),
            new FakeProcesses(),
            restart);

        int exitCode = application.Run(
        [
            "--restart-helper",
            "--launcher-argument",
            "--update-channel",
            "--launcher-argument",
            "test"
        ]);

        Assert.Equal(8, exitCode);
        Assert.Equal(["--update-channel", "test"], restart.LastLauncherArguments);
    }

    [Fact]
    public void Restart_handoff_requires_acceptance_signal_not_only_helper_creation()
    {
        using var accepted = new ManualResetEvent(false);
        using var helperExited = new ManualResetEvent(true);

        Assert.False(RestartCoordinator.WaitForHandoffSignal(
            accepted,
            helperExited,
            timeoutMs: 20));

        helperExited.Reset();
        accepted.Set();
        Assert.True(RestartCoordinator.WaitForHandoffSignal(
            accepted,
            helperExited,
            timeoutMs: 20));
    }

    [Fact]
    public void Target_normalization_rejects_filesystem_roots_and_preserves_normal_directories()
    {
        string target = NewDirectory("normalize-target");

        Assert.Equal(
            Path.GetFullPath(target),
            UpdaterPaths.NormalizeTarget(target + Path.DirectorySeparatorChar),
            ignoreCase: true);
        Assert.Throws<InvalidDataException>(() =>
            UpdaterPaths.NormalizeTarget(Path.GetPathRoot(Path.GetFullPath(target))!));
        Assert.Throws<ArgumentException>(() => UpdaterPaths.NormalizeTarget("   "));
    }

    [Fact]
    public void Client_data_path_is_portable_and_isolated_by_computer_and_user()
    {
        string target = NewDirectory("data-layout-target");

        Assert.Equal(
            Path.Combine(target, "data", "WORK_PC", "Alice_User"),
            SCBL.Client.Shared.ClientDataLayout.ResolveForIdentity(
                target,
                "WORK:PC",
                "Alice/User"),
            ignoreCase: true);
    }

    [Fact]
    public void Planner_rejects_traversal_and_case_insensitive_duplicates()
    {
        string target = NewDirectory("planner-target");
        string traversal = CreateZip("traversal.zip", ("../escape.txt", "bad"));
        string duplicate = CreateZip(
            "duplicate.zip",
            ("same.txt", "one"),
            ("SAME.txt", "two"));

        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            traversal, target, UpdatePackageKind.FullPackage, "1.2.3"));
        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            duplicate, target, UpdatePackageKind.FullPackage, "1.2.3"));
    }

    [Fact]
    public void Full_package_requires_one_complete_content_root()
    {
        string target = NewDirectory("content-root-target");
        string missing = CreateZip("missing.zip", ("readme.txt", "none"));
        string ambiguous = CreateZip(
            "ambiguous.zip",
            ("SCBL.exe", "first"),
            ("nested/SCBL.exe", "second"));
        string outside = CreateZip(
            "outside.zip",
            ("wrapper/SCBL.exe", "launcher"),
            ("outside.txt", "not-planned"));

        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            missing, target, UpdatePackageKind.FullPackage, "1.2.3"));
        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            ambiguous, target, UpdatePackageKind.FullPackage, "1.2.3"));
        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            outside, target, UpdatePackageKind.FullPackage, "1.2.3"));
    }

    [Fact]
    public void Launcher_component_accepts_only_one_root_launcher()
    {
        string target = NewDirectory("launcher-target");
        string extra = CreateZip(
            "launcher-extra.zip",
            ("SCBL.exe", "launcher"),
            ("extra.txt", "extra"));
        string nested = CreateZip(
            "launcher-nested.zip",
            ("nested/SCBL.exe", "launcher"));

        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            extra, target, UpdatePackageKind.LauncherComponent, "1.2.3"));
        Assert.Throws<InvalidDataException>(() => new PackageInstallPlanner().Prepare(
            nested, target, UpdatePackageKind.LauncherComponent, "1.2.3"));
    }

    [Fact]
    public void Full_package_plan_excludes_user_data_and_update_metadata()
    {
        string target = NewDirectory("exclusions-target");
        string package = CreateZip(
            "exclusions.zip",
            ("SCBL.exe", "launcher"),
            ("components/runtime/runtime.bin", "runtime"),
            ("data/TESTPC/TestUser/PRIVATE.txt", "private"),
            ("logs/log.txt", "log"),
            ("updates/payload.bin", "update"),
            ("backup/old.bin", "backup"),
            ("backup_saves/save.bin", "save"),
            ("launcher_settings.json", "settings"),
            ("nested/update_manifest.json", "metadata"));

        InstallPlan plan = new PackageInstallPlanner().Prepare(
            package, target, UpdatePackageKind.FullPackage, "1.2.3");

        Assert.Equal(
            new[] { "components/runtime/runtime.bin", "SCBL.exe" },
            plan.Files.Select(file => file.RelativePath),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transaction_commits_verified_files_and_receipt()
    {
        string target = NewDirectory("commit-target");
        Write(target, "existing.txt", "old");
        string package = CreateZip(
            "commit.zip",
            ("SCBL.exe", "launcher"),
            ("existing.txt", "new"),
            ("new/file.txt", "created"));

        new TransactionalUpdateInstaller().Install(
            package,
            target,
            UpdatePackageKind.FullPackage,
            "1.2.3",
            static _ => { });

        Assert.Equal("new", Read(target, "existing.txt"));
        Assert.Equal("created", Read(target, "new/file.txt"));
        Assert.False(File.Exists(UpdaterPaths.GetJournalPath(target)));
        Assert.True(File.Exists(Path.Combine(
            UpdaterPaths.GetUpdatesDirectory(target),
            "last_applied_update.json")));
    }

    [Fact]
    public void Injected_interruption_rolls_back_in_reverse_without_leaking_new_paths()
    {
        string target = NewDirectory("rollback-target");
        Write(target, "a-existing.txt", "old");
        string package = CreateZip(
            "rollback.zip",
            ("SCBL.exe", "launcher"),
            ("a-existing.txt", "new"),
            ("b-new.txt", "created"));
        var observer = new ThrowOnceObserver(
            TransactionCheckpoint.AfterMutation,
            "a-existing.txt");

        Assert.Throws<IOException>(() =>
            new TransactionalUpdateInstaller(observer: observer).Install(
                package,
                target,
                UpdatePackageKind.FullPackage,
                "1.2.3",
                static _ => { }));

        Assert.Equal("old", Read(target, "a-existing.txt"));
        Assert.False(File.Exists(Path.Combine(target, "b-new.txt")));
        Assert.False(File.Exists(UpdaterPaths.GetJournalPath(target)));
    }

    [Fact]
    public void Applying_recovery_uses_package_kind_guard_before_reverse_mutation()
    {
        string target = NewDirectory("recover-applying-target");
        string transactionId = Guid.NewGuid().ToString("N");
        string transactionRoot = Path.Combine(
            UpdaterPaths.GetTransactionsDirectory(target), transactionId);
        string backupRoot = Path.Combine(transactionRoot, "backup");
        Write(target, "existing.txt", "new");
        Write(backupRoot, "existing.txt", "old");
        Write(target, "created.txt", "created");
        TransactionJournal journal = Journal(
            target,
            transactionId,
            transactionRoot,
            TransactionPhase.Applying,
            new JournalFileMutation
            {
                RelativePath = "existing.txt",
                Size = 3,
                Sha256 = HashText("new"),
                TargetExisted = true,
                OriginalSize = 3,
                OriginalSha256 = HashText("old"),
                MutationStarted = true,
                Applied = true
            },
            new JournalFileMutation
            {
                RelativePath = "created.txt",
                Size = 7,
                Sha256 = HashText("created"),
                TargetExisted = false,
                MutationStarted = true,
                Applied = true
            });
        AtomicJsonFile.Write(UpdaterPaths.GetJournalPath(target), journal);
        var guardedKinds = new List<UpdatePackageKind>();

        new FileInstallTransaction().RecoverPending(
            target,
            kind => guardedKinds.Add(kind));

        Assert.Equal(new[] { UpdatePackageKind.FullPackage }, guardedKinds);
        Assert.Equal("old", Read(target, "existing.txt"));
        Assert.False(File.Exists(Path.Combine(target, "created.txt")));
        Assert.False(File.Exists(UpdaterPaths.GetJournalPath(target)));
    }

    [Fact]
    public void Files_applied_recovery_finishes_receipt_without_process_guard()
    {
        string target = NewDirectory("recover-applied-target");
        string transactionId = Guid.NewGuid().ToString("N");
        string transactionRoot = Path.Combine(
            UpdaterPaths.GetTransactionsDirectory(target), transactionId);
        Directory.CreateDirectory(transactionRoot);
        Write(target, "installed.txt", "new");
        AtomicJsonFile.Write(
            UpdaterPaths.GetJournalPath(target),
            Journal(
                target,
                transactionId,
                transactionRoot,
                TransactionPhase.FilesApplied,
                new JournalFileMutation
                {
                    RelativePath = "installed.txt",
                    Size = 3,
                    Sha256 = HashText("new"),
                    TargetExisted = false,
                    MutationStarted = true,
                    Applied = true
                }));
        int guards = 0;

        new FileInstallTransaction().RecoverPending(target, _ => guards++);

        Assert.Equal(0, guards);
        Assert.Equal("new", Read(target, "installed.txt"));
        Assert.True(File.Exists(Path.Combine(
            UpdaterPaths.GetUpdatesDirectory(target),
            "last_applied_update.json")));
        Assert.False(File.Exists(UpdaterPaths.GetJournalPath(target)));
    }

    [Fact]
    public void Application_defers_game_and_applies_kind_aware_stop_boundaries()
    {
        string target = NewDirectory("application-target");
        string package = Path.Combine(_root, "application-package.zip");
        File.WriteAllText(package, "placeholder");

        var gameProcesses = new FakeProcesses { GameRunning = true };
        var gameInstaller = new FakeInstaller();
        var gameRestart = new FakeRestart();
        Assert.Equal(7, new UpdaterApplication(gameInstaller, gameProcesses, gameRestart)
            .Run(Args(package, target, "full")));
        Assert.Equal(0, gameInstaller.InstallCalls);
        Assert.Equal(0, gameProcesses.StopCalls);
        Assert.Equal(1, gameRestart.ImmediateCalls);

        var fullProcesses = new FakeProcesses { StopFailure = new IOException("injected") };
        var fullInstaller = new FakeInstaller();
        Assert.Equal(1, new UpdaterApplication(fullInstaller, fullProcesses, new FakeRestart())
            .Run(Args(package, target, "full")));
        Assert.Equal(1, fullInstaller.InstallCalls);
        Assert.Equal(1, fullProcesses.StopCalls);

        var launcherProcesses = new FakeProcesses();
        var launcherInstaller = new FakeInstaller();
        Assert.Equal(0, new UpdaterApplication(launcherInstaller, launcherProcesses, new FakeRestart())
            .Run(Args(package, target, "launcher-component")));
        Assert.Equal(UpdatePackageKind.LauncherComponent, launcherInstaller.LastKind);
        Assert.Equal(0, launcherProcesses.StopCalls);

        var recoveryProcesses = new FakeProcesses();
        var recoveryInstaller = new FakeInstaller
        {
            HasPending = true,
            RecoveryKind = UpdatePackageKind.FullPackage
        };
        Assert.Equal(0, new UpdaterApplication(recoveryInstaller, recoveryProcesses, new FakeRestart())
            .Run(new[] { "--target", target }));
        Assert.Equal(1, recoveryInstaller.RecoverCalls);
        Assert.Equal(1, recoveryProcesses.StopCalls);
    }

    [Fact]
    public void Successful_update_requires_one_confirmed_restart_handoff()
    {
        string target = NewDirectory("confirmed-restart-target");
        string package = Path.Combine(_root, "confirmed-restart-package.zip");
        File.WriteAllText(package, "placeholder");
        var restart = new FakeRestart { ScheduleResult = true };

        int exitCode = new UpdaterApplication(
            new FakeInstaller(),
            new FakeProcesses(),
            restart).Run(Args(package, target, "launcher-component"));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, restart.ScheduleCalls);
        Assert.Equal(0, restart.ImmediateCalls);
        Assert.Equal(0, restart.FailureReports);
    }

    [Fact]
    public void Unconfirmed_restart_handoff_falls_back_once_and_failure_is_visible()
    {
        string target = NewDirectory("failed-restart-target");
        string package = Path.Combine(_root, "failed-restart-package.zip");
        File.WriteAllText(package, "placeholder");
        var restart = new FakeRestart
        {
            ScheduleResult = false,
            ImmediateResult = false
        };

        int exitCode = new UpdaterApplication(
            new FakeInstaller(),
            new FakeProcesses(),
            restart).Run(Args(package, target, "launcher-component"));

        Assert.Equal(8, exitCode);
        Assert.Equal(1, restart.ScheduleCalls);
        Assert.Equal(1, restart.ImmediateCalls);
        Assert.Equal(1, restart.FailureReports);
    }

    [Fact]
    public void No_update_does_not_schedule_or_report_a_restart()
    {
        string target = NewDirectory("no-update-target");
        var restart = new FakeRestart();

        int exitCode = new UpdaterApplication(
            new FakeInstaller(),
            new FakeProcesses(),
            restart).Run(["--target", target]);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, restart.ScheduleCalls);
        Assert.Equal(0, restart.ImmediateCalls);
        Assert.Equal(0, restart.FailureReports);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A failed cleanup must not hide the product assertion result.
        }
    }

    private string CreateZip(
        string name,
        params (string Path, string Content)[] entries)
    {
        string path = Path.Combine(_root, name);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryPath, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private string NewDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string root, string relative)
        => File.ReadAllText(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));

    private string HashText(string value)
    {
        string path = Path.Combine(_root, "hash-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, value);
        return FileIntegrity.ComputeSha256(path);
    }

    private static TransactionJournal Journal(
        string target,
        string transactionId,
        string transactionRoot,
        TransactionPhase phase,
        params JournalFileMutation[] files)
        => new()
        {
            TransactionId = transactionId,
            TargetRoot = UpdaterPaths.NormalizeTarget(target),
            TransactionRoot = transactionRoot,
            Version = "1.2.3",
            PackageKind = UpdatePackageKind.FullPackage,
            Phase = phase,
            Files = files.ToList()
        };

    private static string[] Args(string package, string target, string type)
        => new[]
        {
            "--package", package,
            "--target", target,
            "--version", "1.2.3",
            "--package-type", type
        };

    private sealed class ThrowOnceObserver(
        TransactionCheckpoint checkpoint,
        string relativePath) : ITransactionObserver
    {
        private bool _thrown;

        public void OnCheckpoint(TransactionCheckpoint actualCheckpoint, string? actualPath)
        {
            if (_thrown
                || actualCheckpoint != checkpoint
                || !string.Equals(actualPath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _thrown = true;
            throw new IOException("injected interruption");
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public int InstallCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public bool HasPending { get; set; }
        public UpdatePackageKind RecoveryKind { get; set; }
        public UpdatePackageKind LastKind { get; private set; }

        public bool HasPendingTransaction(string target) => HasPending;

        public void RecoverPendingTransaction(
            string target,
            Action<UpdatePackageKind> beforeRecoveryMutation)
        {
            RecoverCalls++;
            beforeRecoveryMutation(RecoveryKind);
        }

        public void Install(
            string package,
            string target,
            UpdatePackageKind packageKind,
            string version,
            Action<UpdatePackageKind> beforeFileMutation)
        {
            InstallCalls++;
            LastKind = packageKind;
            beforeFileMutation(packageKind);
        }
    }

    private sealed class FakeProcesses : IUpdaterProcessBoundary
    {
        public bool GameRunning { get; set; }
        public Exception? StopFailure { get; set; }
        public int StopCalls { get; private set; }

        public void WaitForProcessExit(int pid) { }
        public bool IsGameRunning() => GameRunning;

        public void StopRuntimeProcessesForUpdate(string target)
        {
            StopCalls++;
            if (StopFailure != null)
                throw StopFailure;
        }
    }

    private sealed class FakeRestart : IRestartBoundary
    {
        public bool ScheduleResult { get; set; } = true;
        public bool ImmediateResult { get; set; } = true;
        public int ScheduleCalls { get; private set; }
        public int ImmediateCalls { get; private set; }
        public int FailureReports { get; private set; }
        public IReadOnlyList<string> LastLauncherArguments { get; private set; } = [];

        public int RunHelper(
            string? target,
            string? restart,
            string? waitPidText,
            string? handoffEventName,
            IReadOnlyCollection<string> launcherArguments)
        {
            LastLauncherArguments = launcherArguments.ToArray();
            return 8;
        }

        public bool ScheduleAfterUpdaterExit(
            string target,
            string? restart,
            IReadOnlyCollection<string> launcherArguments)
        {
            ScheduleCalls++;
            return ScheduleResult;
        }

        public bool LaunchImmediately(
            string target,
            string? restart,
            IReadOnlyCollection<string> launcherArguments)
        {
            ImmediateCalls++;
            return ImmediateResult;
        }

        public void ReportFailure(string target) => FailureReports++;
    }
}

[CollectionDefinition("Updater transaction tests", DisableParallelization = true)]
public sealed class UpdaterTransactionTestCollection;
