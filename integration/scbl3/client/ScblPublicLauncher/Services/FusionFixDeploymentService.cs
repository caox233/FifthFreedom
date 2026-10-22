using System.IO.Compression;
using System.Security.Cryptography;

namespace SplinterCellCNLauncher.Services;

public enum FusionFixDeploymentOutcome
{
    Installed,
    AlreadyPresent,
    ExistingModPreserved
}

public sealed record FusionFixDeploymentResult(
    FusionFixDeploymentOutcome Outcome,
    int InstalledFileCount,
    int PreservedFileCount = 0,
    string ReasonCode = "none",
    string? ConflictRelativePath = null);

/// <summary>
/// Installs the frozen FusionFix payload without overwriting player files. The
/// player-owned FusionFix INI is never opened, copied, modified, or deleted.
/// Existing configuration is identified by name only; existing binaries are
/// reused when they have a valid Windows MZ header. The embedded source asset
/// itself remains bound to its frozen SHA256.
/// </summary>
public sealed class FusionFixDeploymentService
{
    public const string AssetFileName = "FusionFix.zip";
    public const string AssetSha256 = "B807D01B849CA7AEAD6C04A43EAA2924809D0741E7007B907E00DA868F33A7FF";
    public const string ProtectedIniRelativePath = "SYSTEM/scripts/SplinterCellBlacklist.FusionFix.ini";

    private readonly Func<Stream> _openAsset;
    private readonly string _expectedAssetSha256;
    private readonly Action<FusionFixDeploymentResult> _reportResult;

    public FusionFixDeploymentService()
        : this(
            () => EmbeddedResourceService.OpenEmbeddedFileStrict(AssetFileName),
            AssetSha256)
    {
    }

    internal FusionFixDeploymentService(
        Func<Stream> openAsset,
        string expectedAssetSha256,
        Action<FusionFixDeploymentResult>? reportResult = null)
    {
        _openAsset = openAsset ?? throw new ArgumentNullException(nameof(openAsset));
        _expectedAssetSha256 = NormalizeSha256(expectedAssetSha256);
        _reportResult = reportResult ?? LogResult;
    }

