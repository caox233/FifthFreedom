using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Scbl.Server;

public sealed class ServerDescriptorPublisher
{
    private static readonly Regex VersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ComponentFields = new(StringComparer.Ordinal)
    {
        "version", "sha256", "size", "url", "updateMode", "required", "releaseNotes",
    };

    private readonly ServerPaths _paths;

    public ServerDescriptorPublisher(ServerPaths paths) => _paths = paths;

    public string Publish(ServerConfiguration config, ServerSecrets secrets)
    {
        config.Validate(throwOnError: true);
        secrets.Validate();
        string channelManifest = Path.Combine(_paths.ClientUpdates, "components", "channels", config.Mode, "client_components_v2.json");
        if (!File.Exists(channelManifest)) throw new FileNotFoundException($"{config.Mode} 客户端组件清单不存在。", channelManifest);
        byte[] manifestBytes = File.ReadAllBytes(channelManifest);
        using JsonDocument manifestDocument = ParseObject(manifestBytes, $"{config.Mode} 客户端组件清单");
        JsonElement manifest = manifestDocument.RootElement;
        if (!TryInt(manifest, "schemaVersion", out int schemaVersion) || schemaVersion != 2 ||
            !TryString(manifest, "channel", out string? channel) || channel != config.Mode)
        {
            throw new InvalidDataException($"{config.Mode} 客户端组件清单结构无效。");
        }
        Dictionary<string, JsonElement> components = ReadComponentSection(manifest, "components");
        Dictionary<string, JsonElement> files = ReadComponentSection(manifest, "files", optional: true);
        if (components.Count + files.Count == 0) throw new InvalidDataException("客户端组件清单不得为空。");
        if (components.Keys.Intersect(files.Keys, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException("components 与 files 存在同名客户端组件。");

        JsonObject? fullPackage = ReadFullPackage();
        string latest = components.TryGetValue("launcher", out JsonElement launcher) && TryString(launcher, "version", out string? launcherVersion)
            ? launcherVersion!
            : fullPackage?["version"]?.GetValue<string>() ?? "0.0.0";
        if (!VersionPattern.IsMatch(latest)) throw new InvalidDataException("无法从客户端制品确定有效 Launcher 版本。");

        var payload = new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            server = new { id = config.ServerId, mode = config.Mode },
            client = new
            {
                version = latest,
                updateBaseUrl = config.BuildPublicUpdateBaseUrl(),
                componentGeneration = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                components,
                files,
                fullPackage,
            },
            network = new
            {
                publicHost = config.PublicHost,
                useOriginHost = string.IsNullOrWhiteSpace(config.PublicHost),
                publicPort = config.GamePort,
                transports = new[] { "quic", "wss" },
                networkName = config.EasyTierNetworkName,
                secret = secrets.NetworkSecret,
                mtu = config.Mtu,
                enableIpv6 = config.EnableIpv6,
            },
            controlPlane = new { address = config.VirtualIp, port = config.ControlPort },
            dedicated = new { address = config.VirtualIp, configPort = 80, authPort = 50051, gamePort = 21126, protocol = "prudp" },
            announcements = new
            {
                active = ReadAnnouncement("active", latest),
                startup = ReadAnnouncement("startup", latest),
                update = ReadAnnouncement("update", latest),
            },
        };
        string destination = Path.Combine(_paths.ClientUpdates, "scbl-server.json");
        AtomicFile.WriteAllText(destination, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return destination;
    }

    private Dictionary<string, JsonElement> ReadComponentSection(JsonElement manifest, string sectionName, bool optional = false)
    {
        if (!manifest.TryGetProperty(sectionName, out JsonElement section))
        {
            if (optional) return new(StringComparer.OrdinalIgnoreCase);
            throw new InvalidDataException($"客户端组件清单缺少 {sectionName}。");
        }
        if (section.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"客户端组件清单 {sectionName} 结构无效。");
        Dictionary<string, JsonElement> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty item in section.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Value.ValueKind != JsonValueKind.Object || !result.TryAdd(item.Name, item.Value.Clone()))
                throw new InvalidDataException($"客户端组件记录无效：{item.Name}");
            ValidateComponent(item.Name, item.Value);
        }
        return result;
    }

