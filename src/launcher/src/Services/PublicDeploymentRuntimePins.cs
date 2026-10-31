using System.Security.Cryptography;

namespace SplinterCellCNLauncher.Services;

public enum PublicRuntimeRole { Core, Cli, Packet, Wintun }

/// <summary>A reviewed input declaration, not independent license or publisher verification.</summary>
public sealed class PublicRuntimePin
{
    public PublicRuntimeRole Role { get; }
    public long Bytes { get; }
    public string Sha256 { get; }
    public PublicRuntimePin(PublicRuntimeRole role, long bytes, string sha256)
    {
        if (!Enum.IsDefined(role) || bytes is <= 0 or > 1024L * 1024 * 1024 || sha256 is not { Length: 64 }
            || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("PUBLIC_RUNTIME_PIN_INVALID");
        Role = role; Bytes = bytes; Sha256 = sha256;
    }
    public override string ToString() => "PublicRuntimePin (declared input)";
}

/// <summary>
/// Immutable, explicitly supplied expected bytes. Verification checks only local
/// files; it never downloads, installs a driver, starts a process or opens Public.
/// Licensing, trusted provenance and real runtime readiness remain separate gates.
/// </summary>
public sealed class PublicDeploymentRuntimePins
{
    public string Version { get; }
    private readonly PublicRuntimePin[] _pins;
    public IReadOnlyList<PublicRuntimePin> Pins => Array.AsReadOnly(_pins);
    public PublicDeploymentRuntimePins(string version, IEnumerable<PublicRuntimePin> pins)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 64 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            || pins is null) throw new ArgumentException("PUBLIC_RUNTIME_PIN_INVALID");
        _pins = pins.Take(5).ToArray();
        if (_pins.Length != 4 || _pins.Any(p => p is null) || _pins.Select(p => p.Role).Distinct().Count() != 4)
            throw new ArgumentException("PUBLIC_RUNTIME_PIN_INVALID");
        Version = version;
    }

    public void VerifyFiles(string runtimeDirectory)
    {
        try
        {
            if (string.IsNullOrEmpty(runtimeDirectory) || !Path.IsPathFullyQualified(runtimeDirectory)) throw Invalid();
            string root = Path.GetFullPath(runtimeDirectory);
            RejectLinks(root);
            foreach (var pin in _pins)
            {
                string name = pin.Role switch { PublicRuntimeRole.Core => "easytier-core.exe", PublicRuntimeRole.Cli => "easytier-cli.exe",
                    PublicRuntimeRole.Packet => "Packet.dll", PublicRuntimeRole.Wintun => "wintun.dll", _ => throw Invalid() };
                string path = Path.Combine(root, name);
                RejectLinks(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != pin.Bytes || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != pin.Sha256
                    || input.Length != pin.Bytes) throw Invalid();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { throw Invalid(); }
    }
    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
    }
    private static IOException Invalid() => new("PUBLIC_RUNTIME_INPUTS_INVALID");
    public override string ToString() => "PublicDeploymentRuntimePins (declared files; not runtime readiness)";
}
