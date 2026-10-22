using System.Security.Cryptography;
using System.Text.Json;
using SCBL.Client.Shared;

internal static class UpdaterPaths
{
    private static readonly string[] ExcludedDirectories =
    {
        "data/",
        "temp/",
        "logs/",
        "updates/",
        "backup/",
        "backup_saves/"
    };

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher_settings.json",
        "update_manifest.json",
        "client_update_manifest.json",
        "client_package_info.json",
        "client_package_manifest.json"
    };

    public static string InferTargetDirectory()
    {
        string baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(baseDirectory, "SCBL.exe")))
            return baseDirectory;

        string current = Directory.GetCurrentDirectory();
        return File.Exists(Path.Combine(current, "SCBL.exe")) ? current : baseDirectory;
    }

    public static string NormalizeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Updater target must not be empty.", nameof(target));

        string fullPath = Path.GetFullPath(target);
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
            throw new InvalidDataException("Updater target has no filesystem root.");

        string normalized = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedRoot = pathRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Updater target must not be a filesystem root.");
        }

        return normalized;
    }

    public static string GetClientDataDirectory(string target)
        => ClientDataLayout.Resolve(NormalizeTarget(target));

    public static string GetUpdatesDirectory(string target)
        => Path.Combine(GetClientDataDirectory(target), "updates");

    public static string GetJournalPath(string target)
        => Path.Combine(GetUpdatesDirectory(target), "active_install_transaction.json");

    public static string GetTransactionsDirectory(string target)
        => Path.Combine(GetUpdatesDirectory(target), "transactions");

    public static string NormalizeSafeRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\0') >= 0)
            throw new InvalidDataException("Update package contains an empty or invalid path.");

        string normalized = relative.Replace('\\', '/');
        if (!normalized.Equals(normalized.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException($"Update package path has leading or trailing whitespace: {relative}");
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new InvalidDataException($"Update package contains a rooted path: {relative}");

        normalized = normalized.TrimEnd('/');
        string[] parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Length == 0
            || parts.Any(part => string.IsNullOrWhiteSpace(part)
                || part is "." or ".."
                || part.Contains(':')
                || part.EndsWith('.')
                || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsName(part)))
        {
            throw new InvalidDataException($"Update package contains an unsafe relative path: {relative}");
        }

        return string.Join('/', parts);
    }

    private static bool IsReservedWindowsName(string part)
    {
        string stem = part.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }

    public static bool IsExcludedFromFullPackage(string relative)
    {
        string normalized = NormalizeSafeRelativePath(relative);
        foreach (string directory in ExcludedDirectories)
        {
            string prefix = directory.TrimEnd('/');
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ExcludedFiles.Contains(normalized)
            || ExcludedFiles.Contains(Path.GetFileName(normalized));
    }

    public static string ResolveContainedPath(string root, string relative)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            NormalizeSafeRelativePath(relative).Replace('/', Path.DirectorySeparatorChar)));
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Update path escapes its root: {relative}");
        EnsureNoReparseTraversal(normalizedRoot, candidate);
        return candidate;
    }

    public static void EnsureNoReparseTraversal(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update path escapes its validated root.");
        }

        string current = normalizedRoot;
        CheckExistingPathForReparsePoint(current);
        if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return;
        string relative = normalizedCandidate[prefix.Length..];
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            CheckExistingPathForReparsePoint(current);
        }
    }

    private static void CheckExistingPathForReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Update path traverses a reparse point: {path}");
    }

    public static bool IsContainedPath(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class UpdaterLog
{
    private static string? _clientRoot;

    public static void Initialize(string target) => _clientRoot = UpdaterPaths.NormalizeTarget(target);

    public static void Write(string message)
    {
        try
        {
            string root = _clientRoot ?? UpdaterPaths.InferTargetDirectory();
            string logDirectory = Path.Combine(UpdaterPaths.GetClientDataDirectory(root), "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "updater.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not decide whether an otherwise safe recovery succeeds.
        }
    }
}

internal static class FileIntegrity
{
    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static bool Matches(string path, long size, string sha256)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                && info.Length == size
                && ComputeSha256(path).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static T Read<T>(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<T>(bytes, Options)
            ?? throw new InvalidDataException($"JSON state is empty or invalid: {path}");
    }

    public static void Write<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Atomic JSON path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".new." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // A uniquely named same-directory artifact is safe to retain as evidence.
            }
        }
    }
}
