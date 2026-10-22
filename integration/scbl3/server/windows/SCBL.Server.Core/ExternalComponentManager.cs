using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scbl.Server;

public sealed class ExternalComponentManager
{
    private static readonly string[] EasyTierFiles = ["easytier-core.exe", "easytier-cli.exe", "Packet.dll", "wintun.dll"];
    private static readonly HttpClient Http = CreateClient();
    private readonly ServerPaths _paths;
    private readonly ServerLog _log;

    public ExternalComponentManager(ServerPaths paths, ServerLog log) { _paths = paths; _log = log; }

    public string? FindEasyTierCore() => EasyTierFiles.All(name => FindManagedBinary(_paths.EasyTier, name) is not null)
        ? FindManagedBinary(_paths.EasyTier, "easytier-core.exe")
        : null;
    public string? FindEasyTierCli() => FindManagedBinary(_paths.EasyTier, "easytier-cli.exe");
    public string? FindDdnsGo() => FindManagedBinary(_paths.DdnsGo, "ddns-go.exe");

    public async Task<string> EnsureEasyTierAsync(CancellationToken cancellationToken = default, bool refreshLatest = false, IProgress<string>? progress = null)
    {
        string? existing = FindEasyTierCore();
        if (!refreshLatest && existing is not null) return existing;
        return await InstallLatestZipAsync(
            "https://api.github.com/repos/EasyTier/EasyTier/releases/latest",
            _paths.EasyTier,
            name => name.StartsWith("easytier-windows-x86_64-v", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            EasyTierFiles,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    public async Task<string> EnsureDdnsGoAsync(CancellationToken cancellationToken = default, bool refreshLatest = false, IProgress<string>? progress = null)
    {
        string? existing = FindDdnsGo();
        if (!refreshLatest && existing is not null) return existing;
        return await InstallLatestZipAsync(
            "https://api.github.com/repos/jeessy2/ddns-go/releases/latest",
            _paths.DdnsGo,
            name => Regex.IsMatch(name, "^ddns-go_[0-9.]+_windows_x86_64\\.zip$", RegexOptions.IgnoreCase),
            ["ddns-go.exe"],
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    private async Task<string> InstallLatestZipAsync(string api, string root, Func<string, bool> selector, IReadOnlyList<string> required, CancellationToken cancellationToken, IProgress<string>? progress)
    {
        ExternalPointer? previousPointer = AtomicJson.Read<ExternalPointer>(Path.Combine(root, "current.json"));
        progress?.Report("正在读取外部组件版本信息……");
        using HttpRequestMessage apiRequest = new(HttpMethod.Get, api);
        apiRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage apiResponse = await Http.SendAsync(apiRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        apiResponse.EnsureSuccessStatusCode();
        await using Stream response = await apiResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(response, cancellationToken: cancellationToken).ConfigureAwait(false);
        string tag = document.RootElement.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("官方发布缺少版本号。");
        JsonElement asset = document.RootElement.GetProperty("assets").EnumerateArray().SingleOrDefault(item => selector(item.GetProperty("name").GetString() ?? ""));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("官方发布缺少 Windows x86_64 制品。");
        string name = asset.GetProperty("name").GetString()!;
        string url = asset.GetProperty("browser_download_url").GetString()!;
        if (!IsSafeSegment(name)) throw new InvalidDataException("外部组件制品名称无效。");
        string version = tag.TrimStart('v');
        if (!IsSafeSegment(version)) throw new InvalidDataException("外部组件版本号无效。");
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out JsonElement digestElement) ? digestElement.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(digest[7..], "^[0-9a-fA-F]{64}$"))
        {
            throw new InvalidDataException("官方发布未提供可验证的 SHA256 digest。");
        }
        string expectedSha256 = digest[7..].ToLowerInvariant();
        if (size is <= 0 or > 256L * 1024 * 1024) throw new InvalidDataException("外部组件大小无效。");
        string archivePath = Path.Combine(_paths.Cache, name);
        Directory.CreateDirectory(_paths.Cache);
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != size || !Hashing.FixedEqualsHex(Hashing.Sha256(archivePath), expectedSha256))
        {
            await DownloadArchiveAsync(url, archivePath, name, size, expectedSha256, progress, cancellationToken).ConfigureAwait(false);
        }
        string releaseName = $"{version}-{expectedSha256[..12]}";
        string release = Path.Combine(root, "releases", releaseName);
        string staging = Path.Combine(root, "releases", ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                foreach (string binary in required)
                {
                    ZipArchiveEntry[] matches = archive.Entries.Where(item => string.Equals(Path.GetFileName(item.FullName), binary, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException($"外部组件包缺少唯一 {binary}。");
                    matches[0].ExtractToFile(Path.Combine(staging, binary), overwrite: false);
                }
            }
            Dictionary<string, string> files = required.ToDictionary(binary => binary, binary => Hashing.Sha256(Path.Combine(staging, binary)), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(release))
            {
                bool matchesExisting = files.All(item => File.Exists(Path.Combine(release, item.Key)) && Hashing.FixedEqualsHex(Hashing.Sha256(Path.Combine(release, item.Key)), item.Value));
                if (matchesExisting) Directory.Delete(staging, true);
                else
                {
                    releaseName += "-repair-" + Guid.NewGuid().ToString("N")[..8];
                    release = Path.Combine(root, "releases", releaseName);
                    Directory.Move(staging, release);
                }
            }
            else Directory.Move(staging, release);
            AtomicJson.Write(Path.Combine(root, "current.json"), new ExternalPointer(version, releaseName, DateTimeOffset.UtcNow, expectedSha256, files));
            Retention.PruneDirectories(Path.Combine(root, "releases"), 3, [releaseName, previousPointer?.Directory]);
            PruneCachedArchives(selector, keep: 3);
            _log.Write("external", $"installed {tag} from {name}");
            return Path.Combine(release, required[0]);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static async Task DownloadArchiveAsync(string url, string archivePath, string name, long expectedSize, string expectedSha256, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string partial = archivePath + ".download";
        AdoptLargestPartial(archivePath, partial, expectedSize);
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > expectedSize) { File.Delete(partial); offset = 0; }
                if (offset == expectedSize)
                {
                    if (Hashing.FixedEqualsHex(Hashing.Sha256(partial), expectedSha256))
                    {
                        File.Move(partial, archivePath, overwrite: true);
                        WindowsSecurity.RestrictFile(archivePath);
                        progress?.Report($"{name} 已从本地断点完成校验。");
                        return;
                    }
                    File.Delete(partial);
                    offset = 0;
                }
                long total = offset;
                {
                    using HttpRequestMessage request = new(HttpMethod.Get, url);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    progress?.Report($"正在下载 {name}：{FormatProgress(offset, expectedSize)}");
                    using HttpResponseMessage download = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    download.EnsureSuccessStatusCode();
                    bool resume = offset > 0 && download.StatusCode == HttpStatusCode.PartialContent && download.Content.Headers.ContentRange?.From == offset;
                    if (!resume) offset = 0;
                    total = offset;
                    await using Stream input = await download.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using FileStream output = new(partial, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                    byte[] buffer = new byte[128 * 1024];
                    while (total < expectedSize)
                    {
                        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        idle.CancelAfter(TimeSpan.FromSeconds(30));
                        int read;
                        try { read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException($"下载 {name} 连续 30 秒没有收到数据。");
                        }
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        total += read;
                        if (total > expectedSize) throw new InvalidDataException($"外部组件 {name} 下载大小超过声明值。");
                        progress?.Report($"正在下载 {name}：{FormatProgress(total, expectedSize)}");
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (total != expectedSize) throw new IOException($"外部组件 {name} 下载中断：{total}/{expectedSize} bytes。");
                if (!Hashing.FixedEqualsHex(Hashing.Sha256(partial), expectedSha256))
                {
                    File.Delete(partial);
                    throw new InvalidDataException($"外部组件 {name} SHA256 校验失败。");
                }
                File.Move(partial, archivePath, overwrite: true);
                WindowsSecurity.RestrictFile(archivePath);
                progress?.Report($"{name} 下载与校验完成。");
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException && attempt < 3)
            {
                lastError = error;
                progress?.Report($"{name} 下载中断，将从已有进度重试（{attempt}/3）：{error.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
                break;
            }
        }
        throw new IOException($"外部组件 {name} 下载失败，已保留临时文件供下次续传。", lastError);
    }

    private static void AdoptLargestPartial(string archivePath, string partial, long expectedSize)
    {
        string directory = Path.GetDirectoryName(archivePath)!;
        string prefix = Path.GetFileName(archivePath) + ".";
        FileInfo[] candidates = new DirectoryInfo(directory).GetFiles(Path.GetFileName(archivePath) + "*.download")
            .Where(file => file.Length >= 0 && file.Length <= expectedSize && (string.Equals(file.FullName, partial, StringComparison.OrdinalIgnoreCase) || file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(file => file.Length)
            .ToArray();
        FileInfo? best = candidates.FirstOrDefault();
        if (best is not null && !string.Equals(best.FullName, partial, StringComparison.OrdinalIgnoreCase)) File.Move(best.FullName, partial, overwrite: true);
        foreach (FileInfo stale in candidates.Where(file => !string.Equals(file.FullName, partial, StringComparison.OrdinalIgnoreCase)))
        {
            try { stale.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string FormatProgress(long completed, long total) =>
        $"{completed / 1024d / 1024d:F1}/{total / 1024d / 1024d:F1} MB（{Math.Clamp(completed * 100d / total, 0, 100):F0}%）";

    private static string? FindManagedBinary(string root, string name)
    {
        ExternalPointer? pointer = AtomicJson.Read<ExternalPointer>(Path.Combine(root, "current.json"));
        if (pointer?.Files is null || !pointer.Files.TryGetValue(name, out string? expectedSha256)) return null;
        if (!IsSafeSegment(pointer.Directory)) return null;
        string target = Path.GetFullPath(Path.Combine(root, "releases", pointer.Directory, name));
        return PathSafety.IsInside(target, root) && File.Exists(target) && Hashing.FixedEqualsHex(Hashing.Sha256(target), expectedSha256) ? target : null;
    }

    private void PruneCachedArchives(Func<string, bool> selector, int keep)
    {
        if (!Directory.Exists(_paths.Cache)) return;
        foreach (FileInfo file in new DirectoryInfo(_paths.Cache).GetFiles("*.zip").Where(item => selector(item.Name)).OrderByDescending(item => item.LastWriteTimeUtc).Skip(keep))
        {
            try { file.Delete(); }
            catch (IOException error) { _log.Write("external", $"cache cleanup deferred for {file.Name}: {error.Message}"); }
            catch (UnauthorizedAccessException error) { _log.Write("external", $"cache cleanup denied for {file.Name}: {error.Message}"); }
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SCBL-Windows-Server", "3.0"));
        return client;
    }

    private static bool IsSafeSegment(string value) =>
        value.Length is > 0 and <= 96 &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        !value.Contains(Path.AltDirectorySeparatorChar) &&
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$");
}

public sealed record ExternalPointer(
    string Version,
    string Directory,
    DateTimeOffset ActivatedAtUtc,
    string? ArchiveSha256 = null,
    IReadOnlyDictionary<string, string>? Files = null);
