using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scbl.Server;

public sealed record ClientPublicationStatus(string Channel, IReadOnlyDictionary<string, string> Components, string? FullPackageVersion);

public sealed record ClientPatchPublication(IReadOnlyDictionary<string, string> Components, string Sha256);

public sealed record ClientFullPackagePublication(string Version, long Size, string Sha256, string PublicPath);

public sealed record ClientArchivePublication(
    string InputKind,
    string Version,
    IReadOnlyDictionary<string, string> Components,
    long Size,
    string Sha256,
    string PublicPath);

public sealed class ClientPublicationManager
{
    private sealed record ComponentSpec(string FileName, string Section, string UpdateMode);
    private sealed record PatchArtifact(string Component, string Version, string ArchivePath, long Size, string Sha256, JsonNode? ReleaseNotes);
    private sealed record VerifiedClientPackage(string Version, long Size, string Sha256, JsonObject Manifest, Dictionary<string, JsonObject> Receipts);
    private sealed record PreparedPublication(
        string InputKind,
        string Version,
        string FullPackagePath,
        IReadOnlyList<PatchArtifact> Artifacts,
        Dictionary<string, JsonObject> Receipts);

    private static readonly Regex VersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, ComponentSpec> Catalog = new Dictionary<string, ComponentSpec>(StringComparer.Ordinal)
    {
        ["launcher"] = new("SCBL.exe", "components", "updater-restart"),
        ["hooks"] = new("uplay_r1_loader.dll", "components", "before-game-start"),
        ["route-guard"] = new("route-guard.zip", "components", "next-launch"),
        ["easytier"] = new("easytier-windows-x86_64.zip", "components", "next-launch"),
        ["updater"] = new("SCBL.Updater.exe", "components", "next-launch"),
        ["client-files"] = new("client-files.zip", "files", "immediate"),
    };
    private static readonly HashSet<string> RequiredClientFiles = new(StringComparer.Ordinal)
    {
        "SCBL.exe",
        "SCBL.Updater.exe",
        "components/network/easytier-core.exe",
        "components/network/easytier-cli.exe",
        "components/network/Packet.dll",
        "components/network/wintun.dll",
        "components/route-guard/scbl-process-router.exe",
        "components/route-guard/WinDivert.dll",
        "components/route-guard/WinDivert64.payload.sys",
        "components/hooks/uplay_r1_loader.dll",
        "components/hooks/uplay_r1_loader.dll.sha256",
        "components/hooks/mod/scbl_gameplay.dll",
        "components/hooks/mod/scbl_gameplay.dll.sha256",
    };

    private readonly ServerPaths _paths;

    public ClientPublicationManager(ServerPaths paths) => _paths = paths;

    public ClientPublicationStatus Status(string channel)
    {
        JsonObject manifest = LoadComponentManifest(channel);
        Dictionary<string, string> components = new(StringComparer.Ordinal);
        foreach (string section in new[] { "components", "files" })
        {
            foreach ((string name, JsonNode? value) in RequireObject(manifest, section))
            {
                if (value is JsonObject entry && TryString(entry, "version", out string? version)) components[name] = version!;
            }
        }
        string fullManifest = Path.Combine(_paths.ClientUpdates, "client_update_manifest.json");
        string? fullVersion = null;
        if (File.Exists(fullManifest))
        {
            JsonObject document = ReadObject(fullManifest, "客户端完整包清单");
            _ = TryString(document, "version", out fullVersion);
        }
        return new(channel, components, fullVersion);
    }

