using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Applies bounded retention to launcher-owned state. All operations are best effort:
/// maintenance must never prevent the launcher or game from starting.
/// </summary>
public static class ClientStorageMaintenanceService
{
    internal const string BundleStagingDirectoryPrefix = ".bundle-stage-";
    internal const string BundleTransactionLockFileName = ".bundle-active.lock";
    internal const string BundleBackupMarker = ".bundle-";
    internal const string BundleBackupSuffix = ".bak";
    private const long MaxGameLogBytes = 10L * 1024 * 1024;
    private const int GameLogArchives = 3;
    private const int DiagnosticsToKeep = 10;
    // The verified active component is the rollback-independent canonical copy.
    // The updater already performs an atomic replacement with an immediate
    // backup/restore on failure, so retaining older component payloads only
    // accumulates stale executables (and, in older releases, nested temp trees).
    private const int ComponentVersionsToKeep = 1;
    private static readonly TimeSpan BundleTransactionRetention = TimeSpan.FromDays(1);

    public static void RunStartupCleanup()
    {
        try
        {
            Run(
                LogService.PersistentDataDirectory,
                DateTime.UtcNow,
                message => LogService.Info("Storage maintenance: " + message));
        }
        catch (Exception ex)
        {
            LogService.Warning("Storage maintenance skipped: " + ex.Message);
        }
    }

