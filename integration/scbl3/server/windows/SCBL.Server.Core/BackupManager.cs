using System.IO.Compression;
using System.Text.Json;

namespace Scbl.Server;

public sealed class BackupManager
{
    private readonly ServerPaths _paths;
    private readonly ServerLog _log;

    public BackupManager(ServerPaths paths, ServerLog log) { _paths = paths; _log = log; }

    public string Create()
    {
        _ = _paths.RequireCurrentRelease();
        Directory.CreateDirectory(_paths.Backups);
        string destination = Path.Combine(_paths.Backups, $"scbl-backup-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.zip");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (ZipArchive archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                AddIfPresent(archive, _paths.Config, "config/server.json");
                AddIfPresent(archive, _paths.Secrets, "config/secrets.json");
                AddIfPresent(archive, _paths.Database, "data/dedicated/5th-echelon.db");
                AddIfPresent(archive, _paths.Database + "-wal", "data/dedicated/5th-echelon.db-wal");
                AddIfPresent(archive, _paths.Database + "-shm", "data/dedicated/5th-echelon.db-shm");
                AddIfPresent(archive, _paths.GameplayModeState, "data/runtime/gameplay-modes.json");
                AddIfPresent(archive, _paths.CurrentReleasePointer, "state/current-release.json");
                AddIfPresent(archive, _paths.PreviousReleasePointer, "state/previous-release.json");
            }
            File.Move(temporary, destination);
        }
        finally { File.Delete(temporary); }
        File.WriteAllText(destination + ".sha256", $"{Hashing.Sha256(destination)}  {Path.GetFileName(destination)}{Environment.NewLine}");
        WindowsSecurity.RestrictFile(destination);
        WindowsSecurity.RestrictFile(destination + ".sha256");
        Prune(3);
        _log.Write("backup", $"created {Path.GetFileName(destination)}");
        return destination;
    }

    public void Restore(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("备份不存在。", archivePath);
        string sidecar = archivePath + ".sha256";
        if (!File.Exists(sidecar)) throw new InvalidDataException("备份缺少 SHA256 校验文件。");
        string[] sidecarFields = File.ReadAllText(sidecar).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string expected = sidecarFields.FirstOrDefault() ?? "";
        if (sidecarFields.Length < 2 || !string.Equals(sidecarFields[1], Path.GetFileName(archivePath), StringComparison.Ordinal))
        {
            throw new InvalidDataException("备份 SHA256 文件与目标备份不匹配。");
        }
        if (!Hashing.FixedEqualsHex(Hashing.Sha256(archivePath), expected)) throw new InvalidDataException("备份 SHA256 校验失败。");
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ValidateReleasePointer(archive, "state/current-release.json");
        ValidateReleasePointer(archive, "state/previous-release.json");
        (string Entry, string Target)[] entries =
        [
            ("config/server.json", _paths.Config),
            ("config/secrets.json", _paths.Secrets),
            ("data/dedicated/5th-echelon.db", _paths.Database),
            ("data/dedicated/5th-echelon.db-wal", _paths.Database + "-wal"),
            ("data/dedicated/5th-echelon.db-shm", _paths.Database + "-shm"),
            ("data/runtime/gameplay-modes.json", _paths.GameplayModeState),
            ("state/current-release.json", _paths.CurrentReleasePointer),
            ("state/previous-release.json", _paths.PreviousReleasePointer),
        ];
        FileStateSnapshot[] before = entries.Select(entry => FileStateSnapshot.Capture(entry.Target)).ToArray();
        try
        {
            foreach ((string entry, string target) in entries) RestoreEntry(archive, entry, target);
        }
        catch (Exception original)
        {
            List<Exception> rollbackErrors = [];
            foreach (FileStateSnapshot snapshot in before)
            {
                try { snapshot.Restore(); }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            if (rollbackErrors.Count != 0) throw new AggregateException("备份恢复失败且原状态未能完整恢复。", [original, .. rollbackErrors]);
            throw;
        }
        _log.Write("backup", $"restored {Path.GetFileName(archivePath)}");
    }

    private static void AddIfPresent(ZipArchive archive, string source, string name)
    {
        if (File.Exists(source)) archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
    }

    private static void RestoreEntry(ZipArchive archive, string name, string target)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null)
        {
            File.Delete(target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".restore." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (Stream input = entry.Open())
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
            WindowsSecurity.RestrictFile(target);
        }
        finally { File.Delete(temporary); }
    }

    private void ValidateReleasePointer(ZipArchive archive, string name)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null) return;
        using Stream stream = entry.Open();
        ReleasePointer pointer = JsonSerializer.Deserialize<ReleasePointer>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"备份中的 {name} 无效。");
        try { _ = _paths.RequireRelease(pointer); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException($"备份引用的 Runtime 无效：{pointer.Directory}", error);
        }
    }

    public IReadOnlySet<string> ProtectedReleaseDirectories()
    {
        HashSet<string> protectedDirectories = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_paths.Backups)) return protectedDirectories;
        foreach (string file in Directory.EnumerateFiles(_paths.Backups, "scbl-backup-*.zip"))
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(file);
                foreach (string name in new[] { "state/current-release.json", "state/previous-release.json" })
                {
                    ZipArchiveEntry? entry = archive.GetEntry(name);
                    if (entry is null) continue;
                    using Stream stream = entry.Open();
                    ReleasePointer? pointer = JsonSerializer.Deserialize<ReleasePointer>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (pointer is not null && PathSafety.IsInside(Path.GetFullPath(Path.Combine(_paths.Releases, pointer.Directory)), _paths.Releases))
                    {
                        protectedDirectories.Add(pointer.Directory);
                    }
                }
            }
            catch (InvalidDataException error) { _log.Write("backup", $"ignored unreadable backup {Path.GetFileName(file)}: {error.Message}"); }
            catch (JsonException error) { _log.Write("backup", $"ignored invalid backup {Path.GetFileName(file)}: {error.Message}"); }
            catch (IOException error) { _log.Write("backup", $"could not inspect backup {Path.GetFileName(file)}: {error.Message}"); }
            catch (UnauthorizedAccessException error) { _log.Write("backup", $"could not access backup {Path.GetFileName(file)}: {error.Message}"); }
        }
        return protectedDirectories;
    }

    private void Prune(int keep)
    {
        FileInfo[] files = new DirectoryInfo(_paths.Backups).GetFiles("scbl-backup-*.zip").OrderByDescending(file => file.CreationTimeUtc).ToArray();
        foreach (FileInfo file in files.Skip(keep))
        {
            file.Delete();
            File.Delete(file.FullName + ".sha256");
        }
    }
}
