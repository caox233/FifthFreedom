using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SplinterCellCNLauncher.Services;

internal sealed record ComponentFileInstall(
    string SourcePath,
    string TargetPath,
    string ExpectedSha256);

internal sealed record ComponentFileDelete(
    string TargetPath,
    string ExpectedSha256);

/// <summary>
/// Replaces and removes component-owned files as one local transaction. Deletions
/// are first moved beside their exact targets and are restored on any later error.
/// </summary>
internal static class TransactionalComponentInstaller
{
    private sealed record PreparedFile(
        ComponentFileInstall Install,
        string TemporaryPath,
        string BackupPath,
        bool TargetExisted);

    private sealed record PreparedDelete(
        ComponentFileDelete Delete,
        string BackupPath,
        bool AlreadyMoved);

    internal static void Install(
        IReadOnlyCollection<ComponentFileInstall> files,
        Action<int>? beforeCommitForTest = null)
        => Install(files, Array.Empty<ComponentFileDelete>(), beforeCommitForTest);

    internal static void Install(
        IReadOnlyCollection<ComponentFileInstall> files,
        IReadOnlyCollection<ComponentFileDelete> deletes,
        Action<int>? beforeCommitForTest = null)
    {
        if (files.Count == 0 && deletes.Count == 0)
            return;

        string[] duplicateTargets = files.Select(item => item.TargetPath)
            .Concat(deletes.Select(item => item.TargetPath))
            .GroupBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateTargets.Length > 0)
            throw new InvalidDataException("Component transaction contains duplicate target files: " + string.Join(", ", duplicateTargets));

        var preparedFiles = new List<PreparedFile>();
        var preparedDeletes = new List<PreparedDelete>();
        try
        {
            foreach (ComponentFileInstall install in files)
            {
                ValidateInstall(install);
                string target = Path.GetFullPath(install.TargetPath);
                string temporary = target + ".component-new";
                string backup = target + ".component-backup";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                RecoverTarget(target, temporary, backup, install.ExpectedSha256);

                if (File.Exists(target)
                    && ComputeSha256(target).Equals(install.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryDelete(temporary);
                File.Copy(install.SourcePath, temporary, overwrite: true);
                VerifyHash(temporary, install.ExpectedSha256, "component temporary file");
                preparedFiles.Add(new PreparedFile(
                    install with { TargetPath = target },
                    temporary,
                    backup,
                    File.Exists(target)));
            }

            foreach (ComponentFileDelete delete in deletes)
            {
                ValidateDelete(delete);
                string target = Path.GetFullPath(delete.TargetPath);
                string backup = target + ".component-delete-backup";
                bool alreadyMoved = false;
                if (File.Exists(backup))
                {
                    VerifyHash(backup, delete.ExpectedSha256, "component deletion backup");
                    if (File.Exists(target))
                        TryDelete(backup);
                    else
                        alreadyMoved = true;
                }
                if (!alreadyMoved)
                    VerifyHash(target, delete.ExpectedSha256, "component-owned deletion target");
                preparedDeletes.Add(new PreparedDelete(
                    delete with { TargetPath = target },
                    backup,
                    alreadyMoved));
            }

            int index = 0;
            foreach (PreparedFile item in preparedFiles)
            {
                beforeCommitForTest?.Invoke(index++);
                TryDelete(item.BackupPath);
                if (item.TargetExisted)
                    File.Move(item.Install.TargetPath, item.BackupPath, overwrite: true);
                File.Move(item.TemporaryPath, item.Install.TargetPath, overwrite: true);
                VerifyHash(item.Install.TargetPath, item.Install.ExpectedSha256, "component installed file");
            }

            foreach (PreparedDelete item in preparedDeletes)
            {
                beforeCommitForTest?.Invoke(index++);
                if (!item.AlreadyMoved)
                    File.Move(item.Delete.TargetPath, item.BackupPath, overwrite: false);
            }

            foreach (PreparedFile item in preparedFiles)
                TryDelete(item.BackupPath);
            foreach (PreparedDelete item in preparedDeletes)
                TryDelete(item.BackupPath);
        }
        catch
        {
            foreach (PreparedDelete item in preparedDeletes.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(item.BackupPath) && !File.Exists(item.Delete.TargetPath))
                        File.Move(item.BackupPath, item.Delete.TargetPath, overwrite: false);
                }
                catch
                {
                    // Leave the exact backup beside its target for the next attempt.
                }
            }
            foreach (PreparedFile item in preparedFiles.AsEnumerable().Reverse())
            {
                TryDelete(item.TemporaryPath);
                try
                {
                    if (File.Exists(item.BackupPath))
                    {
                        TryDelete(item.Install.TargetPath);
                        File.Move(item.BackupPath, item.Install.TargetPath, overwrite: true);
                    }
                    else if (!item.TargetExisted)
                    {
                        TryDelete(item.Install.TargetPath);
                    }
                }
                catch
                {
                    // Preserve the original exception and the surviving recovery evidence.
                }
            }
            throw;
        }
    }

    private static void RecoverTarget(
        string target,
        string temporary,
        string backup,
        string expectedSha256)
    {
        TryDelete(temporary);
        if (!File.Exists(backup))
            return;

        if (File.Exists(target))
        {
            try
            {
                if (ComputeSha256(target).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(backup);
                    return;
                }
            }
            catch { }
            TryDelete(target);
        }
        File.Move(backup, target, overwrite: true);
    }

    private static void ValidateInstall(ComponentFileInstall install)
    {
        if (string.IsNullOrWhiteSpace(install.SourcePath) || !File.Exists(install.SourcePath))
            throw new FileNotFoundException("Component source file does not exist.", install.SourcePath);
        if (string.IsNullOrWhiteSpace(install.TargetPath))
            throw new InvalidDataException("Component target file is empty.");
        if (!IsSha256(install.ExpectedSha256))
            throw new InvalidDataException("Component file SHA256 is invalid.");
        VerifyHash(install.SourcePath, install.ExpectedSha256, "component source file");
    }

    private static void ValidateDelete(ComponentFileDelete delete)
    {
        if (string.IsNullOrWhiteSpace(delete.TargetPath))
            throw new InvalidDataException("Component-owned deletion target is empty.");
        string target = Path.GetFullPath(delete.TargetPath);
        if (!File.Exists(target) && !File.Exists(target + ".component-delete-backup"))
            throw new FileNotFoundException("Component-owned deletion target does not exist.", target);
        if (!IsSha256(delete.ExpectedSha256))
            throw new InvalidDataException("Component deletion SHA256 is invalid.");
    }

    private static void VerifyHash(string path, string expected, string label)
    {
        string actual = ComputeSha256(path);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"{label} SHA256 mismatch. expected={expected}, actual={actual}");
    }

    internal static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsSha256(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