    public ClientArchivePublication PublishArchive(string archivePath, string channel, ServerConfiguration config, ServerSecrets secrets)
    {
        ValidateChannel(channel);
        _paths.EnsureLayout();
        using FileStream publicationLock = AcquireLock();
        string staging = Path.Combine(_paths.Staging, "client-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            PreparedPublication prepared = PrepareArchive(archivePath, channel, staging);
            return CommitPreparedPublication(prepared, channel, config, secrets, staging);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private PreparedPublication PrepareArchive(string archivePath, string channel, string staging)
    {
        FileInfo source = new(archivePath);
        if (!source.Exists || !string.Equals(source.Extension, ".zip", StringComparison.OrdinalIgnoreCase) || source.Length is <= 0 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("客户端发布文件必须是 1 GiB 以内的非空 ZIP。");
        using ZipArchive archive = ZipFile.OpenRead(source.FullName);
        HashSet<string> names = ReadSafeEntries(archive, maximumFiles: 256).Keys.ToHashSet(StringComparer.Ordinal);
        if (names.Contains("client_package_manifest.json")) return PrepareFullArchive(source.FullName, staging);
        if (names.Contains("scbl-client-patch.json")) return PreparePatchArchive(source.FullName, channel, staging);
        throw new InvalidDataException("无法识别客户端 ZIP：缺少完整包或补丁清单。");
    }

    private PreparedPublication PrepareFullArchive(string archivePath, string staging)
    {
        string tree = Path.Combine(staging, "full-tree");
        VerifiedClientPackage source = ExtractVerifiedFullPackage(archivePath, tree);
        IReadOnlyList<PatchArtifact> artifacts = BuildArtifactsFromTree(tree, source.Receipts, Path.Combine(staging, "components"));
        Dictionary<string, JsonObject> receipts = RefreshReceiptArtifactFacts(source.Receipts, artifacts);
        string normalized = Path.Combine(staging, $"SCBL-Client-v{source.Version}-win-x86.zip");
        BuildNormalizedFullPackage(tree, source.Manifest, receipts, normalized);
        return new("full", source.Version, normalized, artifacts, receipts);
    }

    private PreparedPublication PreparePatchArchive(string archivePath, string channel, string staging)
    {
        string componentStaging = Path.Combine(staging, "components");
        Directory.CreateDirectory(componentStaging);
        IReadOnlyList<PatchArtifact> artifacts = ExtractAndVerifyPatch(archivePath, componentStaging);
        string tree = Path.Combine(staging, "full-tree");
        string currentFull = Path.Combine(_paths.ClientUpdates, "SCBL.zip");
        JsonObject baseManifest;
        Dictionary<string, JsonObject> receipts;
        if (File.Exists(currentFull))
        {
            VerifiedClientPackage current = ExtractVerifiedFullPackage(currentFull, tree);
            baseManifest = current.Manifest;
            receipts = current.Receipts;
        }
        else
        {
            if (!artifacts.Select(item => item.Component).ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.Keys))
                throw new InvalidDataException("首次发布补丁必须包含全部六个客户端组件；也可以直接上传完整客户端 ZIP。");
            Directory.CreateDirectory(tree);
            baseManifest = new JsonObject { ["schemaVersion"] = 1 };
            receipts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        }

        List<PatchArtifact> effectiveArtifacts = [];
        foreach (PatchArtifact artifact in artifacts)
        {
            JsonObject candidate = ApplyArtifactToTree(tree, componentStaging, artifact);
            if (receipts.TryGetValue(artifact.Component, out JsonObject? current) &&
                TryString(current, "version", out string? currentVersion) && currentVersion == artifact.Version &&
                ReceiptsInstallEquivalent(current, candidate))
            {
                continue;
            }
            receipts[artifact.Component] = candidate;
            effectiveArtifacts.Add(artifact);
        }
        if (!receipts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.Keys))
            throw new InvalidDataException("补丁应用后没有形成完整的六组件客户端快照。");
        if (!TryString(receipts["launcher"], "version", out string? version) || !VersionPattern.IsMatch(version!))
            throw new InvalidDataException("补丁缺少有效的 Launcher 客户端版本。");
        EnsureRequiredClientTree(tree);
        string normalized = Path.Combine(staging, $"SCBL-Client-v{version}-win-x86.zip");
        if (effectiveArtifacts.Count == 0 && File.Exists(currentFull)) File.Copy(currentFull, normalized);
        else BuildNormalizedFullPackage(tree, baseManifest, receipts, normalized);
        return new("patch", version!, normalized, effectiveArtifacts, receipts);
    }

    private static bool ReceiptsInstallEquivalent(JsonObject left, JsonObject right)
    {
        if (left["files"] is not JsonObject leftFiles || right["files"] is not JsonObject rightFiles) return false;
        if (!JsonNode.DeepEquals(leftFiles, rightFiles)) return false;
        JsonArray leftDelete = left["delete"] as JsonArray ?? new JsonArray();
        JsonArray rightDelete = right["delete"] as JsonArray ?? new JsonArray();
        return JsonNode.DeepEquals(leftDelete, rightDelete);
    }

    private VerifiedClientPackage ExtractVerifiedFullPackage(string archivePath, string destination)
    {
        FileInfo source = new(archivePath);
        if (!source.Exists || !string.Equals(source.Extension, ".zip", StringComparison.OrdinalIgnoreCase) || source.Length is <= 0 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("客户端完整包必须是 1 GiB 以内的非空 ZIP。");
        using ZipArchive archive = ZipFile.OpenRead(source.FullName);
        Dictionary<string, ZipArchiveEntry> entries = ReadSafeEntries(archive, maximumFiles: 256);
        if (!entries.TryGetValue("client_package_manifest.json", out ZipArchiveEntry? manifestEntry)) throw new InvalidDataException("客户端完整包缺少 client_package_manifest.json。");
        JsonObject manifest = ReadObject(manifestEntry, "客户端完整包清单");
        if (!TryInt(manifest, "schemaVersion", out int schema) || schema != 1 || !TryString(manifest, "clientVersion", out string? version) || !VersionPattern.IsMatch(version!) || manifest["files"] is not JsonArray files || manifest["componentVersions"] is not JsonObject versions || manifest["componentReceipts"] is not JsonObject receiptDocument)
            throw new InvalidDataException("客户端完整包清单无效。");
        Dictionary<string, (long Size, string Sha256)> declared = new(StringComparer.Ordinal);
        foreach (JsonNode? value in files)
        {
            if (value is not JsonObject item || !TryString(item, "path", out string? path) || !IsSafeArchivePath(path!) || !TryLong(item, "size", out long size) || size <= 0 || !TryString(item, "sha256", out string? digest) || !DigestPattern.IsMatch(digest!) || !declared.TryAdd(path!, (size, digest!)))
                throw new InvalidDataException("客户端完整包包含无效文件记录。");
        }
        HashSet<string> expectedNames = declared.Keys.Append("client_package_manifest.json").ToHashSet(StringComparer.Ordinal);
        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames)) throw new InvalidDataException("客户端完整包文件与清单不一致。");
        Directory.CreateDirectory(destination);
        foreach ((string path, (long size, string digest)) in declared)
        {
            ZipArchiveEntry entry = entries[path];
            if (entry.Length != size || !Hashing.FixedEqualsHex(Sha256(entry), digest)) throw new InvalidDataException($"客户端完整包文件校验失败：{path}");
            string target = SafeTreePath(destination, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.Open();
            using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        if (!RequiredClientFiles.IsSubsetOf(declared.Keys)) throw new InvalidDataException("客户端完整包缺少必要运行文件。");
        if (!versions.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.Keys) || !receiptDocument.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.Keys))
            throw new InvalidDataException("客户端完整包的组件版本或回执集合不完整。");
        Dictionary<string, JsonObject> receipts = new(StringComparer.Ordinal);
        HashSet<string> claimedFiles = new(StringComparer.Ordinal);
        foreach (string component in Catalog.Keys)
        {
            if (!TryString(versions, component, out string? componentVersion) || !VersionPattern.IsMatch(componentVersion!) || receiptDocument[component] is not JsonObject receipt || !TryString(receipt, "version", out string? receiptVersion) || receiptVersion != componentVersion || !TryString(receipt, "sha256", out string? artifactDigest) || !DigestPattern.IsMatch(artifactDigest!) || receipt["files"] is not JsonObject receiptFiles)
                throw new InvalidDataException($"客户端完整包组件回执无效：{component}");
            foreach ((string path, JsonNode? digestNode) in receiptFiles)
            {
                string? digest = digestNode?.GetValue<string>();
                if (!claimedFiles.Add(path) || digest is null || !declared.TryGetValue(path, out (long Size, string Sha256) fact) || fact.Sha256 != digest)
                    throw new InvalidDataException($"客户端完整包组件文件回执无效：{component}");
            }
            receipts[component] = (JsonObject)receipt.DeepClone();
        }
        if (!TryString(versions, "launcher", out string? launcherVersion) || launcherVersion != version) throw new InvalidDataException("客户端完整包版本与 Launcher 版本不一致。");
        return new(version!, source.Length, Hashing.Sha256(source.FullName), manifest, receipts);
    }

