namespace Scbl.Server;

public sealed class ServerPaths
{
    public ServerPaths(string? programRoot = null, string? dataRoot = null)
    {
        ProgramRoot = Path.GetFullPath(programRoot ??
            Environment.GetEnvironmentVariable("SCBL_WINDOWS_PROGRAM_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SCBL Server"));
        DataRoot = Path.GetFullPath(dataRoot ??
            Environment.GetEnvironmentVariable("SCBL_WINDOWS_DATA_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SCBL Server"));
    }

    public string ProgramRoot { get; }
    public string DataRoot { get; }
    public string Bootstrap => Path.Combine(ProgramRoot, "bootstrap");
    public string Config => Path.Combine(DataRoot, "config", "server.json");
    public string Secrets => Path.Combine(DataRoot, "config", "secrets.json");
    public string CurrentReleasePointer => Path.Combine(DataRoot, "state", "current-release.json");
    public string PreviousReleasePointer => Path.Combine(DataRoot, "state", "previous-release.json");
    public string Releases => Path.Combine(DataRoot, "releases");
    public string Backups => Path.Combine(DataRoot, "backups");
    public string Cache => Path.Combine(DataRoot, "cache");
    public string Logs => Path.Combine(DataRoot, "logs");
    public string Database => Path.Combine(DataRoot, "data", "dedicated", "5th-echelon.db");
    public string RuntimeData => Path.Combine(DataRoot, "data", "runtime");
    public string GameplayModeState => Path.Combine(RuntimeData, "gameplay-modes.json");
    public string ClientRoot => Path.Combine(DataRoot, "client");
    public string ClientUpdates => Path.Combine(ClientRoot, "updates");
    public string ClientBundleCache => Path.Combine(Cache, "client-bundles");
    public string External => Path.Combine(DataRoot, "external");
    public string EasyTier => Path.Combine(External, "easytier");
    public string DdnsGo => Path.Combine(External, "ddns-go");
    public string Staging => Path.Combine(DataRoot, "staging");
    public string Diagnostics => Path.Combine(DataRoot, "diagnostics");

    public void EnsureLayout()
    {
        foreach (string directory in new[]
        {
            ProgramRoot, Bootstrap, Path.GetDirectoryName(Config)!, Path.GetDirectoryName(CurrentReleasePointer)!,
            Releases, Backups, Cache, Logs, Path.GetDirectoryName(Database)!, RuntimeData, ClientUpdates,
            ClientBundleCache, External, EasyTier, DdnsGo, Staging, Diagnostics
        })
        {
            Directory.CreateDirectory(directory);
        }
        WindowsSecurity.SecureRoots(this);
    }

    public string RequireCurrentRelease()
    {
        ReleasePointer pointer = AtomicJson.Read<ReleasePointer>(CurrentReleasePointer)
            ?? throw new InvalidOperationException("尚未安装 SCBL Windows Runtime。");
        return RequireRelease(pointer);
    }

    public string RequireRelease(ReleasePointer pointer)
    {
        string fullPath = Path.GetFullPath(Path.Combine(Releases, pointer.Directory));
        if (!PathSafety.IsInside(fullPath, Releases) || !Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("Runtime 指针无效。");
        }
        PackageManifest manifest = AtomicJson.Read<PackageManifest>(Path.Combine(fullPath, "release.json"))
            ?? throw new InvalidDataException("Runtime 缺少 release.json。");
        manifest.Validate(fullPackage: false);
        if (!string.Equals(manifest.ReleaseVersion, pointer.Version, StringComparison.Ordinal)) throw new InvalidDataException("Runtime 指针版本与 release.json 不一致。");
        return fullPath;
    }
}

public sealed record ReleasePointer(string Version, string Directory, DateTimeOffset ActivatedAtUtc);
