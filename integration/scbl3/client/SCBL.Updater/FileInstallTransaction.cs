internal sealed class UpdateTransactionLease : IDisposable
{
    private readonly FileStream _stream;

    private UpdateTransactionLease(FileStream stream) => _stream = stream;

    public static UpdateTransactionLease Acquire(string target)
    {
        string updates = UpdaterPaths.GetUpdatesDirectory(target);
        Directory.CreateDirectory(updates);
        string path = Path.Combine(updates, "install_transaction.lock");
        try
        {
            return new UpdateTransactionLease(new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException(
                "Another SCBL Updater transaction is already active.",
                exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}

internal sealed class TransactionalUpdateInstaller : IUpdateInstaller
{
    private readonly PackageInstallPlanner _planner;
    private readonly ITransactionObserver _observer;

    public TransactionalUpdateInstaller(
        PackageInstallPlanner? planner = null,
        ITransactionObserver? observer = null)
    {
        _planner = planner ?? new PackageInstallPlanner();
        _observer = observer ?? NullTransactionObserver.Instance;
    }

    public bool HasPendingTransaction(string target)
        => File.Exists(UpdaterPaths.GetJournalPath(target));

    public void RecoverPendingTransaction(
        string target,
        Action<UpdatePackageKind> beforeRecoveryMutation)
    {
        using UpdateTransactionLease lease = UpdateTransactionLease.Acquire(target);
        new FileInstallTransaction(_observer).RecoverPending(
            target,
            beforeRecoveryMutation);
    }

    public void Install(
        string package,
        string target,
        UpdatePackageKind packageKind,
        string version,
        Action<UpdatePackageKind> beforeFileMutation)
    {
        using UpdateTransactionLease lease = UpdateTransactionLease.Acquire(target);
        var transaction = new FileInstallTransaction(_observer);
        transaction.RecoverPending(target, beforeFileMutation);

        InstallPlan plan = _planner.Prepare(package, target, packageKind, version);
        try
        {
            beforeFileMutation(packageKind);
        }
        catch
        {
            CleanupUnjournaledPlan(plan);
            throw;
        }

        try
        {
            transaction.Execute(plan);
        }
        catch
        {
            CleanupUnjournaledPlan(plan);
            throw;
        }
    }

    private static void CleanupUnjournaledPlan(InstallPlan plan)
    {
        try
        {
            if (File.Exists(plan.JournalPath))
                return;
            if (Directory.Exists(plan.TransactionRoot))
                Directory.Delete(plan.TransactionRoot, recursive: true);
        }
        catch (Exception exception)
        {
            UpdaterLog.Write(
                $"Unjournaled staging cleanup retained {plan.TransactionRoot}: {exception.Message}");
        }
    }
}

internal sealed class FileInstallTransaction
{
    private const int ReplaceAttempts = 32;
    private const int ReplaceRetryDelayMs = 250;
    private readonly ITransactionObserver _observer;

    public FileInstallTransaction(ITransactionObserver? observer = null)
        => _observer = observer ?? NullTransactionObserver.Instance;

    public void Execute(InstallPlan plan)
    {
        TransactionJournal journal = CreateJournal(plan);
        AtomicJsonFile.Write(plan.JournalPath, journal);
        _observer.OnCheckpoint(TransactionCheckpoint.JournalPrepared, null);

        try
        {
            BackupExistingFiles(plan);
            journal.Phase = TransactionPhase.BackedUp;
            AtomicJsonFile.Write(plan.JournalPath, journal);
            _observer.OnCheckpoint(TransactionCheckpoint.BackupsCompleted, null);

            journal.Phase = TransactionPhase.Applying;
            AtomicJsonFile.Write(plan.JournalPath, journal);
            CreatePlannedDirectories(plan, journal);

            for (int index = 0; index < plan.Files.Count; index++)
            {
                PlannedFileMutation mutation = plan.Files[index];
                _observer.OnCheckpoint(
                    TransactionCheckpoint.BeforeMutation,
                    mutation.RelativePath);
                ValidateTargetStillMatchesPlan(mutation);
                journal.Files[index].MutationStarted = true;
                AtomicJsonFile.Write(plan.JournalPath, journal);
                ReplaceFromSource(
                    mutation.SourcePath,
                    mutation.TargetPath,
                    mutation.Size,
                    mutation.Sha256,
                    plan.TransactionId);
                journal.Files[index].Applied = true;
                AtomicJsonFile.Write(plan.JournalPath, journal);
                _observer.OnCheckpoint(
                    TransactionCheckpoint.AfterMutation,
                    mutation.RelativePath);
            }

            journal.Phase = TransactionPhase.FilesApplied;
            AtomicJsonFile.Write(plan.JournalPath, journal);
            _observer.OnCheckpoint(TransactionCheckpoint.FilesApplied, null);
            journal.Phase = TransactionPhase.Committed;
            AtomicJsonFile.Write(plan.JournalPath, journal);
            _observer.OnCheckpoint(TransactionCheckpoint.Committed, null);
            _observer.OnCheckpoint(TransactionCheckpoint.BeforeReceipt, null);
            WriteAppliedReceipt(plan.TargetRoot, plan.Version);
            CleanupCompletedTransaction(plan.JournalPath, plan.TransactionRoot);
            UpdaterLog.Write(
                $"Update transaction committed. id={plan.TransactionId}, files={plan.Files.Count}");
        }
        catch (Exception updateFailure)
        {
            if (journal.Phase is TransactionPhase.Prepared or TransactionPhase.BackedUp)
            {
                CleanupCompletedTransaction(plan.JournalPath, plan.TransactionRoot);
                throw;
            }

            if (journal.Phase == TransactionPhase.Committed)
                throw;

            try
            {
                Rollback(plan.JournalPath, journal);
            }
            catch (Exception rollbackFailure)
            {
                journal.Phase = TransactionPhase.RollbackFailed;
                TryWriteJournal(plan.JournalPath, journal);
                UpdaterLog.Write(
                    $"Rollback failed; evidence retained at {plan.TransactionRoot}: {rollbackFailure}");
                throw new UpdateRollbackException(updateFailure, rollbackFailure);
            }

            throw;
        }
    }

    public void RecoverPending(
        string target,
        Action<UpdatePackageKind>? beforeFileMutation = null)
    {
        string journalPath = UpdaterPaths.GetJournalPath(target);
        if (!File.Exists(journalPath))
            return;

        TransactionJournal journal = AtomicJsonFile.Read<TransactionJournal>(journalPath);
        ValidateJournal(target, journal);
        UpdaterLog.Write(
            $"Recovering unfinished update transaction. id={journal.TransactionId}, phase={journal.Phase}");

        switch (journal.Phase)
        {
            case TransactionPhase.Prepared:
            case TransactionPhase.BackedUp:
            case TransactionPhase.RolledBack:
                CleanupCompletedTransaction(journalPath, journal.TransactionRoot);
                return;
            case TransactionPhase.FilesApplied:
                VerifyAppliedFiles(journal);
                journal.Phase = TransactionPhase.Committed;
                AtomicJsonFile.Write(journalPath, journal);
                _observer.OnCheckpoint(TransactionCheckpoint.BeforeReceipt, null);
                WriteAppliedReceipt(journal.TargetRoot, journal.Version);
                CleanupCompletedTransaction(journalPath, journal.TransactionRoot);
                return;
            case TransactionPhase.Committed:
                VerifyAppliedFiles(journal);
                _observer.OnCheckpoint(TransactionCheckpoint.BeforeReceipt, null);
                WriteAppliedReceipt(journal.TargetRoot, journal.Version);
                CleanupCompletedTransaction(journalPath, journal.TransactionRoot);
                return;
            case TransactionPhase.Applying:
            case TransactionPhase.RollingBack:
            case TransactionPhase.RollbackFailed:
                beforeFileMutation?.Invoke(journal.PackageKind);
                Rollback(journalPath, journal);
                return;
            default:
                throw new InvalidDataException(
                    $"Unsupported update transaction phase: {journal.Phase}");
        }
    }

    private static TransactionJournal CreateJournal(InstallPlan plan)
        => new()
        {
            TransactionId = plan.TransactionId,
            TargetRoot = plan.TargetRoot,
            TransactionRoot = plan.TransactionRoot,
            Version = plan.Version,
            PackageKind = plan.PackageKind,
            Phase = TransactionPhase.Prepared,
            PackageDirectories = plan.PackageDirectories.ToList(),
            Directories = plan.CreatedDirectories.Select(relative => new JournalDirectoryMutation
            {
                RelativePath = relative
            }).ToList(),
            Files = plan.Files.Select(file => new JournalFileMutation
            {
                RelativePath = file.RelativePath,
                Size = file.Size,
                Sha256 = file.Sha256,
                TargetExisted = file.TargetExisted,
                OriginalSize = file.OriginalSize,
                OriginalSha256 = file.OriginalSha256,
                Applied = false
            }).ToList()
        };

    private static void BackupExistingFiles(InstallPlan plan)
    {
        foreach (PlannedFileMutation mutation in plan.Files.Where(file => file.TargetExisted))
        {
            if (!mutation.OriginalSize.HasValue
                || string.IsNullOrWhiteSpace(mutation.OriginalSha256)
                || !FileIntegrity.Matches(
                    mutation.TargetPath,
                    mutation.OriginalSize.Value,
                    mutation.OriginalSha256))
            {
                throw new IOException(
                    $"Installed file changed while the update plan was being prepared: {mutation.RelativePath}");
            }

            CopyAndVerify(
                mutation.TargetPath,
                mutation.BackupPath,
                mutation.OriginalSize.Value,
                mutation.OriginalSha256);
        }
    }

    private static void CreatePlannedDirectories(
        InstallPlan plan,
        TransactionJournal journal)
    {
        for (int index = 0; index < plan.CreatedDirectories.Count; index++)
        {
            string relative = plan.CreatedDirectories[index];
            string path = UpdaterPaths.ResolveContainedPath(plan.TargetRoot, relative);
            if (File.Exists(path))
                throw new IOException($"Update directory collides with a file: {relative}");
            if (Directory.Exists(path))
                throw new IOException($"A new target directory appeared during update planning: {relative}");
            journal.Directories[index].MutationStarted = true;
            AtomicJsonFile.Write(plan.JournalPath, journal);
            Directory.CreateDirectory(path);
            journal.Directories[index].Created = true;
            AtomicJsonFile.Write(plan.JournalPath, journal);
        }
    }

    private static void ValidateTargetStillMatchesPlan(PlannedFileMutation mutation)
    {
        if (mutation.TargetExisted)
        {
            if (!mutation.OriginalSize.HasValue
                || string.IsNullOrWhiteSpace(mutation.OriginalSha256)
                || !FileIntegrity.Matches(
                    mutation.TargetPath,
                    mutation.OriginalSize.Value,
                    mutation.OriginalSha256))
            {
                throw new IOException(
                    $"Installed file changed before atomic replacement: {mutation.RelativePath}");
            }
        }
        else if (File.Exists(mutation.TargetPath) || Directory.Exists(mutation.TargetPath))
        {
            throw new IOException(
                $"A new target path appeared during update planning: {mutation.RelativePath}");
        }
    }

    private void Rollback(string journalPath, TransactionJournal journal)
    {
        journal.Phase = TransactionPhase.RollingBack;
        AtomicJsonFile.Write(journalPath, journal);
        string backupRoot = Path.Combine(journal.TransactionRoot, "backup");

        foreach (JournalFileMutation mutation in journal.Files.AsEnumerable().Reverse())
        {
            _observer.OnCheckpoint(
                TransactionCheckpoint.BeforeRollbackMutation,
                mutation.RelativePath);
            string targetPath = UpdaterPaths.ResolveContainedPath(
                journal.TargetRoot,
                mutation.RelativePath);
            string transactionTemporary = targetPath
                + ".scbl-tx-"
                + journal.TransactionId
                + ".tmp";
            if (File.Exists(transactionTemporary))
                File.Delete(transactionTemporary);
            string rollbackTemporary = targetPath
                + ".scbl-tx-"
                + journal.TransactionId
                + ".rollback.tmp";
            if (File.Exists(rollbackTemporary))
                File.Delete(rollbackTemporary);
            if (mutation.TargetExisted)
            {
                if (!mutation.OriginalSize.HasValue
                    || string.IsNullOrWhiteSpace(mutation.OriginalSha256))
                {
                    throw new InvalidDataException(
                        $"Rollback metadata is incomplete: {mutation.RelativePath}");
                }

                if (!mutation.MutationStarted)
                {
                    if (!FileIntegrity.Matches(
                            targetPath,
                            mutation.OriginalSize.Value,
                            mutation.OriginalSha256))
                    {
                        throw new IOException(
                            $"An unmodified target changed during rollback: {mutation.RelativePath}");
                    }
                    _observer.OnCheckpoint(
                        TransactionCheckpoint.AfterRollbackMutation,
                        mutation.RelativePath);
                    continue;
                }

                string backupPath = UpdaterPaths.ResolveContainedPath(
                    backupRoot,
                    mutation.RelativePath);
                if (!FileIntegrity.Matches(
                    backupPath,
                    mutation.OriginalSize.Value,
                    mutation.OriginalSha256))
                {
                    throw new IOException(
                        $"Rollback backup is missing or corrupt: {mutation.RelativePath}");
                }
                ReplaceFromSource(
                    backupPath,
                    targetPath,
                    mutation.OriginalSize.Value,
                    mutation.OriginalSha256,
                    journal.TransactionId + ".rollback");
            }
            else if (File.Exists(targetPath))
            {
                if (!mutation.MutationStarted
                    || !FileIntegrity.Matches(targetPath, mutation.Size, mutation.Sha256))
                {
                    throw new IOException(
                        $"Refusing to delete a new path not owned by this transaction: {mutation.RelativePath}");
                }
                File.Delete(targetPath);
            }
            else if (Directory.Exists(targetPath))
            {
                throw new IOException(
                    $"Rollback target changed from a transaction file to a directory: {mutation.RelativePath}");
            }

            _observer.OnCheckpoint(
                TransactionCheckpoint.AfterRollbackMutation,
                mutation.RelativePath);
        }

        foreach (JournalDirectoryMutation mutation in journal.Directories
                     .OrderByDescending(item => item.RelativePath.Count(character => character == '/'))
                     .ThenByDescending(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            string relative = mutation.RelativePath;
            string directory = UpdaterPaths.ResolveContainedPath(
                journal.TargetRoot,
                relative);
            if (!mutation.Created)
            {
                if (mutation.MutationStarted && Directory.Exists(directory))
                {
                    throw new IOException(
                        $"Directory creation was interrupted before ownership was recorded: {relative}");
                }
                continue;
            }
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        VerifyRolledBackFiles(journal);
        journal.Phase = TransactionPhase.RolledBack;
        AtomicJsonFile.Write(journalPath, journal);
        CleanupCompletedTransaction(journalPath, journal.TransactionRoot);
        UpdaterLog.Write($"Update transaction rolled back. id={journal.TransactionId}");
    }

    private static void VerifyAppliedFiles(TransactionJournal journal)
    {
        foreach (JournalFileMutation file in journal.Files)
        {
            string target = UpdaterPaths.ResolveContainedPath(
                journal.TargetRoot,
                file.RelativePath);
            if (!FileIntegrity.Matches(target, file.Size, file.Sha256))
                throw new IOException($"Applied transaction file failed recovery verification: {file.RelativePath}");
        }
    }

    private static void VerifyRolledBackFiles(TransactionJournal journal)
    {
        foreach (JournalFileMutation file in journal.Files)
        {
            string target = UpdaterPaths.ResolveContainedPath(
                journal.TargetRoot,
                file.RelativePath);
            if (file.TargetExisted)
            {
                if (!file.OriginalSize.HasValue
                    || string.IsNullOrWhiteSpace(file.OriginalSha256)
                    || !FileIntegrity.Matches(
                        target,
                        file.OriginalSize.Value,
                        file.OriginalSha256))
                {
                    throw new IOException($"Rollback verification failed: {file.RelativePath}");
                }
            }
            else if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException($"Rollback left a transaction-created path: {file.RelativePath}");
            }
        }

        foreach (JournalDirectoryMutation directory in journal.Directories.Where(item => item.Created))
        {
            string target = UpdaterPaths.ResolveContainedPath(
                journal.TargetRoot,
                directory.RelativePath);
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new IOException(
                    $"Rollback left a transaction-created directory: {directory.RelativePath}");
            }
        }
    }

    private static void ValidateJournal(string expectedTarget, TransactionJournal journal)
    {
        if (journal.SchemaVersion != 1
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.PackageKind)
            || !Enum.IsDefined(journal.Phase)
            || !UpdaterApplication.NormalizeVersion(journal.Version)
                .Equals(journal.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update transaction journal has invalid identity fields.");
        }

        string target = UpdaterPaths.NormalizeTarget(expectedTarget);
        if (!UpdaterPaths.NormalizeTarget(journal.TargetRoot)
                .Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update transaction journal targets another client directory.");
        }

        string expectedTransactionRoot = Path.Combine(
            UpdaterPaths.GetTransactionsDirectory(target),
            journal.TransactionId);
        if (!Path.GetFullPath(journal.TransactionRoot)
                .Equals(Path.GetFullPath(expectedTransactionRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update transaction journal has an invalid transaction root.");
        }

        var uniqueFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalFileMutation file in journal.Files)
        {
            string relative = UpdaterPaths.NormalizeSafeRelativePath(file.RelativePath);
            if (!relative.Equals(file.RelativePath, StringComparison.Ordinal)
                || !uniqueFiles.Add(relative)
                || file.Size < 0
                || file.Size > PackageInstallPlanner.MaximumSingleFileBytes
                || file.Sha256.Length != 64
                || !file.Sha256.All(Uri.IsHexDigit)
                || (file.TargetExisted
                    && (!file.OriginalSize.HasValue
                        || file.OriginalSize < 0
                        || string.IsNullOrWhiteSpace(file.OriginalSha256)
                        || file.OriginalSha256.Length != 64
                        || !file.OriginalSha256.All(Uri.IsHexDigit))))
            {
                throw new InvalidDataException("Update transaction journal contains an invalid file mutation.");
            }
        }

        var uniqueCreatedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JournalDirectoryMutation directory in journal.Directories)
        {
            string normalized = UpdaterPaths.NormalizeSafeRelativePath(directory.RelativePath);
            if (!normalized.Equals(directory.RelativePath, StringComparison.Ordinal)
                || !uniqueCreatedDirectories.Add(normalized))
            {
                throw new InvalidDataException("Update transaction journal contains an invalid created directory.");
            }
        }

        var uniquePackageDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in journal.PackageDirectories)
        {
            string normalized = UpdaterPaths.NormalizeSafeRelativePath(directory);
            if (!normalized.Equals(directory, StringComparison.Ordinal)
                || !uniquePackageDirectories.Add(normalized))
            {
                throw new InvalidDataException("Update transaction journal contains an invalid package directory.");
            }
        }
    }

    private static void CopyAndVerify(
        string source,
        string destination,
        long expectedSize,
        string expectedSha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        using (var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        if (!FileIntegrity.Matches(destination, expectedSize, expectedSha256))
            throw new IOException($"Copied file failed size/SHA256 verification: {destination}");
    }

    private static void ReplaceFromSource(
        string source,
        string destination,
        long expectedSize,
        string expectedSha256,
        string transactionId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".scbl-tx-" + transactionId + ".tmp";
        Exception? lastFailure = null;
        for (int attempt = 1; attempt <= ReplaceAttempts; attempt++)
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
                CopyAndVerify(source, temporary, expectedSize, expectedSha256);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(temporary, destination);

                if (!FileIntegrity.Matches(destination, expectedSize, expectedSha256))
                    throw new IOException("Installed file failed size/SHA256 verification.");
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                    // Preserve the uniquely named transaction temporary file as evidence.
                }
                if (attempt < ReplaceAttempts)
                    Thread.Sleep(ReplaceRetryDelayMs);
            }
        }

        throw new IOException(
            $"Unable to atomically replace {destination} after {ReplaceAttempts} attempts.",
            lastFailure);
    }

    private static void WriteAppliedReceipt(string target, string version)
    {
        string path = Path.Combine(
            UpdaterPaths.GetUpdatesDirectory(target),
            "last_applied_update.json");
        AtomicJsonFile.Write(path, new AppliedUpdateReceipt
        {
            Version = version,
            AppliedAt = DateTimeOffset.Now.ToString("O")
        });
        UpdaterLog.Write("Applied update receipt written: " + path + ", version=" + version);
    }

    private static void CleanupCompletedTransaction(
        string journalPath,
        string transactionRoot)
    {
        try
        {
            if (File.Exists(journalPath))
                File.Delete(journalPath);
        }
        catch (Exception exception)
        {
            UpdaterLog.Write(
                $"Committed transaction journal cleanup deferred: {journalPath}: {exception.Message}");
            return;
        }

        try
        {
            if (Directory.Exists(transactionRoot))
                Directory.Delete(transactionRoot, recursive: true);
        }
        catch (Exception exception)
        {
            UpdaterLog.Write(
                $"Committed transaction artifact cleanup deferred: {transactionRoot}: {exception.Message}");
        }
    }

    private static void TryWriteJournal(
        string journalPath,
        TransactionJournal journal)
    {
        try
        {
            AtomicJsonFile.Write(journalPath, journal);
        }
        catch (Exception exception)
        {
            UpdaterLog.Write("Could not persist rollback-failed state: " + exception.Message);
        }
    }

    private sealed class AppliedUpdateReceipt
    {
        public string Version { get; set; } = "";
        public string AppliedAt { get; set; } = "";
    }
}
