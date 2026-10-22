using System.IO.Compression;

internal sealed class PackageInstallPlanner
{
    internal const int MaximumArchiveEntries = 20_000;
    internal const long MaximumSingleFileBytes = 1024L * 1024 * 1024;
    internal const long MaximumTotalFileBytes = 4L * 1024 * 1024 * 1024;

    public InstallPlan Prepare(
        string package,
        string target,
        UpdatePackageKind packageKind,
        string version)
    {
        string normalizedTarget = UpdaterPaths.NormalizeTarget(target);
        string transactionId = Guid.NewGuid().ToString("N");
        string transactionRoot = Path.Combine(
            UpdaterPaths.GetTransactionsDirectory(normalizedTarget),
            transactionId);
        string stagingRoot = Path.Combine(transactionRoot, "staging");
        string backupRoot = Path.Combine(transactionRoot, "backup");
        UpdaterPaths.EnsureNoReparseTraversal(normalizedTarget, transactionRoot);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            ArchiveExtraction extraction = ExtractValidatedArchive(package, stagingRoot);
            string contentRoot = packageKind == UpdatePackageKind.LauncherComponent
                ? ValidateLauncherComponent(stagingRoot, extraction.Files)
                : DetermineFullPackageContentRoot(
                    stagingRoot,
                    extraction.Entries,
                    extraction.Files);
            UpdaterLog.Write($"Validated update content root: {contentRoot}");

            var packageDirectories = packageKind == UpdatePackageKind.LauncherComponent
                ? Array.Empty<string>()
                : Directory.GetDirectories(contentRoot, "*", SearchOption.AllDirectories)
                    .Select(directory => UpdaterPaths.NormalizeSafeRelativePath(
                        Path.GetRelativePath(contentRoot, directory)))
                    .Where(relative => !UpdaterPaths.IsExcludedFromFullPackage(relative))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            string[] candidateFiles = Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories)
                .Select(path => UpdaterPaths.NormalizeSafeRelativePath(
                    Path.GetRelativePath(contentRoot, path)))
                .Where(relative => packageKind == UpdatePackageKind.LauncherComponent
                    || !UpdaterPaths.IsExcludedFromFullPackage(relative))
                .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidateFiles.Length > MaximumArchiveEntries)
                throw new InvalidDataException("Update mutation plan contains too many files.");

            var duplicateTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<PlannedFileMutation>();
            foreach (string relative in candidateFiles)
            {
                if (!duplicateTargets.Add(relative))
                    throw new InvalidDataException($"Update mutation plan contains a duplicate path: {relative}");

                string source = UpdaterPaths.ResolveContainedPath(contentRoot, relative);
                string destination = UpdaterPaths.ResolveContainedPath(normalizedTarget, relative);
                var sourceInfo = new FileInfo(source);
                if (!sourceInfo.Exists
                    || sourceInfo.Length < 0
                    || sourceInfo.Length > MaximumSingleFileBytes)
                {
                    throw new InvalidDataException($"Update file has an invalid size: {relative}");
                }

                string sourceSha256 = FileIntegrity.ComputeSha256(source);
                bool targetExisted = File.Exists(destination);
                long? originalSize = null;
                string? originalSha256 = null;
                if (targetExisted)
                {
                    var destinationInfo = new FileInfo(destination);
                    originalSize = destinationInfo.Length;
                    originalSha256 = FileIntegrity.ComputeSha256(destination);
                    if (destinationInfo.Length == sourceInfo.Length
                        && originalSha256.Equals(sourceSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdaterLog.Write("Unchanged file omitted from mutation plan: " + relative);
                        continue;
                    }
                }
                else if (Directory.Exists(destination))
                {
                    throw new InvalidDataException($"Update file collides with an installed directory: {relative}");
                }

                files.Add(new PlannedFileMutation(
                    relative,
                    source,
                    destination,
                    UpdaterPaths.ResolveContainedPath(backupRoot, relative),
                    sourceInfo.Length,
                    sourceSha256,
                    targetExisted,
                    originalSize,
                    originalSha256));
            }

            string[] createdDirectories = BuildCreatedDirectoryPlan(
                normalizedTarget,
                packageDirectories,
                files.Select(file => file.RelativePath));
            return new InstallPlan(
                transactionId,
                normalizedTarget,
                transactionRoot,
                stagingRoot,
                backupRoot,
                UpdaterPaths.GetJournalPath(normalizedTarget),
                version,
                packageKind,
                files,
                packageDirectories,
                createdDirectories);
        }
        catch
        {
            TryDeleteOwnedDirectory(transactionRoot);
            throw;
        }
    }

    private static ArchiveExtraction ExtractValidatedArchive(
        string package,
        string stagingRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("Update archive has an invalid entry count.");

        var descriptors = new List<(ZipArchiveEntry Entry, string Relative, bool IsDirectory)>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = UpdaterPaths.NormalizeSafeRelativePath(entry.FullName);
            bool isDirectory = string.IsNullOrEmpty(entry.Name)
                || entry.FullName.EndsWith('/')
                || entry.FullName.EndsWith('\\');
            if (!allPaths.Add(relative))
                throw new InvalidDataException($"Update archive contains a duplicate path: {relative}");
            if (IsLinkOrReparsePoint(entry))
                throw new InvalidDataException($"Update archive contains a link or reparse point: {relative}");

            if (!isDirectory)
            {
                if (entry.Length < 0 || entry.Length > MaximumSingleFileBytes)
                    throw new InvalidDataException($"Update archive entry is too large: {relative}");
                totalSize = checked(totalSize + entry.Length);
                if (totalSize > MaximumTotalFileBytes)
                    throw new InvalidDataException("Update archive exceeds the total uncompressed size limit.");
                filePaths.Add(relative);
            }

            descriptors.Add((entry, relative, isDirectory));
        }

        foreach ((_, string relative, _) in descriptors)
        {
            string[] parts = relative.Split('/');
            string prefix = "";
            for (int index = 0; index < parts.Length - 1; index++)
            {
                prefix = index == 0 ? parts[index] : prefix + "/" + parts[index];
                if (filePaths.Contains(prefix))
                    throw new InvalidDataException($"Update archive has a file/directory collision: {relative}");
            }
        }

        foreach ((ZipArchiveEntry entry, string relative, bool isDirectory) in descriptors
                     .OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase))
        {
            string destination = UpdaterPaths.ResolveContainedPath(stagingRoot, relative);
            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
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
            if (new FileInfo(destination).Length != entry.Length)
                throw new InvalidDataException($"Extracted update file size mismatch: {relative}");
        }

        string[] entries = descriptors
            .Select(item => item.Relative)
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] files = descriptors
            .Where(item => !item.IsDirectory)
            .Select(item => item.Relative)
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ArchiveExtraction(entries, files);
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        bool unixSymlink = unixType == 0xA000;
        bool windowsReparsePoint = (entry.ExternalAttributes
            & (int)FileAttributes.ReparsePoint) != 0;
        return unixSymlink || windowsReparsePoint;
    }

    private static string ValidateLauncherComponent(
        string stagingRoot,
        IReadOnlyList<string> files)
    {
        if (files.Count != 1
            || !files[0].Equals("SCBL.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Launcher component package must contain only SCBL.exe at its root.");
        }
        return stagingRoot;
    }

    private static string DetermineFullPackageContentRoot(
        string extractionRoot,
        IReadOnlyList<string> extractedEntries,
        IReadOnlyList<string> extractedFiles)
    {
        string[] candidates = extractedFiles
            .Where(relative => Path.GetFileName(relative)
                .Equals("SCBL.exe", StringComparison.OrdinalIgnoreCase))
            .Select(relative => Path.GetDirectoryName(
                relative.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty)
            .Select(relative => string.IsNullOrEmpty(relative)
                ? extractionRoot
                : UpdaterPaths.ResolveContainedPath(
                    extractionRoot,
                    relative.Replace(Path.DirectorySeparatorChar, '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException(
                candidates.Length == 0
                    ? "Full update package must contain SCBL.exe at its content root."
                    : "Full update package contains multiple possible SCBL.exe content roots.");
        }

        string contentRoot = candidates[0];
        foreach (string relative in extractedEntries)
        {
            string stagedEntry = UpdaterPaths.ResolveContainedPath(
                extractionRoot,
                relative);
            if (!stagedEntry.Equals(contentRoot, StringComparison.OrdinalIgnoreCase)
                && !UpdaterPaths.IsContainedPath(contentRoot, stagedEntry))
            {
                throw new InvalidDataException(
                    $"Full update package contains an entry outside its content root: {relative}");
            }
        }
        return contentRoot;
    }

    private sealed record ArchiveExtraction(
        IReadOnlyList<string> Entries,
        IReadOnlyList<string> Files);

    private static string[] BuildCreatedDirectoryPlan(
        string targetRoot,
        IEnumerable<string> packageDirectories,
        IEnumerable<string> filePaths)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in packageDirectories)
            AddDirectoryAndParents(directory, candidates);
        foreach (string file in filePaths)
        {
            string? directory = Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(directory))
                AddDirectoryAndParents(directory.Replace(Path.DirectorySeparatorChar, '/'), candidates);
        }

        return candidates
            .Where(relative => !Directory.Exists(UpdaterPaths.ResolveContainedPath(targetRoot, relative)))
            .OrderBy(relative => relative.Count(character => character == '/'))
            .ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddDirectoryAndParents(
        string relative,
        ISet<string> directories)
    {
        string normalized = UpdaterPaths.NormalizeSafeRelativePath(relative);
        string[] parts = normalized.Split('/');
        string current = "";
        foreach (string part in parts)
        {
            current = string.IsNullOrEmpty(current) ? part : current + "/" + part;
            directories.Add(current);
        }
    }

    private static void TryDeleteOwnedDirectory(string transactionRoot)
    {
        try
        {
            if (Directory.Exists(transactionRoot))
                Directory.Delete(transactionRoot, recursive: true);
        }
        catch (Exception exception)
        {
            UpdaterLog.Write(
                $"Staging cleanup retained transaction evidence at {transactionRoot}: {exception.Message}");
        }
    }
}
