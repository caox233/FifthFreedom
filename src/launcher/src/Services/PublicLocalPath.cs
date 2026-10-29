namespace SplinterCellCNLauncher.Services;

/// <summary>
/// Pre-I/O path admission for explicit Public deployment inputs. Windows inputs
/// must name ordinary local drive paths. Syntax/drive admission precedes the
/// root-to-leaf attribute walk; content hashes remain a separate check.
/// </summary>
internal static class PublicLocalPath
{
    internal static bool IsAllowed(string? path) =>
        IsAllowed(path, OperatingSystem.IsWindows(), root => new DriveInfo(root).DriveType);

    internal static void RequirePlain(string path) => RequirePlain(path, OperatingSystem.IsWindows(),
        root => new DriveInfo(root).DriveType, File.GetAttributes);

    internal static void RequirePlain(string path, bool windows, Func<string, DriveType> driveType,
        Func<string, FileAttributes> attributes)
    {
        try
        {
            if (!IsAllowed(path, windows, driveType)) throw Invalid();
            int rootLength = windows ? 3 : 1;
            char separator = windows ? '\\' : '/';
            string current = path[..rootLength];
            Inspect(current);
            if (path.Length == rootLength) return;
            foreach (string part in path[rootLength..].Split(separator))
            {
                current = current.EndsWith(separator) ? current + part : current + separator + part;
                Inspect(current);
            }

            void Inspect(string item)
            {
                // Never inspect a descendant before its own ancestor passed.
                // This is a sequential metadata check, not handle-pinned race
                // isolation against concurrent local filesystem mutations.
                var value = attributes(item);
                if ((value & FileAttributes.ReparsePoint) != 0
                    || (item.Length < path.Length && (value & FileAttributes.Directory) == 0)) throw Invalid();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException)
        { throw Invalid(); }
    }

    // The platform grammar and drive metadata are explicit so tests can reject
    // UNC/device/mapped-drive inputs without contacting any network location.
    // Query only a syntactically admitted drive root, never the supplied target.
    internal static bool IsAllowed(string? path, bool windows, Func<string, DriveType> driveType)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return false;
        if (!windows)
        {
            if (path[0] != '/' || path.Contains('\\')) return false;
            return path.Length == 1 || CanonicalSegments(path[1..].Split('/'), false);
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.AsSpan(2).Contains(':') || path.IndexOfAny(['/', '<', '>', '"', '|', '?', '*']) >= 0
            || (path.Length > 3 && !CanonicalSegments(path[3..].Split('\\'), true))) return false;
        try
        {
            // Unknown/absent drives fail closed as well as mapped network drives.
            return driveType(path[..3]) is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException)
        { return false; }
    }

    private static bool CanonicalSegments(string[] segments, bool windows)
    {
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
            if (!windows) continue;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))) return false;
        }
        return true;
    }

    private static IOException Invalid() => new("PUBLIC_DEPLOYMENT_CONFIGURATION_INVALID");
}
