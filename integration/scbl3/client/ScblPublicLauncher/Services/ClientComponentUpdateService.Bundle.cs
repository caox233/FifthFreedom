using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SplinterCellCNLauncher.Services;

public sealed partial class ClientComponentUpdateService
{
    private static IReadOnlyList<KeyValuePair<string, ClientComponentDefinition>> CollectBundleCandidates(
        ClientComponentManifest manifest,
        IReadOnlyDictionary<string, (string Version, string Sha256)> installed,
        Uri manifestUri)
    {
        var candidates = new List<KeyValuePair<string, ClientComponentDefinition>>();
        foreach ((string name, ClientComponentDefinition definition) in manifest.Components
                     .Concat(manifest.Files)
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyDictionary<string, ComponentSpec> supported = manifest.Files.ContainsKey(name)
                ? SupportedFileBundles
                : SupportedComponents;
            ValidateDefinition(name, definition, manifestUri, supported);
            if (installed.TryGetValue(name, out (string Version, string Sha256) current)
                && InstalledComponentSatisfiesCandidate(
                    name,
                    current.Version,
                    current.Sha256,
                    definition.Version,
                    definition.Sha256))
            {
                continue;
            }
            candidates.Add(new KeyValuePair<string, ClientComponentDefinition>(name, definition));
        }
        return candidates;
    }

