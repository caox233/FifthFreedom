internal enum UpdatePackageKind
{
    FullPackage,
    LauncherComponent
}

internal sealed record PlannedFileMutation(
    string RelativePath,
    string SourcePath,
    string TargetPath,
    string BackupPath,
    long Size,
    string Sha256,
    bool TargetExisted,
    long? OriginalSize,
    string? OriginalSha256);

internal sealed record InstallPlan(
    string TransactionId,
    string TargetRoot,
    string TransactionRoot,
    string StagingRoot,
    string BackupRoot,
    string JournalPath,
    string Version,
    UpdatePackageKind PackageKind,
    IReadOnlyList<PlannedFileMutation> Files,
    IReadOnlyList<string> PackageDirectories,
    IReadOnlyList<string> CreatedDirectories);

internal enum TransactionPhase
{
    Prepared,
    BackedUp,
    Applying,
    FilesApplied,
    Committed,
    RollingBack,
    RolledBack,
    RollbackFailed
}

internal sealed class TransactionJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string TransactionId { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public string TransactionRoot { get; set; } = "";
    public string Version { get; set; } = "";
    public UpdatePackageKind PackageKind { get; set; }
    public TransactionPhase Phase { get; set; }
    public List<JournalFileMutation> Files { get; set; } = new();
    public List<string> PackageDirectories { get; set; } = new();
    public List<JournalDirectoryMutation> Directories { get; set; } = new();
}

internal sealed class JournalFileMutation
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public bool TargetExisted { get; set; }
    public long? OriginalSize { get; set; }
    public string? OriginalSha256 { get; set; }
    public bool MutationStarted { get; set; }
    public bool Applied { get; set; }
}

internal sealed class JournalDirectoryMutation
{
    public string RelativePath { get; set; } = "";
    public bool MutationStarted { get; set; }
    public bool Created { get; set; }
}

internal enum TransactionCheckpoint
{
    JournalPrepared,
    BackupsCompleted,
    BeforeMutation,
    AfterMutation,
    FilesApplied,
    BeforeReceipt,
    Committed,
    BeforeRollbackMutation,
    AfterRollbackMutation
}

/// <summary>
/// Internal observation-only seam for deterministic QA interruption. Production
/// uses the no-op implementation; observers can abort by throwing but cannot
/// bypass validation, hashing, journaling, or rollback.
/// </summary>
internal interface ITransactionObserver
{
    void OnCheckpoint(TransactionCheckpoint checkpoint, string? relativePath);
}

internal sealed class NullTransactionObserver : ITransactionObserver
{
    public static NullTransactionObserver Instance { get; } = new();

    private NullTransactionObserver()
    {
    }

    public void OnCheckpoint(TransactionCheckpoint checkpoint, string? relativePath)
    {
    }
}

internal interface IUpdateInstaller
{
    bool HasPendingTransaction(string target);
    void RecoverPendingTransaction(
        string target,
        Action<UpdatePackageKind> beforeRecoveryMutation);
    void Install(
        string package,
        string target,
        UpdatePackageKind packageKind,
        string version,
        Action<UpdatePackageKind> beforeFileMutation);
}

internal interface IUpdaterProcessBoundary
{
    void WaitForProcessExit(int pid);
    bool IsGameRunning();
    void StopRuntimeProcessesForUpdate(string target);
}

internal interface IRestartBoundary
{
    int RunHelper(
        string? target,
        string? restart,
        string? waitPidText,
        string? handoffEventName,
        IReadOnlyCollection<string> launcherArguments);

    bool ScheduleAfterUpdaterExit(
        string target,
        string? restart,
        IReadOnlyCollection<string> launcherArguments);

    bool LaunchImmediately(
        string target,
        string? restart,
        IReadOnlyCollection<string> launcherArguments);

    void ReportFailure(string target);
}

internal sealed class RestartBoundary : IRestartBoundary
{
    public int RunHelper(
        string? target,
        string? restart,
        string? waitPidText,
        string? handoffEventName,
        IReadOnlyCollection<string> launcherArguments)
        => RestartCoordinator.RunHelper(
            target,
            restart,
            waitPidText,
            handoffEventName,
            launcherArguments);

    public bool ScheduleAfterUpdaterExit(
        string target,
        string? restart,
        IReadOnlyCollection<string> launcherArguments)
        => RestartCoordinator.ScheduleAfterUpdaterExit(target, restart, launcherArguments);

    public bool LaunchImmediately(
        string target,
        string? restart,
        IReadOnlyCollection<string> launcherArguments)
        => RestartCoordinator.LaunchImmediately(target, restart, launcherArguments);

    public void ReportFailure(string target)
        => RestartCoordinator.ReportFailure(target);
}

internal sealed class UpdateRollbackException : IOException
{
    public UpdateRollbackException(Exception updateFailure, Exception rollbackFailure)
        : base(
            "Update failed and rollback could not be completed. Transaction evidence was preserved.",
            new AggregateException(updateFailure, rollbackFailure))
    {
    }
}