    public Task<FusionFixDeploymentResult> EnsureInstalledAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => EnsureInstalled(gameDirectory, cancellationToken),
            cancellationToken);

    private FusionFixDeploymentResult EnsureInstalled(
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        string gameRoot = NormalizeGameRoot(gameDirectory);
        byte[] assetBytes;
        using (Stream source = _openAsset())
        using (var buffer = new MemoryStream())
        {
            source.CopyTo(buffer);
            assetBytes = buffer.ToArray();
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(assetBytes));
        if (!actualSha256.Equals(_expectedAssetSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"FusionFix asset hash mismatch: expected={_expectedAssetSha256}, actual={actualSha256}.");
        }

        using var archiveBuffer = new MemoryStream(assetBytes, writable: false);
        using var archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Read, leaveOpen: false);
        IReadOnlyList<InstallEntry> entries = BuildInstallPlan(archive, gameRoot);
        if (entries.Count == 0)
            throw new InvalidDataException("FusionFix asset contains no installable files below src/.");

        ExistingModInspection existing = InspectExistingMod(gameRoot, entries);
        if (existing.State != ExistingModState.IncompleteSafeToInstall)
        {
            FusionFixDeploymentResult result = new(
                existing.State == ExistingModState.CompleteFrozenPayload
                    ? FusionFixDeploymentOutcome.AlreadyPresent
                    : FusionFixDeploymentOutcome.ExistingModPreserved,
                InstalledFileCount: 0,
                PreservedFileCount: existing.ExistingFileCount,
                ReasonCode: existing.ReasonCode,
                ConflictRelativePath: existing.ConflictRelativePath);
            _reportResult(result);
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string stagingRoot = Path.Combine(
            gameRoot,
            ".scbl-staging",
            "fusionfix-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<InstallEntry> installEntries = existing.MissingEntries;
        var createdTargets = new List<string>(installEntries.Count);
        int preservedDuringInstall = 0;
        Exception? primaryError = null;
        var rollbackErrors = new List<Exception>();

        try
        {
            foreach (InstallEntry item in installEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(stagingRoot, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                using Stream input = item.Entry.Open();
                using var output = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
                if (output.Length != item.Entry.Length)
                    throw new InvalidDataException($"FusionFix staged size mismatch: {item.RelativePath}.");
            }

            foreach (InstallEntry item in installEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(stagingRoot, item.RelativePath);
                if (File.Exists(item.TargetPath))
                {
                    if (item.IsPlayerEditable)
                    {
                        File.Delete(stagedPath);
                        preservedDuringInstall++;
                        continue;
                    }
                    throw new IOException(
                        $"A MOD file appeared during FusionFix installation; no existing file was replaced: {item.RelativePath}.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                File.Move(stagedPath, item.TargetPath, overwrite: false);
                createdTargets.Add(item.TargetPath);
            }

            VerifyInstalledState(entries);
            FusionFixDeploymentResult result = new(
                FusionFixDeploymentOutcome.Installed,
                InstalledFileCount: createdTargets.Count,
                PreservedFileCount: existing.ExistingFileCount + preservedDuringInstall,
                ReasonCode: existing.ExistingFileCount + preservedDuringInstall == 0
                    ? "installed-frozen-payload"
                    : "completed-missing-files");
            _reportResult(result);
            return result;
        }
        catch (Exception ex)
        {
            primaryError = ex;
            throw;
        }
        finally
        {
            if (primaryError != null)
            {
                for (int index = createdTargets.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        File.Delete(createdTargets[index]);
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackErrors.Add(rollbackError);
                    }
                }
            }

            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch (Exception cleanupError)
            {
                if (primaryError != null)
                    rollbackErrors.Add(cleanupError);
            }

            if (primaryError != null && rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "FusionFix installation failed and rollback was incomplete.",
                    [primaryError, .. rollbackErrors]);
            }
        }
    }

    private static IReadOnlyList<InstallEntry> BuildInstallPlan(
        ZipArchive archive,
        string gameRoot)
    {
        var result = new List<InstallEntry>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string rootPrefix = gameRoot.EndsWith(Path.DirectorySeparatorChar)
            ? gameRoot
            : gameRoot + Path.DirectorySeparatorChar;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string fullName = entry.FullName;
            if (fullName.EndsWith('/'))
                continue;
            if (!fullName.StartsWith("src/", StringComparison.Ordinal)
                || fullName.Contains('\\'))
            {
                throw new InvalidDataException($"FusionFix archive entry is outside src/: {fullName}.");
            }

            string relativeArchivePath = fullName[4..];
            string[] segments = relativeArchivePath.Split('/');
            if (segments.Length == 0
                || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                    || segment is "." or ".."))
            {
                throw new InvalidDataException($"FusionFix archive entry is not canonical: {fullName}.");
            }

            string normalizedRelativePath = string.Join('/', segments);
            if (normalizedRelativePath.Equals(
                    ProtectedIniRelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Intentionally do not call entry.Open() for the player-owned INI.
                continue;
            }

            string relativePath = Path.Combine(segments);
            string targetPath = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
            if (!targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !targets.Add(targetPath))
            {
                throw new InvalidDataException($"FusionFix archive target is unsafe or duplicated: {fullName}.");
            }

            bool isPlayerEditable = IsPlayerEditableConfiguration(relativePath);
            result.Add(new InstallEntry(
                entry,
                relativePath,
                targetPath,
                isPlayerEditable));
        }

        return result;
    }

    private static ExistingModInspection InspectExistingMod(
        string gameRoot,
        IReadOnlyList<InstallEntry> entries)
    {
        var missingEntries = new List<InstallEntry>();
        int existingFileCount = 0;
        foreach (InstallEntry entry in entries)
        {
            if (!File.Exists(entry.TargetPath))
            {
                missingEntries.Add(entry);
                continue;
            }

            existingFileCount++;
            if (entry.IsPlayerEditable)
                continue;
            if (!IsUsableWindowsBinary(entry.TargetPath))
            {
                return new ExistingModInspection(
                    ExistingModState.ConflictingModPreserved,
                    missingEntries,
                    existingFileCount,
                    ReasonCode: "existing-binary-invalid",
                    ConflictRelativePath: NormalizeRelativePath(entry.RelativePath));
            }
        }

        return missingEntries.Count == 0
            ? new ExistingModInspection(
                ExistingModState.CompleteFrozenPayload,
                missingEntries,
                existingFileCount,
                ReasonCode: "complete-usable-payload",
                ConflictRelativePath: null)
            : new ExistingModInspection(
                ExistingModState.IncompleteSafeToInstall,
                missingEntries,
                existingFileCount,
                ReasonCode: "missing-install-files",
                ConflictRelativePath: null);
    }

    private static bool IsUsableWindowsBinary(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return input.ReadByte() == 'M' && input.ReadByte() == 'Z';
    }

    private static void VerifyInstalledState(IReadOnlyList<InstallEntry> entries)
    {
        foreach (InstallEntry entry in entries)
        {
            if (!File.Exists(entry.TargetPath))
            {
                throw new InvalidDataException(
                    $"FusionFix install target is missing after deployment: {NormalizeRelativePath(entry.RelativePath)}.");
            }
            if (!entry.IsPlayerEditable && !IsUsableWindowsBinary(entry.TargetPath))
            {
                throw new InvalidDataException(
                    $"FusionFix binary is not a usable Windows DLL after deployment: {NormalizeRelativePath(entry.RelativePath)}.");
            }
        }
    }

    private static bool IsPlayerEditableConfiguration(string relativePath)
        => Path.GetExtension(relativePath).ToLowerInvariant() is
            ".ini" or ".xml" or ".toml" or ".json" or ".cfg" or ".conf";

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private static void LogResult(FusionFixDeploymentResult result)
    {
        string message =
            $"FusionFix deployment: outcome={result.Outcome}, reason={result.ReasonCode}, " +
            $"installed={result.InstalledFileCount}, preserved={result.PreservedFileCount}" +
            (string.IsNullOrWhiteSpace(result.ConflictRelativePath)
                ? "."
                : $", conflict={result.ConflictRelativePath}.");
        if (result.Outcome == FusionFixDeploymentOutcome.ExistingModPreserved)
            LogService.Warning(message);
        else
            LogService.Info(message);
    }

    private static string NormalizeGameRoot(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new ArgumentException("A game directory is required.", nameof(gameDirectory));
        string root = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Game directory does not exist: {root}");
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var selected = new DirectoryInfo(root);
        if (selected.Name.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            && selected.Parent != null)
        {
            // Launcher settings intentionally point at src/SYSTEM because the
            // game executable lives there. The FusionFix archive is rooted at
            // src/, so installation must start from the parent src directory.
            return selected.Parent.FullName.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        string nestedSourceRoot = Path.Combine(root, "src");
        if (Directory.Exists(Path.Combine(nestedSourceRoot, "SYSTEM")))
            return Path.GetFullPath(nestedSourceRoot);

        return root;
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException("A 64-character SHA256 is required.", nameof(value));
        return normalized.ToUpperInvariant();
    }

    private sealed record InstallEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        string TargetPath,
        bool IsPlayerEditable);

    private sealed record ExistingModInspection(
        ExistingModState State,
        IReadOnlyList<InstallEntry> MissingEntries,
        int ExistingFileCount,
        string ReasonCode,
        string? ConflictRelativePath);

    private enum ExistingModState
    {
        IncompleteSafeToInstall,
        CompleteFrozenPayload,
        ConflictingModPreserved
    }
}
