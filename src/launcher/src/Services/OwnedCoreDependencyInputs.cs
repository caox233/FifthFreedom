namespace SplinterCellCNLauncher.Services;

// A narrow reviewed byte-origin declaration. It does not establish licensing,
// Authenticode validity, loader closure, a live driver or Public admission.
internal sealed class OwnedCoreDependencyInputs
{
    internal const string SourceUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
    internal const long ArchiveBytes = 750540;
    internal const string ArchiveSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";
    internal const string ArchiveMember = "wintun/bin/amd64/wintun.dll";
    internal const long MemberBytes = 427552;
    internal const string MemberSha256 = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce";

    internal string FilePath { get; }
    private OwnedCoreDependencyInputs(string path) => FilePath = path;

    internal static OwnedCoreDependencyInputs ForOfficialWintun0141Amd64(string runtimeDirectory)
    {
        var directory = OwnedCoreLaunchInputs.Canonical(runtimeDirectory);
        return new(OwnedCoreLaunchInputs.Canonical(Path.Combine(directory, "wintun.dll")));
    }

    internal void RequireAdjacentTo(string executable)
    {
        string expected = Path.Combine(Path.GetDirectoryName(executable)!, "wintun.dll");
        if (!string.Equals(FilePath, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw OwnedCoreLaunchInputs.Invalid();
    }

    // Ownership transfers to the same pin set as the image/configuration. There
    // is no independently disposable DLL lease that could expire before the job.
    internal void Pin(OwnedCoreLaunchInputs.PinnedInputs owner) => owner.Add(FilePath, MemberBytes, MemberSha256);
    public override string ToString() => "OwnedCoreDependencyInputs (declared origin; not admission)";
}
