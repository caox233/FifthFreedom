using System;
using System.IO;
using System.Linq;

namespace SCBL.Client.Shared;

public static class ClientDataLayout
{
    public const string RootDirectoryName = "data";

    public static string ComputerName { get; } = SanitizeSegment(
        Environment.MachineName,
        "UNKNOWN-PC");

    public static string UserName { get; } = SanitizeSegment(
        Environment.UserName,
        "UNKNOWN-USER");

    public static string Resolve(string clientRoot)
        => ResolveForIdentity(clientRoot, ComputerName, UserName);

    public static string ResolveForIdentity(
        string clientRoot,
        string? computerName,
        string? userName)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientRoot));
        return Path.Combine(
            root,
            RootDirectoryName,
            SanitizeSegment(computerName, "UNKNOWN-PC"),
            SanitizeSegment(userName, "UNKNOWN-USER"));
    }

    private static string SanitizeSegment(string? value, string fallback)
    {
        string clean = string.Concat((value ?? string.Empty).Trim().Select(character =>
            char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character));
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }
}