    internal static Uri BuildBundleUri(Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri || (manifestUri.Scheme != Uri.UriSchemeHttp && manifestUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("组件清单来源必须是 HTTP(S) 绝对地址。");
        Uri endpoint = new(manifestUri, "/components/bundles/v1");
        if (!endpoint.Scheme.Equals(manifestUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !endpoint.Host.Equals(manifestUri.Host, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != manifestUri.Port)
        {
            throw new InvalidDataException("组件 bundle 端点必须与清单同源。");
        }
        return endpoint;
    }

    private async Task<bool> TryPrefetchBundleAsync(
        IReadOnlyList<KeyValuePair<string, ClientComponentDefinition>> candidates,
        string channel,
        Uri manifestUri,
        string manifestSha256,
        CancellationToken cancellationToken,
        ClientComponentDownloadProgress? downloadProgress = null,
        string? componentsRootOverride = null)
    {
        if (candidates.Count is < 1 or > MaximumBundleComponents)
            return false;

        Uri bundleUri = BuildBundleUri(manifestUri);
        var requestDocument = new ClientComponentBundleRequest
        {
            Channel = channel,
            ManifestSha256 = manifestSha256,
            Components = candidates
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ClientComponentBundleRequestItem
                {
                    Name = pair.Key,
                    Sha256 = pair.Value.Sha256.ToLowerInvariant()
                })
                .ToList()
        };
        string componentsRoot = componentsRootOverride
            ?? Path.Combine(LogService.PersistentDataDirectory, "components");
        Directory.CreateDirectory(componentsRoot);
        string archivePath = Path.Combine(componentsRoot, $".bundle-{Guid.NewGuid():N}.download");
        long declaredBytes = candidates.Sum(pair => pair.Value.Size);
        long maximumArchiveBytes = checked(declaredBytes + MaximumBundleOverheadBytes);
        TimeSpan timeout = CalculateArtifactDownloadTimeout(declaredBytes);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, bundleUri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestDocument, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            using HttpResponseMessage response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Warning(
                    $"Component bundle unavailable; falling back once to individual artifacts. status={(int)response.StatusCode}, channel={channel}, count={candidates.Count}.");
                return false;
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("组件 bundle Content-Type 无效。");
            if (response.Content.Headers.ContentLength is long contentLength
                && (contentLength <= 0 || contentLength > maximumArchiveBytes))
            {
                throw new InvalidDataException("组件 bundle 响应大小超出安全边界。");
            }

            await using (Stream source = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[1024 * 128];
                long total = 0;
                long progressTotal = response.Content.Headers.ContentLength ?? maximumArchiveBytes;
                ReportProgress(downloadProgress?.AdvanceBundle(0, progressTotal)
                    ?? new ClientUpdateProgress(
                        ClientUpdateProgressStage.Downloading,
                        IsIndeterminate: progressTotal <= 0,
                        BytesCompleted: 0,
                        BytesTotal: Math.Max(0, progressTotal)));
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), linkedCts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > maximumArchiveBytes)
                        throw new InvalidDataException("组件 bundle 流超出安全边界。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token).ConfigureAwait(false);
                    ReportProgress(downloadProgress?.AdvanceBundle(total, progressTotal)
                        ?? new ClientUpdateProgress(
                            ClientUpdateProgressStage.Downloading,
                            IsIndeterminate: progressTotal <= 0,
                            BytesCompleted: total,
                            BytesTotal: Math.Max(0, progressTotal)));
                }
                await destination.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                ReportProgress(downloadProgress?.AdvanceBundle(total, total)
                    ?? new ClientUpdateProgress(
                        ClientUpdateProgressStage.Downloading,
                        IsIndeterminate: total <= 0,
                        BytesCompleted: total,
                        BytesTotal: total));
            }

            await Task.Run(
                () => InstallBundleArchive(
                    archivePath,
                    candidates,
                    channel,
                    manifestSha256,
                    componentsRoot),
                linkedCts.Token).ConfigureAwait(false);
            ReportProgress(ClientUpdateProgressStage.VerifyingArtifacts);
            LogService.Info(
                $"Component bundle verified and staged: channel={channel}, count={candidates.Count}, components={string.Join(',', candidates.Select(pair => pair.Key))}.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            downloadProgress?.UseIndividualFallback();
            LogService.Warning(
                $"Component bundle rejected; falling back once to individual artifacts. channel={channel}, count={candidates.Count}, reason={ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    internal Task<bool> TryPrefetchBundleForTestAsync(
        ClientComponentManifest manifest,
        string channel,
        Uri manifestUri,
        string manifestSha256,
        string componentsRoot,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KeyValuePair<string, ClientComponentDefinition>> candidates = manifest.Components
            .Concat(manifest.Files)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return TryPrefetchBundleAsync(
            candidates,
            channel,
            manifestUri,
            manifestSha256,
            cancellationToken,
            downloadProgress: null,
            componentsRootOverride: componentsRoot);
    }

    private static void InstallBundleArchive(
        string archivePath,
        IReadOnlyList<KeyValuePair<string, ClientComponentDefinition>> candidates,
        string channel,
        string manifestSha256,
        string componentsRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry[] entries = archive.Entries.ToArray();
        if (entries.Any(entry => string.IsNullOrWhiteSpace(entry.Name)
                                 || entry.FullName.Contains('\\')
                                 || entry.FullName.StartsWith('/')
                                 || entry.FullName.Split('/').Any(part => part is "" or "." or "..")))
        {
            throw new InvalidDataException("组件 bundle 包含不安全路径或目录项。");
        }
        if (entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("组件 bundle 包含重复条目。");
        ZipArchiveEntry bundleManifestEntry = archive.GetEntry("bundle-manifest.json")
            ?? throw new InvalidDataException("组件 bundle 缺少内部清单。");
        byte[] bundleManifestBytes = ReadBoundedEntry(bundleManifestEntry, MaximumBundleManifestBytes);
        ClientComponentBundleManifest bundleManifest = JsonSerializer.Deserialize<ClientComponentBundleManifest>(
            bundleManifestBytes,
            JsonOptions) ?? throw new InvalidDataException("组件 bundle 内部清单为空。");
        if (bundleManifest.SchemaVersion != SupportedBundleSchemaVersion
            || !bundleManifest.Channel.Equals(channel, StringComparison.Ordinal)
            || !bundleManifest.ManifestSha256.Equals(manifestSha256, StringComparison.OrdinalIgnoreCase)
            || bundleManifest.Components.Count != candidates.Count)
        {
            throw new InvalidDataException("组件 bundle 内部清单与请求世代不一致。");
        }
        if (bundleManifest.Components.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("组件 bundle 内部清单包含重复组件。");

        var expectedEntries = new HashSet<string>(StringComparer.Ordinal) { "bundle-manifest.json" };
        var verifiedEntries = new List<(string Name, ClientComponentDefinition Definition, ComponentSpec Spec, ZipArchiveEntry Entry)>();
        foreach ((string name, ClientComponentDefinition definition) in candidates)
        {
            ComponentSpec spec = SupportedComponents.TryGetValue(name, out ComponentSpec? componentSpec)
                ? componentSpec
                : SupportedFileBundles.TryGetValue(name, out ComponentSpec? fileSpec)
                    ? fileSpec
                    : throw new InvalidDataException($"组件 bundle 包含不支持的组件：{name}。");
            string expectedPath = $"components/{name}/{spec.FileName}";
            ClientComponentBundleManifestItem item = bundleManifest.Components.SingleOrDefault(
                value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"组件 bundle 缺少内部清单项：{name}。");
            if (!item.Name.Equals(name, StringComparison.Ordinal)
                || !item.Sha256.Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase)
                || item.Size != definition.Size
                || !item.Path.Equals(expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"组件 bundle 内部清单项无效：{name}。");
            }
            ZipArchiveEntry entry = archive.GetEntry(expectedPath)
                ?? throw new InvalidDataException($"组件 bundle 缺少文件：{name}。");
            expectedEntries.Add(expectedPath);
            verifiedEntries.Add((name, definition, spec, entry));
        }
        if (entries.Length != expectedEntries.Count
            || entries.Any(entry => !expectedEntries.Contains(entry.FullName)))
        {
            throw new InvalidDataException("组件 bundle 包含未声明的多余条目。");
        }

        string transactionId = Guid.NewGuid().ToString("N");
        string stagingRoot = Path.Combine(
            componentsRoot,
            ClientStorageMaintenanceService.BundleStagingDirectoryPrefix + transactionId);
        Directory.CreateDirectory(stagingRoot);
        FileStream? transactionLease = null;
        var staged = new List<(string Name, ClientComponentDefinition Definition, ComponentSpec Spec, string Path)>();
        var mutations = new List<(string Target, string Backup, bool HadOriginal)>();
        try
        {
            transactionLease = new FileStream(
                Path.Combine(
                    stagingRoot,
                    ClientStorageMaintenanceService.BundleTransactionLockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            transactionLease.Flush(flushToDisk: true);
            foreach ((string name, ClientComponentDefinition definition, ComponentSpec spec, ZipArchiveEntry entry) in verifiedEntries)
            {
                string stagedPath = Path.Combine(stagingRoot, name);
                ExtractAndVerifyBundleEntry(entry, stagedPath, definition.Size, definition.Sha256, name);
                staged.Add((name, definition, spec, stagedPath));
            }
            foreach ((string name, ClientComponentDefinition definition, ComponentSpec spec, string stagedPath) in staged)
            {
                string targetDirectory = Path.Combine(
                    componentsRoot,
                    name,
                    SanitizePathSegment(definition.Version));
                Directory.CreateDirectory(targetDirectory);
                string target = Path.Combine(targetDirectory, spec.FileName);
                string backup = target
                    + ClientStorageMaintenanceService.BundleBackupMarker
                    + transactionId
                    + ClientStorageMaintenanceService.BundleBackupSuffix;
                bool hadOriginal = File.Exists(target);
                if (hadOriginal)
                    File.Move(target, backup, overwrite: false);
                mutations.Add((target, backup, hadOriginal));
                File.Move(stagedPath, target, overwrite: false);
            }
            foreach ((_, string backup, _) in mutations)
                TryDelete(backup);
        }
        catch (Exception installError)
        {
            var rollbackErrors = new List<Exception>();
            foreach ((string target, string backup, bool hadOriginal) in mutations.AsEnumerable().Reverse())
            {
                try
                {
                    TryDelete(target);
                    if (hadOriginal && File.Exists(backup))
                        File.Move(backup, target, overwrite: false);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("组件 bundle 缓存事务回滚失败。", new[] { installError }.Concat(rollbackErrors));
            throw;
        }
        finally
        {
            transactionLease?.Dispose();
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Staging is not active content. Startup maintenance recognizes
                // only this transaction's strict GUID name and lease marker.
            }
        }
    }

    internal static IReadOnlyList<string> InstallBundleArchiveForTest(
        string archivePath,
        ClientComponentManifest manifest,
        string channel,
        string manifestSha256,
        string componentsRoot)
    {
        IReadOnlyList<KeyValuePair<string, ClientComponentDefinition>> candidates = manifest.Components
            .Concat(manifest.Files)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        InstallBundleArchive(archivePath, candidates, channel, manifestSha256, componentsRoot);
        return candidates.Select(pair => pair.Key).ToArray();
    }

    private static byte[] ReadBoundedEntry(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException("组件 bundle 内部清单过大。");
        using Stream source = entry.Open();
        using var destination = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException("组件 bundle 内部清单过大。");
            destination.Write(buffer, 0, read);
        }
    }

    private static void ExtractAndVerifyBundleEntry(
        ZipArchiveEntry entry,
        string destinationPath,
        long expectedSize,
        string expectedSha256,
        string name)
    {
        if (expectedSize <= 0 || entry.Length != expectedSize)
            throw new InvalidDataException($"组件 bundle 条目大小无效：{name}。");
        using Stream source = entry.Open();
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 128];
        long total = 0;
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total += read;
            if (total > expectedSize)
                throw new InvalidDataException($"组件 bundle 条目超出声明大小：{name}。");
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        destination.Flush(flushToDisk: true);
        string actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (total != expectedSize || !actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"组件 bundle 条目校验失败：{name}。");
    }

}