    private IReadOnlyList<PatchArtifact> BuildArtifactsFromTree(string tree, Dictionary<string, JsonObject> receipts, string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        List<PatchArtifact> artifacts = [];
        foreach ((string component, ComponentSpec spec) in Catalog)
        {
            if (!receipts.TryGetValue(component, out JsonObject? receipt) || !TryString(receipt, "version", out string? version) || !VersionPattern.IsMatch(version!) || receipt["files"] is not JsonObject files)
                throw new InvalidDataException($"客户端完整包缺少可生成的组件回执：{component}");
            string directory = Path.Combine(outputRoot, component);
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, spec.FileName);
            switch (component)
            {
                case "launcher":
                    CopyTreeFile(tree, "SCBL.exe", artifactPath);
                    break;
                case "hooks":
                    CopyTreeFile(tree, "components/hooks/uplay_r1_loader.dll", artifactPath);
                    break;
                case "updater":
                    CopyTreeFile(tree, "SCBL.Updater.exe", artifactPath);
                    break;
                case "route-guard":
                    WriteDeterministicZip(artifactPath, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["scbl-process-router.exe"] = SafeTreePath(tree, "components/route-guard/scbl-process-router.exe"),
                        ["WinDivert.dll"] = SafeTreePath(tree, "components/route-guard/WinDivert.dll"),
                        ["WinDivert64.sys"] = SafeTreePath(tree, "components/route-guard/WinDivert64.payload.sys"),
                    });
                    break;
                case "easytier":
                    WriteDeterministicZip(artifactPath, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["easytier-core.exe"] = SafeTreePath(tree, "components/network/easytier-core.exe"),
                        ["easytier-cli.exe"] = SafeTreePath(tree, "components/network/easytier-cli.exe"),
                        ["Packet.dll"] = SafeTreePath(tree, "components/network/Packet.dll"),
                        ["wintun.dll"] = SafeTreePath(tree, "components/network/wintun.dll"),
                    });
                    break;
                case "client-files":
                    BuildClientFilesArtifact(tree, files, receipt["delete"] as JsonArray ?? new JsonArray(), artifactPath);
                    break;
                default:
                    throw new InvalidDataException($"未知客户端组件：{component}");
            }
            string digest = Hashing.Sha256(artifactPath);
            artifacts.Add(new(component, version!, artifactPath, new FileInfo(artifactPath).Length, digest, null));
        }
        return artifacts;
    }

    private static Dictionary<string, JsonObject> RefreshReceiptArtifactFacts(Dictionary<string, JsonObject> source, IReadOnlyList<PatchArtifact> artifacts)
    {
        Dictionary<string, JsonObject> result = source.ToDictionary(item => item.Key, item => (JsonObject)item.Value.DeepClone(), StringComparer.Ordinal);
        foreach (PatchArtifact artifact in artifacts)
        {
            JsonObject receipt = result[artifact.Component];
            receipt["version"] = artifact.Version;
            receipt["sha256"] = artifact.Sha256;
        }
        return result;
    }

    private JsonObject ApplyArtifactToTree(string tree, string componentRoot, PatchArtifact artifact)
    {
        ComponentSpec spec = Catalog[artifact.Component];
        string source = Path.Combine(componentRoot, artifact.Component, spec.FileName);
        JsonObject files = new();
        JsonArray deletes = new();
        switch (artifact.Component)
        {
            case "launcher":
                InstallTreeFile(source, tree, "SCBL.exe", files);
                break;
            case "hooks":
                InstallTreeFile(source, tree, "components/hooks/uplay_r1_loader.dll", files);
                string sidecar = SafeTreePath(tree, "components/hooks/uplay_r1_loader.dll.sha256");
                Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
                File.WriteAllText(sidecar, artifact.Sha256 + "  uplay_r1_loader.dll\r\n", System.Text.Encoding.ASCII);
                break;
            case "updater":
                InstallTreeFile(source, tree, "SCBL.Updater.exe", files);
                break;
            case "route-guard":
                ApplyStructuredZip(source, tree, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["scbl-process-router.exe"] = "components/route-guard/scbl-process-router.exe",
                    ["WinDivert.dll"] = "components/route-guard/WinDivert.dll",
                    ["WinDivert64.sys"] = "components/route-guard/WinDivert64.payload.sys",
                }, files);
                break;
            case "easytier":
                ApplyStructuredZip(source, tree, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["easytier-core.exe"] = "components/network/easytier-core.exe",
                    ["easytier-cli.exe"] = "components/network/easytier-cli.exe",
                    ["Packet.dll"] = "components/network/Packet.dll",
                    ["wintun.dll"] = "components/network/wintun.dll",
                }, files);
                break;
            case "client-files":
                ApplyClientFilesArtifact(source, tree, files, deletes);
                break;
            default:
                throw new InvalidDataException($"未知客户端组件：{artifact.Component}");
        }
        JsonObject receipt = new()
        {
            ["version"] = artifact.Version,
            ["sha256"] = artifact.Sha256,
            ["files"] = files,
        };
        if (artifact.Component == "client-files") receipt["delete"] = deletes;
        return receipt;
    }

    private static void ApplyStructuredZip(string archivePath, string tree, IReadOnlyDictionary<string, string> mapping, JsonObject files)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Dictionary<string, ZipArchiveEntry> entries = ReadSafeEntries(archive, maximumFiles: 16);
        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(mapping.Keys)) throw new InvalidDataException("客户端组件 ZIP 文件集合不完整。");
        foreach ((string entryName, string targetPath) in mapping)
        {
            string target = SafeTreePath(tree, targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (Stream input = entries[entryName].Open())
            using (FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            files[targetPath] = Hashing.Sha256(target);
        }
    }

    private static void ApplyClientFilesArtifact(string archivePath, string tree, JsonObject files, JsonArray deletes)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Dictionary<string, ZipArchiveEntry> entries = ReadSafeEntries(archive, maximumFiles: 256);
        if (!entries.TryGetValue("client-files-manifest.json", out ZipArchiveEntry? manifestEntry)) throw new InvalidDataException("Client Files 组件缺少清单。");
        JsonObject manifest = ReadObject(manifestEntry, "Client Files 清单");
        if (!TryInt(manifest, "schemaVersion", out int schema) || schema != 1 || manifest["files"] is not JsonArray records || manifest["delete"] is not JsonArray deleteRecords)
            throw new InvalidDataException("Client Files 清单无效。");
        HashSet<string> declared = new(StringComparer.Ordinal) { "client-files-manifest.json" };
        foreach (JsonNode? value in records)
        {
            if (value is not JsonObject item || !TryString(item, "path", out string? path) || !IsSafeArchivePath(path!) || !TryLong(item, "size", out long size) || size <= 0 || !TryString(item, "sha256", out string? digest) || !DigestPattern.IsMatch(digest!))
                throw new InvalidDataException("Client Files 文件记录无效。");
            string entryName = "files/" + path;
            if (!declared.Add(entryName) || !entries.TryGetValue(entryName, out ZipArchiveEntry? entry) || entry.Length != size || !Hashing.FixedEqualsHex(Sha256(entry), digest!))
                throw new InvalidDataException($"Client Files 文件校验失败：{path}");
            string target = SafeTreePath(tree, path!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (Stream input = entry.Open())
            using (FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            files[path!] = digest;
        }
        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(declared)) throw new InvalidDataException("Client Files ZIP 包含清单之外的文件。");
        foreach (JsonNode? value in deleteRecords)
        {
            string path = value?.GetValue<string>() ?? throw new InvalidDataException("Client Files 删除记录无效。");
            if (!IsSafeArchivePath(path) || files.ContainsKey(path)) throw new InvalidDataException($"Client Files 删除路径无效：{path}");
            string target = SafeTreePath(tree, path);
            if (File.Exists(target)) File.Delete(target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            deletes.Add(path);
        }
    }

    private static void BuildClientFilesArtifact(string tree, JsonObject receiptFiles, JsonArray deletes, string output)
    {
        Dictionary<string, string> sources = new(StringComparer.Ordinal);
        JsonArray records = [];
        foreach ((string path, JsonNode? digestNode) in receiptFiles.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!IsSafeArchivePath(path)) throw new InvalidDataException($"Client Files 回执路径无效：{path}");
            string source = SafeTreePath(tree, path);
            if (!File.Exists(source)) throw new InvalidDataException($"Client Files 回执文件不存在：{path}");
            string digest = Hashing.Sha256(source);
            string? expected = digestNode?.GetValue<string>();
            if (expected is not null && !Hashing.FixedEqualsHex(digest, expected)) throw new InvalidDataException($"Client Files 回执文件漂移：{path}");
            sources["files/" + path] = source;
            records.Add(new JsonObject { ["path"] = path, ["size"] = new FileInfo(source).Length, ["sha256"] = digest });
        }
        JsonObject manifest = new() { ["schemaVersion"] = 1, ["files"] = records, ["delete"] = deletes.DeepClone() };
        string temporaryManifest = output + ".manifest.json";
        File.WriteAllText(temporaryManifest, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        try
        {
            sources["client-files-manifest.json"] = temporaryManifest;
            WriteDeterministicZip(output, sources);
        }
        finally
        {
            File.Delete(temporaryManifest);
        }
    }

    private static void WriteDeterministicZip(string output, IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using FileStream stream = new(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach ((string name, string source) in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!IsSafeArchivePath(name) || !File.Exists(source)) throw new InvalidDataException($"组件归档输入无效：{name}");
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream input = File.OpenRead(source);
            using Stream destination = entry.Open();
            input.CopyTo(destination);
        }
    }

    private static void BuildNormalizedFullPackage(string tree, JsonObject sourceManifest, Dictionary<string, JsonObject> receipts, string output)
    {
        EnsureRequiredClientTree(tree);
        JsonObject manifest = (JsonObject)sourceManifest.DeepClone();
        manifest["schemaVersion"] = 1;
        if (!TryString(receipts["launcher"], "version", out string? version)) throw new InvalidDataException("Launcher 回执缺少版本。");
        manifest["clientVersion"] = version;
        manifest["generatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        manifest["bootstrapHooksSha256"] = Hashing.Sha256(SafeTreePath(tree, "components/hooks/uplay_r1_loader.dll"));
        JsonObject versions = new();
        JsonObject receiptDocument = new();
        foreach (string component in Catalog.Keys)
        {
            JsonObject receipt = receipts[component];
            if (!TryString(receipt, "version", out string? componentVersion) || !TryString(receipt, "sha256", out string? digest) || !DigestPattern.IsMatch(digest!) || receipt["files"] is not JsonObject receiptFiles)
                throw new InvalidDataException($"组件回执不能生成完整包：{component}");
            foreach ((string path, JsonNode? expectedNode) in receiptFiles.ToArray())
            {
                string target = SafeTreePath(tree, path);
                if (!File.Exists(target)) throw new InvalidDataException($"组件回执目标缺失：{component}/{path}");
                string actual = Hashing.Sha256(target);
                string? expected = expectedNode?.GetValue<string>();
                if (expected is not null && !Hashing.FixedEqualsHex(actual, expected)) throw new InvalidDataException($"组件回执目标漂移：{component}/{path}");
                receiptFiles[path] = actual;
            }
            versions[component] = componentVersion;
            receiptDocument[component] = receipt.DeepClone();
        }
        manifest["componentVersions"] = versions;
        manifest["componentReceipts"] = receiptDocument;
        List<(string Relative, string FullPath, long Size, string Sha256)> files = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(tree, path).Replace('\\', '/'), FullPath: path))
            .Where(item => item.Relative != "client_package_manifest.json")
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => (item.Relative, item.FullPath, new FileInfo(item.FullPath).Length, Hashing.Sha256(item.FullPath)))
            .ToList();
        manifest["files"] = new JsonArray(files.Select(item => (JsonNode)new JsonObject { ["path"] = item.Relative, ["size"] = item.Size, ["sha256"] = item.Sha256 }).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach ((string relative, string fullPath, _, _) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream input = File.OpenRead(fullPath);
            using Stream destination = entry.Open();
            input.CopyTo(destination);
        }
        ZipArchiveEntry manifestEntry = archive.CreateEntry("client_package_manifest.json", CompressionLevel.Optimal);
        manifestEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using StreamWriter writer = new(manifestEntry.Open(), new System.Text.UTF8Encoding(false));
        writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void EnsureRequiredClientTree(string tree)
    {
        foreach (string relative in RequiredClientFiles)
            if (!File.Exists(SafeTreePath(tree, relative))) throw new InvalidDataException($"客户端快照缺少必要文件：{relative}");
    }

    private static string SafeTreePath(string root, string relative)
    {
        if (!IsSafeArchivePath(relative)) throw new InvalidDataException($"客户端相对路径无效：{relative}");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"客户端路径越界：{relative}");
        return result;
    }

    private static void CopyTreeFile(string tree, string relative, string destination)
    {
        string source = SafeTreePath(tree, relative);
        if (!File.Exists(source)) throw new InvalidDataException($"客户端完整包缺少组件文件：{relative}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void InstallTreeFile(string source, string tree, string relative, JsonObject receiptFiles)
    {
        if (!File.Exists(source)) throw new InvalidDataException($"客户端组件文件不存在：{source}");
        string target = SafeTreePath(tree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        receiptFiles[relative] = Hashing.Sha256(target);
    }

    private ClientArchivePublication CommitPreparedPublication(PreparedPublication prepared, string channel, ServerConfiguration config, ServerSecrets secrets, string staging)
    {
        JsonObject componentManifest = LoadComponentManifest(channel);
        foreach (PatchArtifact artifact in prepared.Artifacts) ValidateProgression(componentManifest, artifact);
        string componentManifestPath = ComponentManifestPath(channel);
        string fullManifestPath = Path.Combine(_paths.ClientUpdates, "client_update_manifest.json");
        string descriptorPath = Path.Combine(_paths.ClientUpdates, "scbl-server.json");
        string publicAlias = Path.Combine(_paths.ClientUpdates, "SCBL.zip");
        string rollbackAlias = Path.Combine(staging, "SCBL.zip.rollback");
        FileStateSnapshot componentManifestBefore = FileStateSnapshot.Capture(componentManifestPath);
        FileStateSnapshot fullManifestBefore = FileStateSnapshot.Capture(fullManifestPath);
        FileStateSnapshot descriptorBefore = FileStateSnapshot.Capture(descriptorPath);
        bool aliasExisted = File.Exists(publicAlias);
        List<string> createdDirectories = [];
        string releaseDirectory = Path.Combine(_paths.ClientUpdates, "releases", prepared.Version);
        string immutable = Path.Combine(releaseDirectory, $"SCBL-Client-v{prepared.Version}-win-x86.zip");
        bool releaseCreated = false;
        try
        {
            foreach (PatchArtifact artifact in prepared.Artifacts)
            {
                ComponentSpec spec = Catalog[artifact.Component];
                string targetDirectory = Path.Combine(_paths.ClientUpdates, "components", "artifacts", artifact.Component, artifact.Version);
                string target = Path.Combine(targetDirectory, spec.FileName);
                if (Directory.Exists(targetDirectory))
                {
                    if (!File.Exists(target) || new FileInfo(target).Length != artifact.Size || !Hashing.FixedEqualsHex(Hashing.Sha256(target), artifact.Sha256))
                        throw new InvalidDataException($"不可变客户端组件已存在且内容不同：{artifact.Component}/{artifact.Version}");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
                    string temporary = targetDirectory + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    Directory.CreateDirectory(temporary);
                    try
                    {
                        AtomicFile.Copy(artifact.ArchivePath, Path.Combine(temporary, spec.FileName));
                        AtomicFile.WriteAllText(Path.Combine(temporary, "component.json"), new JsonObject
                        {
                            ["schemaVersion"] = 2,
                            ["component"] = artifact.Component,
                            ["publishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["artifact"] = CreateEntry(artifact),
                        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                        Directory.Move(temporary, targetDirectory);
                        createdDirectories.Add(targetDirectory);
                    }
                    finally
                    {
                        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                    }
                }
                RequireObject(componentManifest, spec.Section)[artifact.Component] = CreateEntry(artifact);
            }
            componentManifest["schemaVersion"] = 2;
            componentManifest["channel"] = channel;
            componentManifest["generatedAt"] = DateTimeOffset.UtcNow.ToString("O");
            Dictionary<string, JsonObject> current = CurrentComponents(componentManifest);
            if (!current.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.Keys)) throw new InvalidDataException("发布后客户端组件清单不完整。");
            foreach (string component in Catalog.Keys)
            {
                JsonObject entry = current[component];
                JsonObject receipt = prepared.Receipts[component];
                if (!TryString(entry, "version", out string? entryVersion) || !TryString(entry, "sha256", out string? entryDigest) || !TryString(receipt, "version", out string? receiptVersion) || !TryString(receipt, "sha256", out string? receiptDigest) || entryVersion != receiptVersion || entryDigest != receiptDigest)
                    throw new InvalidDataException($"完整包与组件发布事实不一致：{component}");
            }

            long packageSize = new FileInfo(prepared.FullPackagePath).Length;
            string packageDigest = Hashing.Sha256(prepared.FullPackagePath);
            if (Directory.Exists(releaseDirectory))
            {
                if (!File.Exists(immutable) || new FileInfo(immutable).Length != packageSize || !Hashing.FixedEqualsHex(Hashing.Sha256(immutable), packageDigest))
                    throw new InvalidDataException($"不可变客户端完整包 {prepared.Version} 已存在且内容不同。");
            }
            else
            {
                Directory.CreateDirectory(releaseDirectory);
                AtomicFile.Copy(prepared.FullPackagePath, immutable);
                releaseCreated = true;
            }
            if (aliasExisted) AtomicFile.Copy(publicAlias, rollbackAlias);
            AtomicFile.Copy(immutable, publicAlias);
            JsonObject publication = new()
            {
                ["version"] = prepared.Version,
                ["updateMode"] = "full-package",
                ["fullPackage"] = "SCBL.zip",
                ["immutableFullPackage"] = $"releases/{prepared.Version}/SCBL-Client-v{prepared.Version}-win-x86.zip",
                ["fullPackageSha256"] = packageDigest,
                ["fullPackageSize"] = packageSize,
                ["releaseNotes"] = new JsonArray("客户端功能与稳定性更新"),
                ["releaseNotesEn"] = new JsonArray("Client features and stability update"),
            };
            AtomicFile.WriteAllText(componentManifestPath, componentManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            AtomicFile.WriteAllText(fullManifestPath, publication.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            _ = new ServerDescriptorPublisher(_paths).Publish(config, secrets);
            PruneFullPackages(prepared.Version);
            File.Delete(rollbackAlias);
            return new(prepared.InputKind, prepared.Version, current.ToDictionary(item => item.Key, item => item.Value["version"]!.GetValue<string>(), StringComparer.Ordinal), packageSize, packageDigest, "/SCBL.zip");
        }
        catch
        {
            componentManifestBefore.Restore();
            fullManifestBefore.Restore();
            descriptorBefore.Restore();
            if (File.Exists(rollbackAlias)) AtomicFile.Copy(rollbackAlias, publicAlias);
            else if (!aliasExisted) File.Delete(publicAlias);
            if (releaseCreated && Directory.Exists(releaseDirectory)) Directory.Delete(releaseDirectory, recursive: true);
            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
        finally
        {
            File.Delete(rollbackAlias);
        }
    }

    public ClientPatchPublication PublishComponentPatch(string archivePath, string channel, ServerConfiguration config, ServerSecrets secrets)
    {
        ValidateChannel(channel);
        _paths.EnsureLayout();
        using FileStream publicationLock = AcquireLock();
        string staging = Path.Combine(_paths.Staging, "client-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        List<string> createdDirectories = [];
        string manifestPath = ComponentManifestPath(channel);
        string descriptorPath = Path.Combine(_paths.ClientUpdates, "scbl-server.json");
        FileStateSnapshot manifestBefore = FileStateSnapshot.Capture(manifestPath);
        FileStateSnapshot descriptorBefore = FileStateSnapshot.Capture(descriptorPath);
        try
        {
            IReadOnlyList<PatchArtifact> artifacts = ExtractAndVerifyPatch(archivePath, staging);
            JsonObject manifest = LoadComponentManifest(channel);
            foreach (PatchArtifact artifact in artifacts) ValidateProgression(manifest, artifact);

            foreach (PatchArtifact artifact in artifacts)
            {
                ComponentSpec spec = Catalog[artifact.Component];
                string targetDirectory = Path.Combine(_paths.ClientUpdates, "components", "artifacts", artifact.Component, artifact.Version);
                string target = Path.Combine(targetDirectory, spec.FileName);
                if (Directory.Exists(targetDirectory))
                {
                    if (!File.Exists(target) || new FileInfo(target).Length != artifact.Size || !Hashing.FixedEqualsHex(Hashing.Sha256(target), artifact.Sha256))
                        throw new InvalidDataException($"不可变客户端组件已存在且内容不同：{artifact.Component}/{artifact.Version}");
                }
                else
                {
                    string parent = Path.GetDirectoryName(targetDirectory)!;
                    Directory.CreateDirectory(parent);
                    string temporary = targetDirectory + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    Directory.CreateDirectory(temporary);
                    try
                    {
                        AtomicFile.Copy(Path.Combine(staging, artifact.Component, spec.FileName), Path.Combine(temporary, spec.FileName));
                        JsonObject entry = CreateEntry(artifact);
                        AtomicFile.WriteAllText(Path.Combine(temporary, "component.json"), new JsonObject
                        {
                            ["schemaVersion"] = 2,
                            ["component"] = artifact.Component,
                            ["publishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["artifact"] = entry.DeepClone(),
                        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                        Directory.Move(temporary, targetDirectory);
                        createdDirectories.Add(targetDirectory);
                    }
                    finally
                    {
                        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                    }
                }
                RequireObject(manifest, spec.Section)[artifact.Component] = CreateEntry(artifact);
            }

            manifest["schemaVersion"] = 2;
            manifest["channel"] = channel;
            manifest["generatedAt"] = DateTimeOffset.UtcNow.ToString("O");
            AtomicFile.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            _ = new ServerDescriptorPublisher(_paths).Publish(config, secrets);
            return new ClientPatchPublication(artifacts.ToDictionary(item => item.Component, item => item.Version, StringComparer.Ordinal), Hashing.Sha256(archivePath));
        }
        catch
        {
            manifestBefore.Restore();
            descriptorBefore.Restore();
            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public ClientFullPackagePublication PublishFullPackage(string archivePath, string channel, ServerConfiguration config, ServerSecrets secrets)
    {
        ClientArchivePublication result = PublishArchive(archivePath, channel, config, secrets);
        if (result.InputKind != "full") throw new InvalidDataException("此入口只接受完整客户端 ZIP。");
        return new(result.Version, result.Size, result.Sha256, result.PublicPath);
    }

    private IReadOnlyList<PatchArtifact> ExtractAndVerifyPatch(string archivePath, string staging)
    {
        FileInfo source = new(archivePath);
        if (!source.Exists ||
            !string.Equals(source.Extension, ".zip", StringComparison.OrdinalIgnoreCase) && !string.Equals(source.Extension, ".scblpatch", StringComparison.OrdinalIgnoreCase) ||
            source.Length is <= 0 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("客户端组件补丁必须是 1 GiB 以内的非空 ZIP 文件。");
        using ZipArchive archive = ZipFile.OpenRead(source.FullName);
        Dictionary<string, ZipArchiveEntry> entries = ReadSafeEntries(archive, maximumFiles: 32);
        if (!entries.TryGetValue("scbl-client-patch.json", out ZipArchiveEntry? manifestEntry)) throw new InvalidDataException("客户端组件补丁缺少 scbl-client-patch.json。");
        JsonObject manifest = ReadObject(manifestEntry, "客户端组件补丁清单");
        if (!TryInt(manifest, "schemaVersion", out int schema) || schema != 1 || !TryString(manifest, "packageType", out string? type) || type != "scbl-client-components-patch" || manifest["components"] is not JsonArray records || records.Count == 0)
            throw new InvalidDataException("客户端组件补丁清单无效。");
        List<PatchArtifact> artifacts = [];
        HashSet<string> declared = new(StringComparer.Ordinal) { "scbl-client-patch.json" };
        HashSet<string> components = new(StringComparer.Ordinal);
        foreach (JsonNode? value in records)
        {
            if (value is not JsonObject item || !TryString(item, "component", out string? component) || !Catalog.TryGetValue(component!, out ComponentSpec? spec) || !components.Add(component!))
                throw new InvalidDataException("客户端组件补丁包含未知或重复组件。");
            if (!TryString(item, "version", out string? version) || !VersionPattern.IsMatch(version!) || !TryString(item, "path", out string? path) || path != $"artifacts/{component}/{spec.FileName}" || !TryString(item, "file", out string? file) || file != spec.FileName || !TryString(item, "updateMode", out string? updateMode) || updateMode != spec.UpdateMode || !TryLong(item, "size", out long size) || size is <= 0 or > 512L * 1024 * 1024 || !TryString(item, "sha256", out string? digest) || !DigestPattern.IsMatch(digest!))
                throw new InvalidDataException($"客户端组件补丁记录无效：{component}");
            if (!entries.TryGetValue(path!, out ZipArchiveEntry? entry) || entry.Length != size || !declared.Add(path!)) throw new InvalidDataException($"客户端组件文件缺失或大小不符：{component}");
            string directory = Path.Combine(staging, component!);
            Directory.CreateDirectory(directory);
            string extracted = Path.Combine(directory, spec.FileName);
            using (Stream input = entry.Open())
            using (FileStream output = new(extracted, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (!Hashing.FixedEqualsHex(Hashing.Sha256(extracted), digest!)) throw new InvalidDataException($"客户端组件 SHA256 不符：{component}");
            artifacts.Add(new(component!, version!, extracted, size, digest!, item["releaseNotes"]?.DeepClone()));
        }
        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(declared)) throw new InvalidDataException("客户端组件补丁包含清单之外的文件。");
        return artifacts;
    }

    private JsonObject LoadComponentManifest(string channel)
    {
        string path = ComponentManifestPath(channel);
        if (!File.Exists(path)) return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["channel"] = channel,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["components"] = new JsonObject(),
            ["files"] = new JsonObject(),
        };
        JsonObject manifest = ReadObject(path, channel + " 客户端组件清单");
        if (!TryInt(manifest, "schemaVersion", out int schema) || schema != 2 || !TryString(manifest, "channel", out string? foundChannel) || foundChannel != channel)
            throw new InvalidDataException(channel + " 客户端组件清单无效。");
        _ = RequireObject(manifest, "components");
        _ = RequireObject(manifest, "files");
        return manifest;
    }

    private static Dictionary<string, JsonObject> CurrentComponents(JsonObject manifest)
    {
        Dictionary<string, JsonObject> result = new(StringComparer.Ordinal);
        foreach (string section in new[] { "components", "files" })
            foreach ((string name, JsonNode? value) in RequireObject(manifest, section))
                if (value is JsonObject entry && !result.TryAdd(name, entry)) throw new InvalidDataException("客户端组件清单包含重复组件。");
        return result;
    }

    private static JsonObject CreateEntry(PatchArtifact artifact)
    {
        ComponentSpec spec = Catalog[artifact.Component];
        JsonObject entry = new()
        {
            ["version"] = artifact.Version,
            ["sha256"] = artifact.Sha256,
            ["size"] = artifact.Size,
            ["url"] = $"/components/artifacts/{artifact.Component}/{artifact.Version}/{spec.FileName}",
            ["updateMode"] = spec.UpdateMode,
            ["required"] = true,
        };
        if (artifact.ReleaseNotes is not null) entry["releaseNotes"] = artifact.ReleaseNotes.DeepClone();
        return entry;
    }

    private static void ValidateProgression(JsonObject manifest, PatchArtifact artifact)
    {
        ComponentSpec spec = Catalog[artifact.Component];
        JsonObject section = RequireObject(manifest, spec.Section);
        if (section[artifact.Component] is not JsonObject current) return;
        if (!TryString(current, "version", out string? oldVersion) || !TryString(current, "sha256", out string? oldDigest) || !TryLong(current, "size", out long oldSize))
            throw new InvalidDataException($"当前客户端组件记录无效：{artifact.Component}");
        int comparison = CompareVersions(artifact.Version, oldVersion!);
        if (comparison < 0 || comparison == 0 && (oldDigest != artifact.Sha256 || oldSize != artifact.Size))
            throw new InvalidDataException($"客户端组件版本未前进或同版本字节不同：{artifact.Component} 当前 {oldVersion}，上传 {artifact.Version}");
    }

    private FileStream AcquireLock()
    {
        string path = Path.Combine(_paths.ClientRoot, "client-publication.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("已有客户端发布事务正在执行，请稍后重试。", error); }
    }

    private void PruneFullPackages(string currentVersion)
    {
        string root = Path.Combine(_paths.ClientUpdates, "releases");
        if (!Directory.Exists(root)) return;
        DirectoryInfo[] releases = new DirectoryInfo(root).EnumerateDirectories().Where(directory => VersionPattern.IsMatch(directory.Name) && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderByDescending(directory => directory.Name, Comparer<string>.Create(CompareVersions)).ToArray();
        HashSet<string> keep = releases.Take(3).Select(directory => directory.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        keep.Add(Path.Combine(root, currentVersion));
        foreach (DirectoryInfo release in releases)
            if (!keep.Contains(release.FullName)) release.Delete(recursive: true);
    }

    private static Dictionary<string, ZipArchiveEntry> ReadSafeEntries(ZipArchive archive, int maximumFiles)
    {
        ZipArchiveEntry[] files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length == 0 || files.Length > maximumFiles) throw new InvalidDataException("ZIP 文件数量无效。");
        Dictionary<string, ZipArchiveEntry> result = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in files)
        {
            string name = entry.FullName;
            if (!IsSafeArchivePath(name) || !result.TryAdd(name, entry)) throw new InvalidDataException($"ZIP 包含不安全或重复路径：{name}");
        }
        return result;
    }

    private static bool IsSafeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains(':') || value.Contains('\0') || value.StartsWith('/') || value.Contains("//", StringComparison.Ordinal)) return false;
        return value.Split('/').All(part => part is not ("" or "." or ".."));
    }

    private static string Sha256(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static JsonObject ReadObject(string path, string label)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadObject(stream, label);
    }

    private static JsonObject ReadObject(ZipArchiveEntry entry, string label)
    {
        if (entry.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException(label + "大小无效。");
        using Stream stream = entry.Open();
        return ReadObject(stream, label);
    }

    private static JsonObject ReadObject(Stream stream, string label)
    {
        try { return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException(label + "必须是 JSON 对象。"); }
        catch (JsonException error) { throw new InvalidDataException(label + "不是有效 JSON。", error); }
    }

    private static JsonObject RequireObject(JsonObject parent, string name) => parent[name] as JsonObject ?? throw new InvalidDataException($"JSON 缺少对象字段：{name}");
    private string ComponentManifestPath(string channel) => Path.Combine(_paths.ClientUpdates, "components", "channels", channel, "client_components_v2.json");
    private static void ValidateChannel(string channel) { if (channel is not ("stable" or "test")) throw new InvalidDataException("客户端通道必须是 stable 或 test。"); }
    private static int CompareVersions(string left, string right)
    {
        int[] a = left.Split('.').Select(int.Parse).ToArray();
        int[] b = right.Split('.').Select(int.Parse).ToArray();
        for (int index = 0; index < 3; index++) { int result = a[index].CompareTo(b[index]); if (result != 0) return result; }
        return 0;
    }
    private static bool TryString(JsonObject parent, string name, out string? value) { value = null; try { value = parent[name]?.GetValue<string>(); return value is not null; } catch (InvalidOperationException) { return false; } }
    private static bool TryInt(JsonObject parent, string name, out int value) { value = 0; try { JsonValue? node = parent[name] as JsonValue; return node is not null && node.TryGetValue(out value); } catch (InvalidOperationException) { return false; } }
    private static bool TryLong(JsonObject parent, string name, out long value) { value = 0; try { JsonValue? node = parent[name] as JsonValue; return node is not null && node.TryGetValue(out value); } catch (InvalidOperationException) { return false; } }
}
