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
    private async Task<VerifiedClientComponent> EnsureCachedComponentAsync(
        string name,
        ClientComponentDefinition definition,
        string channel,
        Uri manifestUri,
        ClientComponentDownloadProgress? downloadProgress,
        CancellationToken cancellationToken)
        => await EnsureCachedComponentAsync(
            name,
            definition,
            channel,
            manifestUri,
            SupportedComponents,
            downloadProgress,
            cancellationToken).ConfigureAwait(false);

    private async Task<VerifiedClientComponent> EnsureCachedComponentAsync(
        string name,
        ClientComponentDefinition definition,
        string channel,
        Uri manifestUri,
        IReadOnlyDictionary<string, ComponentSpec> supported,
        ClientComponentDownloadProgress? downloadProgress,
        CancellationToken cancellationToken)
    {
        ComponentSpec spec = supported[name];
        Uri source = ResolveAndValidateSourceUri(manifestUri, definition.Url);
        string versionDirectory = SanitizePathSegment(definition.Version);
        string cacheDirectory = Path.Combine(LogService.PersistentDataDirectory, "components", name, versionDirectory);
        Directory.CreateDirectory(cacheDirectory);
        string targetPath = Path.Combine(cacheDirectory, spec.FileName);
        string expectedHash = definition.Sha256.ToUpperInvariant();
        string temporaryPath = targetPath + ".download";
        string checkpointPath = temporaryPath + ".json";
        string lockPath = temporaryPath + ".lock";
        string lockKey = Path.GetFullPath(targetPath);
        SemaphoreSlim downloadGate = ComponentDownloadLocks.GetOrAdd(
            lockKey,
            static _ => new SemaphoreSlim(1, 1));

        await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream downloadLock = await AcquireDownloadFileLockAsync(
                lockPath,
                cancellationToken).ConfigureAwait(false);

            if (File.Exists(targetPath))
            {
                string existingHash = ComputeSha256(targetPath);
                long existingSize = new FileInfo(targetPath).Length;
                if (existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
                    && (definition.Size <= 0 || existingSize == definition.Size))
                {
                    if (downloadProgress is not null)
                        ReportProgress(downloadProgress.AdvanceArtifact(name, definition.Size));
                    LogService.Info(
                        $"Reusing verified component: component={name}, channel={channel}, version={definition.Version}, path={targetPath}, sha256={existingHash}");
                    return new VerifiedClientComponent(name, definition.Version, existingHash, targetPath, channel, source);
                }
                LogService.Warning(
                    $"Cached component mismatch; redownloading. component={name}, expectedSha={expectedHash}, actualSha={existingHash}, expectedSize={definition.Size}, actualSize={existingSize}, path={targetPath}");
            }

            string backupPath = targetPath + ".bak";
            var identity = new ComponentDownloadIdentity(
                channel,
                name,
                definition.Version,
                source.AbsoluteUri,
                expectedHash,
                definition.Size);
            TimeSpan downloadTimeout = CalculateArtifactDownloadTimeout(definition.Size);
            long downloadedBytes = await DownloadFileWithTimeoutAsync(
                source,
                temporaryPath,
                checkpointPath,
                identity,
                downloadTimeout,
                downloadProgress,
                cancellationToken).ConfigureAwait(false);
            try
            {
                ReportProgress(ClientUpdateProgressStage.VerifyingArtifacts);
                if (definition.Size > 0 && downloadedBytes != definition.Size)
                    throw new InvalidDataException($"组件 {name} 文件大小不一致：expected={definition.Size}, actual={downloadedBytes}。");

                string actualHash = ComputeSha256(temporaryPath);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException($"组件 {name} SHA256 校验失败：expected={expectedHash}, actual={actualHash}。");

                // The response has already been streamed to the temporary file.
                string diskHash = ComputeSha256(temporaryPath);
                if (!diskHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException($"组件 {name} 临时文件写入后校验不一致。");
            }
            catch
            {
                DiscardPartialDownload(temporaryPath, checkpointPath);
                throw;
            }

            try
            {
                TryDelete(checkpointPath);
                TryDelete(backupPath);
                if (File.Exists(targetPath))
                    File.Move(targetPath, backupPath, overwrite: true);
                File.Move(temporaryPath, targetPath, overwrite: true);
                string finalHash = ComputeSha256(targetPath);
                if (!finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException($"组件 {name} 原子替换后校验不一致。");
                TryDelete(backupPath);
                LogService.Info(
                    $"Component downloaded and verified: component={name}, channel={channel}, version={definition.Version}, bytes={downloadedBytes}, timeout={downloadTimeout}, sha256={finalHash}, source={source}");
                return new VerifiedClientComponent(name, definition.Version, finalHash, targetPath, channel, source);
            }
            catch
            {
                DiscardPartialDownload(temporaryPath, checkpointPath);
                if (File.Exists(backupPath))
                    File.Move(backupPath, targetPath, overwrite: true);
                throw;
            }
        }
        finally
        {
            downloadGate.Release();
        }
    }

    internal static TimeSpan CalculateArtifactDownloadTimeout(long declaredSize)
    {
        if (declaredSize <= 0)
            return MinimumArtifactDownloadTimeout;

        double transferSeconds = declaredSize / (double)MinimumExpectedDownloadBytesPerSecond;
        TimeSpan estimated = TimeSpan.FromSeconds(transferSeconds + 60);
        if (estimated < MinimumArtifactDownloadTimeout)
            return MinimumArtifactDownloadTimeout;
        return estimated > MaximumArtifactDownloadTimeout
            ? MaximumArtifactDownloadTimeout
            : estimated;
    }

    private async Task<long> DownloadFileWithTimeoutAsync(
        Uri source,
        string destinationPath,
        string checkpointPath,
        ComponentDownloadIdentity identity,
        TimeSpan timeout,
        ClientComponentDownloadProgress? downloadProgress,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        if (TryUseCompletedPartialDownload(destinationPath, checkpointPath, identity, out long completedBytes))
        {
            ReportProgress(downloadProgress?.AdvanceArtifact(identity.Component, completedBytes)
                ?? new ClientUpdateProgress(
                    ClientUpdateProgressStage.Downloading,
                    IsIndeterminate: identity.Size <= 0,
                    BytesCompleted: completedBytes,
                    BytesTotal: Math.Max(0, identity.Size)));
            return completedBytes;
        }
        (long resumeOffset, string? resumeEntityTag) = LoadValidPartialDownload(
            destinationPath,
            checkpointPath,
            identity);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool requestedRange = resumeOffset > 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                if (requestedRange)
                {
                    request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
                    if (!string.IsNullOrWhiteSpace(resumeEntityTag)
                        && EntityTagHeaderValue.TryParse(resumeEntityTag, out EntityTagHeaderValue? entityTag))
                    {
                        request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
                    }
                }

                using HttpResponseMessage response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token).ConfigureAwait(false);

                if (requestedRange && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    DiscardPartialDownload(destinationPath, checkpointPath);
                    resumeOffset = 0;
                    resumeEntityTag = null;
                    continue;
                }

                bool append = requestedRange && response.StatusCode == HttpStatusCode.PartialContent;
                if (append)
                {
                    if (!IsValidPartialContentResponse(response, resumeOffset, identity.Size)
                        || (!string.IsNullOrWhiteSpace(resumeEntityTag)
                            && !string.Equals(
                                resumeEntityTag,
                                response.Headers.ETag?.ToString(),
                                StringComparison.Ordinal)))
                    {
                        DiscardPartialDownload(destinationPath, checkpointPath);
                        resumeOffset = 0;
                        resumeEntityTag = null;
                        continue;
                    }
                }
                else if (requestedRange && response.StatusCode == HttpStatusCode.OK)
                {
                    // The origin ignored Range or If-Range rejected the checkpoint.
                    // Consume this response as a clean full download rather than issuing
                    // a second request.
                    DiscardPartialDownload(destinationPath, checkpointPath);
                    resumeOffset = 0;
                    resumeEntityTag = null;
                }
                else if (requestedRange)
                {
                    response.EnsureSuccessStatusCode();
                    throw new InvalidDataException("组件续传响应不是合法的 200 或 206。");
                }

                response.EnsureSuccessStatusCode();
                ValidateDownloadResponseLength(response, resumeOffset, identity.Size);
                string? responseEntityTag = response.Headers.ETag?.ToString();
                resumeEntityTag = responseEntityTag;
                long completedLength;
                ReportProgress(downloadProgress?.AdvanceArtifact(identity.Component, resumeOffset)
                    ?? new ClientUpdateProgress(
                        ClientUpdateProgressStage.Downloading,
                        IsIndeterminate: identity.Size <= 0,
                        BytesCompleted: resumeOffset,
                        BytesTotal: Math.Max(0, identity.Size)));
                await using (Stream sourceStream = await response.Content
                    .ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false))
                await using (var destination = new FileStream(
                    destinationPath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 128];
                    while (true)
                    {
                        int read = await sourceStream.ReadAsync(
                            buffer.AsMemory(),
                            linkedCts.Token).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        if (identity.Size > 0 && destination.Length + read > identity.Size)
                            throw new InvalidDataException("组件下载内容超出服务器描述声明大小。");
                        await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            linkedCts.Token).ConfigureAwait(false);
                        ReportProgress(downloadProgress?.AdvanceArtifact(identity.Component, destination.Length)
                            ?? new ClientUpdateProgress(
                                ClientUpdateProgressStage.Downloading,
                                IsIndeterminate: identity.Size <= 0,
                                BytesCompleted: destination.Length,
                                BytesTotal: Math.Max(0, identity.Size)));
                    }
                    await destination.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                    completedLength = destination.Length;
                }
                SavePartialDownloadCheckpoint(
                    destinationPath,
                    checkpointPath,
                    identity,
                    responseEntityTag);
                if ((identity.Size > 0 && completedLength != identity.Size)
                    || !ComputeSha256(destinationPath).Equals(
                        identity.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    DiscardPartialDownload(destinationPath, checkpointPath);
                    throw new CryptographicException("组件下载完成后的大小或 SHA256 校验失败。");
                }
                TryDelete(checkpointPath);
                TryDelete(checkpointPath + ".tmp");
                return completedLength;
            }
            catch (OperationCanceledException ex)
            {
                SavePartialDownloadCheckpointIfPossible(
                    destinationPath,
                    checkpointPath,
                    identity,
                    resumeEntityTag);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new TimeoutException($"Component download timed out after {timeout}: {source}", ex);
            }
            catch (InvalidDataException)
            {
                DiscardPartialDownload(destinationPath, checkpointPath);
                throw;
            }
            catch (CryptographicException)
            {
                DiscardPartialDownload(destinationPath, checkpointPath);
                throw;
            }
            catch
            {
                SavePartialDownloadCheckpointIfPossible(
                    destinationPath,
                    checkpointPath,
                    identity,
                    resumeEntityTag);
                throw;
            }
        }

        throw new InvalidDataException("组件续传响应无法安全收敛为完整下载。");
    }

    private static bool TryUseCompletedPartialDownload(
        string destinationPath,
        string checkpointPath,
        ComponentDownloadIdentity identity,
        out long completedBytes)
    {
        completedBytes = 0;
        if (identity.Size <= 0 || !File.Exists(destinationPath))
            return false;
        try
        {
            FileInfo partial = new(destinationPath);
            if ((partial.Attributes & FileAttributes.ReparsePoint) != 0
                || partial.Length != identity.Size
                || !ComputeSha256(destinationPath).Equals(
                    identity.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            TryDelete(checkpointPath);
            TryDelete(checkpointPath + ".tmp");
            completedBytes = partial.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidPartialContentResponse(
        HttpResponseMessage response,
        long expectedOffset,
        long expectedSize)
    {
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (range == null
            || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || range.From != expectedOffset
            || range.To is null
            || range.To < range.From)
        {
            return false;
        }
        return expectedSize <= 0 || range.Length == expectedSize;
    }

    private static void ValidateDownloadResponseLength(
        HttpResponseMessage response,
        long existingBytes,
        long expectedSize)
    {
        if (expectedSize <= 0 || response.Content.Headers.ContentLength is not long responseBytes)
            return;
        if (responseBytes < 0 || existingBytes + responseBytes != expectedSize)
            throw new InvalidDataException("组件下载响应长度与服务器描述声明大小不一致。");
    }

    private static (long Offset, string? EntityTag) LoadValidPartialDownload(
        string destinationPath,
        string checkpointPath,
        ComponentDownloadIdentity identity)
    {
        if (!File.Exists(destinationPath) && !File.Exists(checkpointPath))
            return (0, null);
        if (!File.Exists(destinationPath) || !File.Exists(checkpointPath))
        {
            DiscardPartialDownload(destinationPath, checkpointPath);
            return (0, null);
        }

        try
        {
            FileInfo partial = new(destinationPath);
            FileInfo checkpointFile = new(checkpointPath);
            if ((partial.Attributes & FileAttributes.ReparsePoint) != 0
                || (checkpointFile.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("组件续传文件不能是重解析点。");
            }

            ComponentDownloadCheckpoint checkpoint = JsonSerializer.Deserialize<ComponentDownloadCheckpoint>(
                File.ReadAllBytes(checkpointPath),
                JsonOptions) ?? throw new InvalidDataException("组件续传元数据为空。");
            if (checkpoint.SchemaVersion != 1
                || !CheckpointMatches(checkpoint, identity)
                || checkpoint.DownloadedBytes <= 0
                || checkpoint.DownloadedBytes != partial.Length
                || (identity.Size > 0 && checkpoint.DownloadedBytes >= identity.Size)
                || !Sha256Pattern.IsMatch(checkpoint.PartialSha256)
                || !ComputeSha256(destinationPath).Equals(
                    checkpoint.PartialSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("组件续传元数据与本地 partial 不一致。");
            }
            return (checkpoint.DownloadedBytes, checkpoint.EntityTag);
        }
        catch
        {
            DiscardPartialDownload(destinationPath, checkpointPath);
            return (0, null);
        }
    }

    private static bool CheckpointMatches(
        ComponentDownloadCheckpoint checkpoint,
        ComponentDownloadIdentity identity)
        => checkpoint.Channel.Equals(identity.Channel, StringComparison.OrdinalIgnoreCase)
            && checkpoint.Component.Equals(identity.Component, StringComparison.OrdinalIgnoreCase)
            && checkpoint.Version.Equals(identity.Version, StringComparison.Ordinal)
            && checkpoint.Source.Equals(identity.Source, StringComparison.Ordinal)
            && checkpoint.Sha256.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase)
            && checkpoint.Size == identity.Size;

    private static void SavePartialDownloadCheckpointIfPossible(
        string destinationPath,
        string checkpointPath,
        ComponentDownloadIdentity identity,
        string? entityTag)
    {
        try
        {
            SavePartialDownloadCheckpoint(destinationPath, checkpointPath, identity, entityTag);
        }
        catch
        {
            DiscardPartialDownload(destinationPath, checkpointPath);
        }
    }

    private static void SavePartialDownloadCheckpoint(
        string destinationPath,
        string checkpointPath,
        ComponentDownloadIdentity identity,
        string? entityTag)
    {
        if (!File.Exists(destinationPath))
        {
            TryDelete(checkpointPath);
            return;
        }

        FileInfo partial = new(destinationPath);
        if ((partial.Attributes & FileAttributes.ReparsePoint) != 0
            || partial.Length <= 0
            || (identity.Size > 0 && partial.Length > identity.Size))
        {
            DiscardPartialDownload(destinationPath, checkpointPath);
            return;
        }
        if (identity.Size > 0 && partial.Length == identity.Size)
        {
            TryDelete(checkpointPath);
            TryDelete(checkpointPath + ".tmp");
            return;
        }

        var checkpoint = new ComponentDownloadCheckpoint
        {
            SchemaVersion = 1,
            Channel = identity.Channel,
            Component = identity.Component,
            Version = identity.Version,
            Source = identity.Source,
            Sha256 = identity.Sha256,
            Size = identity.Size,
            DownloadedBytes = partial.Length,
            PartialSha256 = ComputeSha256(destinationPath),
            EntityTag = entityTag
        };
        string stagingPath = checkpointPath + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions);
        try
        {
            using (var stream = new FileStream(
                stagingPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(stagingPath, checkpointPath, overwrite: true);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    private static void DiscardPartialDownload(string destinationPath, string checkpointPath)
    {
        TryDelete(destinationPath);
        TryDelete(checkpointPath);
        TryDelete(checkpointPath + ".tmp");
    }

    private static async Task<FileStream> AcquireDownloadFileLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(lockPath)
                    && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("组件下载锁不能是重解析点。");
                }
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<long> DownloadComponentForTestAsync(
        Uri source,
        string destinationPath,
        string channel,
        string component,
        string version,
        string sha256,
        long size,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        string lockKey = Path.GetFullPath(destinationPath);
        SemaphoreSlim downloadGate = ComponentDownloadLocks.GetOrAdd(
            lockKey,
            static _ => new SemaphoreSlim(1, 1));
        await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream downloadLock = await AcquireDownloadFileLockAsync(
                destinationPath + ".lock",
                cancellationToken).ConfigureAwait(false);
            return await DownloadFileWithTimeoutAsync(
                source,
                destinationPath,
                destinationPath + ".json",
                new ComponentDownloadIdentity(
                    channel,
                    component,
                    version,
                    source.AbsoluteUri,
                    sha256.ToUpperInvariant(),
                    size),
                timeout,
                downloadProgress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            downloadGate.Release();
        }
    }

    private static void WriteState(
        IReadOnlyDictionary<string, VerifiedClientComponent> components,
        string channel,
        Uri manifestUri)
    {
        string statePath = Path.Combine(LogService.PersistentDataDirectory, "components", "component_state.json");
        if (components.Count == 0)
        {
            string root = Path.GetDirectoryName(statePath)!;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = new
        {
            SchemaVersion = SupportedSchemaVersion,
            UpdatedAt = DateTimeOffset.Now,
            Channel = channel,
            Manifest = manifestUri.ToString(),
            Components = components.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    pair.Value.Version,
                    Sha256 = pair.Value.Sha256.ToLowerInvariant(),
                    pair.Value.FilePath,
                    Source = pair.Value.SourceUri.ToString()
                },
                StringComparer.OrdinalIgnoreCase)
        };
        string temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
        File.Move(temporary, statePath, overwrite: true);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SanitizePathSegment(string value)
    {
        string clean = string.Concat((value ?? "").Trim().Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '_' : ch));
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort only; the following replacement operation reports the real failure.
        }
    }
}