    private void ValidateComponent(string name, JsonElement entry)
    {
        if (entry.EnumerateObject().Any(field => !ComponentFields.Contains(field.Name)) ||
            !TryString(entry, "version", out string? version) || !VersionPattern.IsMatch(version!) ||
            !TryString(entry, "sha256", out string? digest) || !DigestPattern.IsMatch(digest!) ||
            !TryLong(entry, "size", out long size) || size <= 0 ||
            !TryString(entry, "url", out string? url) || !IsArtifactUrl(url!) ||
            !entry.TryGetProperty("required", out JsonElement required) || required.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException($"客户端组件记录无效：{name}");
        }
        string candidate = Path.GetFullPath(Path.Combine(_paths.ClientUpdates, url!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        string artifacts = Path.GetFullPath(Path.Combine(_paths.ClientUpdates, "components", "artifacts"));
        if (!PathSafety.IsInside(candidate, artifacts) || IsReparsePoint(candidate) || !File.Exists(candidate) || new FileInfo(candidate).Length != size ||
            !Hashing.FixedEqualsHex(Hashing.Sha256(candidate), digest!))
        {
            throw new InvalidDataException($"客户端组件文件缺失或漂移：{name}");
        }
    }

    private JsonObject? ReadFullPackage()
    {
        string manifestPath = Path.Combine(_paths.ClientUpdates, "client_update_manifest.json");
        if (!File.Exists(manifestPath)) return null;
        using JsonDocument document = ParseObject(File.ReadAllBytes(manifestPath), "客户端完整包清单");
        JsonElement root = document.RootElement;
        if (!TryString(root, "version", out string? version) || !VersionPattern.IsMatch(version!) ||
            !TryString(root, "fullPackage", out string? relative) || !IsSafeRelativePath(relative!) ||
            !TryString(root, "fullPackageSha256", out string? digest) || !DigestPattern.IsMatch(digest!))
        {
            throw new InvalidDataException("客户端完整包清单无效。");
        }
        string path = Path.GetFullPath(Path.Combine(_paths.ClientUpdates, relative!.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathSafety.IsInside(path, _paths.ClientUpdates) || IsReparsePoint(path) || !File.Exists(path)) throw new InvalidDataException("客户端完整包文件不存在或路径无效。");
        long size = new FileInfo(path).Length;
        if (root.TryGetProperty("fullPackageSize", out JsonElement declared) && (declared.ValueKind != JsonValueKind.Number || !declared.TryGetInt64(out long declaredSize) || declaredSize != size))
            throw new InvalidDataException("客户端完整包大小与清单不一致。");
        if (!Hashing.FixedEqualsHex(Hashing.Sha256(path), digest!)) throw new InvalidDataException("客户端完整包 SHA256 与实际文件不一致。");
        return new JsonObject { ["version"] = version, ["url"] = "/" + relative.Replace('\\', '/'), ["sha256"] = digest, ["size"] = size };
    }

    private JsonObject? ReadAnnouncement(string kind, string version)
    {
        string path = Path.Combine(_paths.ClientUpdates, kind + "_announcement.json");
        if (!File.Exists(path)) return kind == "update" ? DefaultUpdate(version) : null;
        using JsonDocument document = ParseObject(File.ReadAllBytes(path), kind + " 公告");
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind != JsonValueKind.True) return kind == "update" ? DefaultUpdate(version) : null;
        if (!TryString(root, "title", out string? title) || string.IsNullOrWhiteSpace(title) || !TryString(root, "body", out string? body) || string.IsNullOrWhiteSpace(body))
            throw new InvalidDataException($"{kind} 公告缺少标题或正文。");
        string? announcementVersion = TryString(root, "version", out string? foundVersion) ? foundVersion : null;
        if (kind == "update" && announcementVersion != version) return DefaultUpdate(version);
        JsonObject result = [];
        foreach (string field in new[] { "id", "title", "body", "title_en", "body_en", "level", "showOnce", "version" })
            if (root.TryGetProperty(field, out JsonElement value)) result[field] = JsonNode.Parse(value.GetRawText());
        return result;
    }

    private static JsonObject? DefaultUpdate(string version) => version == "0.0.0" ? null : new JsonObject
    {
        ["id"] = "update-" + version, ["version"] = version, ["title"] = "客户端更新 " + version,
        ["body"] = "发现新的客户端组件。确认后将下载、验证并安装本次更新。",
        ["title_en"] = "Client Update " + version,
        ["body_en"] = "New client components are available. Confirm to download, verify, and install the update.",
        ["level"] = "info", ["showOnce"] = true,
    };

    private static JsonDocument ParseObject(byte[] content, string label)
    {
        if (content.Length is 0 or > 1024 * 1024) throw new InvalidDataException(label + "大小无效。");
        try
        {
            JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException(label + "必须是 JSON 对象。"); }
            return document;
        }
        catch (JsonException error) { throw new InvalidDataException(label + "不是有效 JSON。", error); }
    }

    private static bool IsArtifactUrl(string value) => value.StartsWith("/components/artifacts/", StringComparison.Ordinal) && !value.Contains("..", StringComparison.Ordinal) && !value.Contains('\\') && !value.Contains('%') && !value.Contains('?') && !value.Contains('#');
    private static bool IsSafeRelativePath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.StartsWith('/') && !value.StartsWith('\\') && !value.Contains("..", StringComparison.Ordinal) && !value.Contains('\\') && !value.Contains('%') && !value.Contains('?') && !value.Contains('#');
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool TryString(JsonElement parent, string name, out string? value) { value = null; return parent.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String && (value = field.GetString()) is not null; }
    private static bool TryInt(JsonElement parent, string name, out int value) { value = 0; return parent.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value); }
    private static bool TryLong(JsonElement parent, string name, out long value) { value = 0; return parent.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value); }
}