    public static void ScheduleDeferredRunnerCleanup()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            try
            {
                DeleteOldChildren(
                    Path.Combine(LogService.UpdatesDirectory, "runner"),
                    DateTime.UtcNow + TimeSpan.FromMinutes(1),
                    message => LogService.Info("Storage maintenance: " + message));
            }
            catch (Exception ex)
            {
                LogService.Warning("Deferred Updater runner cleanup skipped: " + ex.Message);
            }
        });
    }

    internal static void Run(string dataRoot, DateTime utcNow, Action<string>? report = null)
    {
        string logs = Path.Combine(dataRoot, "logs", "game");
        RotateIfOversized(Path.Combine(logs, "bl-tracing.log"), MaxGameLogBytes, GameLogArchives, report);
        RotateIfOversized(Path.Combine(logs, "hooks-party-trace.log"), MaxGameLogBytes, GameLogArchives, report);

        string diagnostics = Path.Combine(dataRoot, "diagnostics");
        PruneNewestFiles(diagnostics, "SCBL_Diagnostics_*.zip", DiagnosticsToKeep, report);
        DeleteOldChildren(Path.Combine(diagnostics, "work"), utcNow - TimeSpan.FromDays(1), report);

        string updates = Path.Combine(dataRoot, "updates");
        DeleteOldChildren(Path.Combine(updates, "runner"), utcNow + TimeSpan.FromMinutes(1), report);
        DeleteOldChildren(Path.Combine(updates, "work"), utcNow - TimeSpan.FromDays(1), report);
        DeleteOldChildren(Path.Combine(updates, "component-work"), utcNow - TimeSpan.FromDays(1), report);
        DeleteOldFiles(updates, new[] { "*.download", "*.tmp" }, utcNow - TimeSpan.FromDays(1), report);
        // Download archives and launcher-component packages are only hand-off
        // material for SCBL.Updater. Once a launcher has restarted they are no
        // longer an installed component or a rollback source.
        DeleteAllChildren(Path.Combine(updates, "downloads"), report);
        DeleteAllChildren(Path.Combine(updates, "component-packages"), report);

        string components = Path.Combine(dataRoot, "components");
        RecoverBundleCacheTransactions(components, utcNow, report);
        PruneComponentVersions(components, ComponentVersionsToKeep, report);
    }

    internal static void RotateIfOversized(
        string path,
        long maxBytes,
        int archiveCount,
        Action<string>? report = null)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes || archiveCount <= 0)
                return;

            string oldest = path + "." + archiveCount;
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (int index = archiveCount - 1; index >= 1; index--)
            {
                string source = path + "." + index;
                if (File.Exists(source))
                    File.Move(source, path + "." + (index + 1), overwrite: true);
            }
            File.Move(path, path + ".1", overwrite: true);
            report?.Invoke("rotated " + path);
        }
        catch (Exception ex)
        {
            report?.Invoke($"rotation skipped for {path}: {ex.Message}");
        }
    }

    internal static void PruneNewestFiles(
        string directory,
        string pattern,
        int keep,
        Action<string>? report = null)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            foreach (FileInfo file in new DirectoryInfo(directory)
                         .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                         .OrderByDescending(item => item.LastWriteTimeUtc)
                         .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                         .Skip(Math.Max(0, keep)))
            {
                TryDeleteFile(file.FullName, report);
            }
        }
        catch (Exception ex)
        {
            report?.Invoke($"file retention skipped for {directory}: {ex.Message}");
        }
    }

    private static void DeleteOldFiles(
        string directory,
        IEnumerable<string> patterns,
        DateTime cutoffUtc,
        Action<string>? report)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string pattern in patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (string file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                        TryDeleteFile(file, report);
                }
                catch { }
            }
        }
    }

    private static void DeleteOldChildren(string directory, DateTime cutoffUtc, Action<string>? report)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            try
            {
                DateTime writeUtc = Directory.Exists(child)
                    ? Directory.GetLastWriteTimeUtc(child)
                    : File.GetLastWriteTimeUtc(child);
                if (writeUtc >= cutoffUtc)
                    continue;
                if (Directory.Exists(child))
                    Directory.Delete(child, recursive: true);
                else
                    File.Delete(child);
                report?.Invoke("deleted stale work item " + child);
            }
            catch (Exception ex)
            {
                report?.Invoke($"stale work cleanup skipped for {child}: {ex.Message}");
            }
        }
    }

    private static void DeleteAllChildren(string directory, Action<string>? report)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            try
            {
                if (Directory.Exists(child))
                    Directory.Delete(child, recursive: true);
                else
                    File.Delete(child);
                report?.Invoke("deleted completed update item " + child);
            }
            catch (Exception ex)
            {
                report?.Invoke($"completed update cleanup skipped for {child}: {ex.Message}");
            }
        }
    }

    private static void PruneComponentVersions(string componentsRoot, int keep, Action<string>? report)
    {
        if (!Directory.Exists(componentsRoot))
            return;

        HashSet<string> activeDirectories = ReadActiveComponentDirectories(componentsRoot);
        foreach (string componentDirectory in Directory.EnumerateDirectories(componentsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(componentDirectory).StartsWith(
                    BundleStagingDirectoryPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            DirectoryInfo[] versions;
            try
            {
                versions = new DirectoryInfo(componentDirectory)
                    .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(item => item.LastWriteTimeUtc)
                    .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            var retained = new HashSet<string>(
                versions.Take(Math.Max(0, keep)).Select(item => Path.GetFullPath(item.FullName)),
                StringComparer.OrdinalIgnoreCase);
            retained.UnionWith(activeDirectories);
            foreach (DirectoryInfo version in versions)
            {
                if (ContainsBundleTransactionBackup(version.FullName))
                    retained.Add(Path.GetFullPath(version.FullName));
            }
            foreach (DirectoryInfo version in versions)
            {
                string fullPath = Path.GetFullPath(version.FullName);
                if (retained.Contains(fullPath))
                    continue;
                try
                {
                    version.Delete(recursive: true);
                    report?.Invoke("deleted old component cache " + fullPath);
                }
                catch (Exception ex)
                {
                    report?.Invoke($"component cache cleanup skipped for {fullPath}: {ex.Message}");
                }
            }
        }
    }

    internal static void RecoverBundleCacheTransactions(
        string componentsRoot,
        DateTime utcNow,
        Action<string>? report = null)
    {
        if (!Directory.Exists(componentsRoot))
            return;

        DateTime staleBeforeUtc = utcNow - BundleTransactionRetention;
        var stages = new Dictionary<string, BundleStage>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateDirectories(
                     componentsRoot,
                     BundleStagingDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (!TryParseBundleStage(path, out string transactionId))
                continue;
            stages[transactionId] = new BundleStage(
                path,
                IsBundleTransactionActive(path),
                Directory.GetLastWriteTimeUtc(path));
        }

        var backupsByTarget = new Dictionary<string, List<BundleBackup>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string componentDirectory in Directory.EnumerateDirectories(
                     componentsRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(componentDirectory).StartsWith(
                    BundleStagingDirectoryPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string versionDirectory in Directory.EnumerateDirectories(
                         componentDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                foreach (string backupPath in Directory.EnumerateFiles(
                             versionDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!TryParseBundleBackup(backupPath, out string targetPath, out string transactionId))
                        continue;
                    if (!backupsByTarget.TryGetValue(targetPath, out List<BundleBackup>? backups))
                    {
                        backups = new List<BundleBackup>();
                        backupsByTarget[targetPath] = backups;
                    }
                    backups.Add(new BundleBackup(
                        backupPath,
                        transactionId,
                        File.GetLastWriteTimeUtc(backupPath)));
                }
            }
        }

        foreach ((string targetPath, List<BundleBackup> backups) in backupsByTarget)
        {
            if (!File.Exists(targetPath))
            {
                if (backups.Count == 1 && !IsActive(backups[0].TransactionId, stages))
                {
                    try
                    {
                        File.Move(backups[0].Path, targetPath, overwrite: false);
                        report?.Invoke("restored interrupted component cache " + targetPath);
                    }
                    catch (Exception ex)
                    {
                        report?.Invoke($"component cache restore skipped for {targetPath}: {ex.Message}");
                    }
                }
                else
                {
                    report?.Invoke(
                        $"component cache restore preserved ambiguous or active backup set for {targetPath}: count={backups.Count}");
                }
                continue;
            }

            foreach (BundleBackup backup in backups)
            {
                if (IsActive(backup.TransactionId, stages)
                    || backup.LastWriteTimeUtc >= staleBeforeUtc)
                {
                    continue;
                }
                TryDeleteFile(backup.Path, report);
            }
        }

        foreach (BundleStage stage in stages.Values)
        {
            if (stage.IsActive || stage.LastWriteTimeUtc >= staleBeforeUtc)
                continue;
            try
            {
                Directory.Delete(stage.Path, recursive: true);
                report?.Invoke("deleted stale bundle staging " + stage.Path);
            }
            catch (Exception ex)
            {
                report?.Invoke($"bundle staging cleanup skipped for {stage.Path}: {ex.Message}");
            }
        }
    }

    private static bool TryParseBundleStage(string path, out string transactionId)
    {
        string name = Path.GetFileName(path);
        transactionId = name.StartsWith(BundleStagingDirectoryPrefix, StringComparison.Ordinal)
            ? name[BundleStagingDirectoryPrefix.Length..]
            : "";
        return IsCanonicalTransactionId(transactionId);
    }

    private static bool TryParseBundleBackup(
        string backupPath,
        out string targetPath,
        out string transactionId)
    {
        targetPath = "";
        transactionId = "";
        string fileName = Path.GetFileName(backupPath);
        if (!fileName.EndsWith(BundleBackupSuffix, StringComparison.Ordinal))
            return false;
        int marker = fileName.LastIndexOf(BundleBackupMarker, StringComparison.Ordinal);
        if (marker <= 0)
            return false;
        string candidate = fileName.Substring(
            marker + BundleBackupMarker.Length,
            fileName.Length - marker - BundleBackupMarker.Length - BundleBackupSuffix.Length);
        if (!IsCanonicalTransactionId(candidate))
            return false;
        targetPath = Path.Combine(Path.GetDirectoryName(backupPath)!, fileName[..marker]);
        transactionId = candidate;
        return true;
    }

    private static bool ContainsBundleTransactionBackup(string versionDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(
                    versionDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Any(path => TryParseBundleBackup(path, out _, out _));
        }
        catch
        {
            // If the directory cannot be inspected, pruning cannot prove that
            // it is free of transaction recovery material.
            return true;
        }
    }

    private static bool IsCanonicalTransactionId(string value)
        => value.Length == 32
           && value.Equals(value.ToLowerInvariant(), StringComparison.Ordinal)
           && Guid.TryParseExact(value, "N", out _);

    private static bool IsBundleTransactionActive(string stagingRoot)
    {
        string lockPath = Path.Combine(stagingRoot, BundleTransactionLockFileName);
        if (!File.Exists(lockPath))
            return false;
        try
        {
            using var lease = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch
        {
            // Any inability to prove the lease is free keeps the transaction protected.
            return true;
        }
    }

    private static bool IsActive(
        string transactionId,
        IReadOnlyDictionary<string, BundleStage> stages)
        => stages.TryGetValue(transactionId, out BundleStage? stage) && stage.IsActive;

    private sealed record BundleStage(
        string Path,
        bool IsActive,
        DateTime LastWriteTimeUtc);

    private sealed record BundleBackup(
        string Path,
        string TransactionId,
        DateTime LastWriteTimeUtc);

    private static HashSet<string> ReadActiveComponentDirectories(string componentsRoot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string statePath = Path.Combine(componentsRoot, "component_state.json");
        try
        {
            if (!File.Exists(statePath))
                return result;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("Components", out JsonElement components)
                && !document.RootElement.TryGetProperty("components", out components))
                return result;
            foreach (JsonProperty component in components.EnumerateObject())
            {
                if (!component.Value.TryGetProperty("FilePath", out JsonElement pathElement)
                    && !component.Value.TryGetProperty("filePath", out pathElement))
                    continue;
                string? path = pathElement.GetString();
                string? directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(directory))
                    result.Add(directory);
            }
        }
        catch
        {
            // An unreadable state file disables component pruning for safety.
            result.Clear();
            foreach (string component in Directory.EnumerateDirectories(componentsRoot, "*", SearchOption.TopDirectoryOnly))
                foreach (string version in Directory.EnumerateDirectories(component, "*", SearchOption.TopDirectoryOnly))
                    result.Add(Path.GetFullPath(version));
        }
        return result;
    }

    private static void TryDeleteFile(string path, Action<string>? report)
    {
        try
        {
            File.Delete(path);
            report?.Invoke("deleted " + path);
        }
        catch (Exception ex)
        {
            report?.Invoke($"delete skipped for {path}: {ex.Message}");
        }
    }
}
