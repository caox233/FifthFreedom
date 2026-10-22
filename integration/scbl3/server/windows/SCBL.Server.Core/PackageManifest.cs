using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Scbl.Server;

public sealed class PackageManifest
{
    public const int MaximumComponents = 64;
    public const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumExpandedComponentBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, ComponentContract> Contracts = new Dictionary<string, ComponentContract>(StringComparer.OrdinalIgnoreCase)
    {
        ["server.manager"] = new("bootstrap", "SCBL.Server.exe", false),
        ["server.host"] = new("bootstrap", "SCBL.Server.Host.exe", false),
        ["server.updater"] = new("bootstrap", "SCBL.Server.Update.exe", false),
        ["server.dedicated"] = new("runtime", "dedicated/SCBL.Dedicated.exe", false),
        ["server.control-plane"] = new("runtime", "control", true),
        ["server.update-server"] = new("runtime", "update", true),
        ["server.assets"] = new("runtime", "assets", true),
        ["runtime.python"] = new("runtime", "python", true),
    };

    public int SchemaVersion { get; set; }
    public string PackageType { get; set; } = "";
    public string ReleaseVersion { get; set; } = "";
    public List<PackageComponent> Components { get; set; } = [];

    public void Validate(bool fullPackage)
    {
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的 Windows 服务端包清单版本。");
        if (PackageType is not ("windows-full" or "windows-update")) throw new InvalidDataException("服务端包类型无效。");
        if (fullPackage && PackageType != "windows-full") throw new InvalidDataException("首次安装必须使用 windows-full 包。");
        if (!Regex.IsMatch(ReleaseVersion, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("服务端版本必须是 X.Y.Z。");
        if (Components.Count is 0 or > MaximumComponents) throw new InvalidDataException("服务端包组件数量无效。");
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> packagePaths = new(StringComparer.OrdinalIgnoreCase);
        List<(string Target, string Path)> destinations = [];
        long declaredBytes = 0;
        foreach (PackageComponent component in Components)
        {
            if (!ids.Add(component.Id)) throw new InvalidDataException($"重复组件：{component.Id}");
            component.Validate();
            if (!Contracts.TryGetValue(component.Id, out ComponentContract? contract) ||
                !string.Equals(component.Target, contract.Target, StringComparison.Ordinal) ||
                !string.Equals(component.EffectiveDestination().Replace('\\', '/'), contract.Destination, StringComparison.Ordinal) ||
                component.Extract != contract.Extract)
            {
                throw new InvalidDataException($"{component.Id} 安装契约无效。");
            }
            if (!packagePaths.Add(component.Path.Replace('\\', '/'))) throw new InvalidDataException($"多个组件复用了同一包内路径：{component.Path}");
            declaredBytes = checked(declaredBytes + component.Size);
            if (declaredBytes > MaximumPackageBytes) throw new InvalidDataException("服务端包声明总大小过大。");
            string destination = component.EffectiveDestination();
            string normalizedDestination = destination.Replace('\\', '/').Trim('/');
            if (destinations.Any(existing => string.Equals(existing.Target, component.Target, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(existing.Path, normalizedDestination, StringComparison.OrdinalIgnoreCase) ||
                 existing.Path.StartsWith(normalizedDestination + "/", StringComparison.OrdinalIgnoreCase) ||
                 normalizedDestination.StartsWith(existing.Path + "/", StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidDataException($"多个组件目标发生覆盖：{destination}");
            }
            destinations.Add((component.Target, normalizedDestination));
        }
        if (fullPackage)
        {
            string[] required = ["server.host", "server.manager", "server.updater", "server.dedicated", "server.control-plane", "server.update-server", "server.assets", "runtime.python"];
            string[] missing = required.Where(id => !ids.Contains(id)).ToArray();
            if (missing.Length != 0) throw new InvalidDataException("完整包缺少组件：" + string.Join(", ", missing));
        }
    }

    public static PackageManifest ReadFromZip(ZipArchive archive)
    {
        ZipArchiveEntry[] matches = archive.Entries.Where(value => string.Equals(value.FullName, "scbl-windows-package.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("包内必须恰好包含一个 scbl-windows-package.json。");
        ZipArchiveEntry entry = matches[0];
        if (entry.Length is <= 0 or > MaximumManifestBytes) throw new InvalidDataException("Windows 服务端包清单大小无效。");
        using Stream stream = entry.Open();
        PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        })
            ?? throw new InvalidDataException("Windows 服务端包清单无效。");
        manifest.Validate(fullPackage: manifest.PackageType == "windows-full");
        return manifest;
    }

    private sealed record ComponentContract(string Target, string Destination, bool Extract);
}

public sealed class PackageComponent
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Target { get; set; } = "runtime";
    public bool Extract { get; set; }
    public string Destination { get; set; } = "";

    public void Validate()
    {
        if (!Regex.IsMatch(Id, @"^[a-z0-9][a-z0-9.-]+$")) throw new InvalidDataException("组件 ID 无效。");
        if (!Regex.IsMatch(Version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException($"{Id} 版本无效。");
        if (Size is <= 0 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException($"{Id} 大小无效。");
        if (!Regex.IsMatch(Sha256, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException($"{Id} SHA256 无效。");
        if (Target is not ("runtime" or "bootstrap")) throw new InvalidDataException($"{Id} 目标无效。");
        if (Path.Length > 240 || Destination.Length > 240) throw new InvalidDataException($"{Id} 路径过长。");
        _ = PathSafety.ResolveRelative(System.IO.Path.GetTempPath(), Path);
        if (!string.IsNullOrEmpty(Destination)) _ = PathSafety.ResolveRelative(System.IO.Path.GetTempPath(), Destination);
    }

    public string EffectiveDestination() => Target == "runtime"
        ? (string.IsNullOrWhiteSpace(Destination) ? Path.Replace("artifacts/runtime/", "", StringComparison.OrdinalIgnoreCase) : Destination)
        : (string.IsNullOrWhiteSpace(Destination) ? System.IO.Path.GetFileName(Path) : Destination);
}
