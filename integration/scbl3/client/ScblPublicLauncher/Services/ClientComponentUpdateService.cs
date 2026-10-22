using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

public interface IClientComponentUpdateReader
{
    Task<IReadOnlyList<PendingComponentUpdate>> GetPendingComponentUpdatesAsync(
        CancellationToken cancellationToken = default);
}

public sealed class ClientComponentManifestChangedException : InvalidOperationException
{
    public ClientComponentManifestChangedException()
        : base("组件清单在显示更新说明后发生变化，必须重新确认新清单。")
    {
    }
}

internal sealed class ClientComponentDownloadProgress
{
    private readonly object _sync = new();
    private readonly IReadOnlyDictionary<string, long> _artifactTotals;
    private readonly Dictionary<string, long> _artifactCompleted;
    private readonly long _individualTotal;
    private bool _usingBundle;
    private long _bundleCompleted;
    private long _bundleTotal;

    public ClientComponentDownloadProgress(IReadOnlyDictionary<string, long> artifactTotals)
    {
        ArgumentNullException.ThrowIfNull(artifactTotals);
        _artifactTotals = artifactTotals.ToDictionary(
            pair => pair.Key,
            pair => Math.Max(0, pair.Value),
            StringComparer.OrdinalIgnoreCase);
        _artifactCompleted = _artifactTotals.Keys.ToDictionary(
            name => name,
            _ => 0L,
            StringComparer.OrdinalIgnoreCase);
        _individualTotal = _artifactTotals.Values.Aggregate(
            0L,
            (total, size) => checked(total + size));
    }

    public ClientUpdateProgress AdvanceArtifact(string name, long completedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            if (_usingBundle)
                return Create(_bundleCompleted, _bundleTotal);
            if (!_artifactTotals.TryGetValue(name, out long artifactTotal))
                return Create(Math.Max(0, completedBytes), 0);

            _artifactCompleted[name] = Math.Clamp(completedBytes, 0, artifactTotal);
            long completed = _artifactCompleted.Values.Aggregate(
                0L,
                (total, value) => checked(total + value));
            return Create(completed, _individualTotal);
        }
    }

    public ClientUpdateProgress AdvanceBundle(long completedBytes, long totalBytes)
    {
        lock (_sync)
        {
            _usingBundle = true;
            _bundleTotal = Math.Max(0, totalBytes);
            _bundleCompleted = _bundleTotal > 0
                ? Math.Clamp(completedBytes, 0, _bundleTotal)
                : Math.Max(0, completedBytes);
            return Create(_bundleCompleted, _bundleTotal);
        }
    }

    public void UseIndividualFallback()
    {
        lock (_sync)
        {
            _usingBundle = false;
            _bundleCompleted = 0;
            _bundleTotal = 0;
            foreach (string name in _artifactCompleted.Keys.ToArray())
                _artifactCompleted[name] = 0;
        }
    }

    private static ClientUpdateProgress Create(long completedBytes, long totalBytes)
        => new(
            ClientUpdateProgressStage.Downloading,
            IsIndeterminate: totalBytes <= 0,
            BytesCompleted: completedBytes,
            BytesTotal: totalBytes);
}

/// <summary>
/// Resolves immutable client components from the selected stable/test manifest.
/// Channel components are downloaded into a versioned verified cache. Hooks can be
/// activated immediately before game start; next-launch components are staged for the
/// startup bootstrap layer. Every downloaded artifact is pinned by size and SHA256.
/// </summary>
public sealed partial class ClientComponentUpdateService : IClientComponentUpdateReader
{
    public event EventHandler<ClientUpdateProgress>? ProgressChanged;

    private const int SupportedSchemaVersion = 2;
    private const int SupportedBundleSchemaVersion = 1;
    private const int MaximumBundleComponents = 6;
    private const int MaximumBundleManifestBytes = 64 * 1024;
    private const long MaximumBundleOverheadBytes = 2 * 1024 * 1024;
    private const int MaximumReleaseNotesPerLanguage = 6;
    private const int MaximumReleaseNoteCharacters = 240;
    private static readonly TimeSpan MinimumArtifactDownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumArtifactDownloadTimeout = TimeSpan.FromMinutes(30);
    private const long MinimumExpectedDownloadBytesPerSecond = 256L * 1024L;
    public const string LauncherComponentName = "launcher";
    private const string ComponentCatalogResourceName = "SCBL.ClientComponentCatalog.json";
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ComponentNamePattern = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UpdateModePattern = new("^[a-z][a-z-]{0,31}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HttpClient DefaultHttp = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ComponentDownloadLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly HttpClient _http;
    private readonly LauncherRuntimeContext _runtimeContext;
    private readonly Func<string> _getPackageRoot;
    private readonly Func<CancellationToken, Task<ServerDescriptorSnapshot>>? _getServerDescriptorAsync;

    private sealed record ComponentSource(
        ClientComponentManifest Manifest,
        Uri ManifestUri,
        string Generation);

    private sealed record ComponentDownloadIdentity(
        string Channel,
        string Component,
        string Version,
        string Source,
        string Sha256,
        long Size);

    private sealed class ComponentDownloadCheckpoint
    {
        public int SchemaVersion { get; init; }
        public string Channel { get; init; } = "";
        public string Component { get; init; } = "";
        public string Version { get; init; } = "";
        public string Source { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long Size { get; init; }
        public long DownloadedBytes { get; init; }
        public string PartialSha256 { get; init; } = "";
        public string? EntityTag { get; init; }
    }

    public ClientComponentUpdateService(
        LauncherRuntimeContext runtimeContext,
        HttpClient? httpClient = null,
        Func<CancellationToken, Task<ServerDescriptorSnapshot>>? getServerDescriptorAsync = null,
        Func<string>? getPackageRoot = null)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        _http = httpClient ?? DefaultHttp;
        _getPackageRoot = getPackageRoot ?? (static () => AppContext.BaseDirectory);
        _getServerDescriptorAsync = getServerDescriptorAsync;
    }

    private sealed record ComponentSpec(
        string FileName,
        string Section,
        string UpdateMode);

    private sealed record PackagedComponentReceipt(
        string Version,
        string ArtifactSha256,
        IReadOnlyDictionary<string, string> Files);

    private sealed class ComponentCatalogDocument
    {
        public int SchemaVersion { get; init; }
        public Dictionary<string, ComponentCatalogEntry> Components { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ComponentCatalogEntry
    {
        public string FileName { get; init; } = "";
        public string Section { get; init; } = "";
        public string UpdateMode { get; init; } = "";
    }

    private static readonly (
        IReadOnlyDictionary<string, ComponentSpec> Components,
        IReadOnlyDictionary<string, ComponentSpec> Files) ComponentCatalog =
        LoadComponentCatalog();

    private static readonly IReadOnlyDictionary<string, ComponentSpec> SupportedComponents =
        ComponentCatalog.Components;

    private static readonly IReadOnlyDictionary<string, ComponentSpec> SupportedFileBundles =
        ComponentCatalog.Files;

    private static (
        IReadOnlyDictionary<string, ComponentSpec> Components,
        IReadOnlyDictionary<string, ComponentSpec> Files) LoadComponentCatalog()
    {
        Assembly assembly = typeof(ClientComponentUpdateService).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ComponentCatalogResourceName)
            ?? throw new InvalidOperationException("内置客户端组件目录缺失。");
        ComponentCatalogDocument catalog = JsonSerializer.Deserialize<ComponentCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("内置客户端组件目录为空。");
        if (catalog.SchemaVersion != 1 || catalog.Components.Count == 0)
            throw new InvalidOperationException("内置客户端组件目录结构无效。");

        var components = new Dictionary<string, ComponentSpec>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ComponentSpec>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ComponentCatalogEntry entry) in catalog.Components)
        {
            if (!ComponentNamePattern.IsMatch(name)
                || string.IsNullOrWhiteSpace(entry.FileName)
                || !Path.GetFileName(entry.FileName).Equals(entry.FileName, StringComparison.Ordinal)
                || (entry.Section != "components" && entry.Section != "files")
                || !UpdateModePattern.IsMatch(entry.UpdateMode))
            {
                throw new InvalidOperationException($"内置客户端组件目录记录无效：{name}。");
            }
            var spec = new ComponentSpec(
                entry.FileName,
                entry.Section,
                entry.UpdateMode);
            Dictionary<string, ComponentSpec> section = entry.Section == "files" ? files : components;
            if (!section.TryAdd(name, spec))
                throw new InvalidOperationException($"内置客户端组件目录包含重复记录：{name}。");
        }
        if (!components.ContainsKey(LauncherComponentName))
            throw new InvalidOperationException("内置客户端组件目录缺少 launcher。");
        return (components, files);
    }

    internal static IReadOnlyDictionary<string, (string FileName, string Section, string UpdateMode)>
        ReadComponentCatalogContract()
        => SupportedComponents
            .Concat(SupportedFileBundles)
            .ToDictionary(
                pair => pair.Key,
                pair => (
                    pair.Value.FileName,
                    pair.Value.Section,
                    pair.Value.UpdateMode),
                StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, VerifiedClientComponent>> ReconcileSelectedChannelAsync(
        string expectedComponentGeneration,
        CancellationToken cancellationToken = default)
    {
        string channel = _runtimeContext.UpdateChannelName;
        try
        {
            ComponentSource source = await GetComponentSourceAsync(channel, cancellationToken)
                .ConfigureAwait(false);
            ClientComponentManifest manifest = source.Manifest;
            Uri manifestUri = source.ManifestUri;
            string manifestSha256 = source.Generation;
            string expected = expectedComponentGeneration.Trim().ToLowerInvariant();
            if (!Sha256Pattern.IsMatch(expected))
                throw new InvalidDataException("待确认组件清单 SHA256 格式无效。");
            if (!manifestSha256.Equals(expected, StringComparison.Ordinal))
                throw new ClientComponentManifestChangedException();

            await Task.Run(
                    () => AdoptVerifiedInstalledFacts(channel, manifest, manifestUri),
                    cancellationToken)
                .ConfigureAwait(false);

            var verified = new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, (string Version, string Sha256)> installed =
                await Task.Run(() => ReadInstalledComponentVersions(channel), cancellationToken)
                    .ConfigureAwait(false);
            IReadOnlyList<KeyValuePair<string, ClientComponentDefinition>> bundleCandidates =
                CollectBundleCandidates(manifest, installed, manifestUri);
            var downloadProgress = new ClientComponentDownloadProgress(
                bundleCandidates.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Size,
                    StringComparer.OrdinalIgnoreCase));
            if (bundleCandidates.Count > 0)
            {
                await TryPrefetchBundleAsync(
                    bundleCandidates,
                    channel,
                    manifestUri,
                    manifestSha256,
                    cancellationToken,
                    downloadProgress).ConfigureAwait(false);
            }
            foreach ((string name, ClientComponentDefinition definition) in manifest.Components.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                ValidateDefinition(name, definition, manifestUri);
                if (installed.TryGetValue(name, out (string Version, string Sha256) current))
                {
                    if (InstalledComponentSatisfiesCandidate(
                            name,
                            current.Version,
                            current.Sha256,
                            definition.Version,
                            definition.Sha256))
                    {
                        LogService.Info(
                            $"Component content already matches server manifest: component={name}, installed={current.Version}, serverVersion={definition.Version}; download skipped.");
                        continue;
                    }
                }
                VerifiedClientComponent component = await EnsureCachedComponentAsync(
                    name,
                    definition,
                    channel,
                    manifestUri,
                    downloadProgress,
                    cancellationToken).ConfigureAwait(false);
                verified[name] = component;
            }

            foreach ((string name, ClientComponentDefinition definition) in manifest.Files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                ValidateDefinition(name, definition, manifestUri, SupportedFileBundles);
                if (installed.TryGetValue(name, out (string Version, string Sha256) current))
                {
                    if (InstalledComponentSatisfiesCandidate(
                            name,
                            current.Version,
                            current.Sha256,
                            definition.Version,
                            definition.Sha256))
                    {
                        LogService.Info($"Client file bundle content already matches server manifest: bundle={name}, installed={current.Version}, serverVersion={definition.Version}; download skipped.");
                        continue;
                    }
                }

                VerifiedClientComponent bundle = await EnsureCachedComponentAsync(
                    name,
                    definition,
                    channel,
                    manifestUri,
                    SupportedFileBundles,
                    downloadProgress,
                    cancellationToken).ConfigureAwait(false);
                StagedComponentBootstrapService.ApplyClientFiles(bundle);
            }

            WriteState(verified, channel, manifestUri);
            LogService.Info(
                $"Component reconciliation completed: channel={channel}, count={verified.Count}, components={string.Join(',', verified.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClientComponentManifestChangedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{channel} 通道组件检查失败。为避免使用旧版或未知文件，已阻止继续启动。\n\n" + ex.Message,
                ex);
        }
    }

    public async Task<IReadOnlyDictionary<string, VerifiedClientComponent>> GetUnappliedReconciledComponentsAsync(
        IReadOnlyDictionary<string, VerifiedClientComponent> reconciled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciled);
        if (reconciled.Count == 0)
            return new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);

        string channel = _runtimeContext.UpdateChannelName;
        IReadOnlyDictionary<string, (string Version, string Sha256)> installed =
            await Task.Run(() => ReadInstalledComponentVersions(channel), cancellationToken)
                .ConfigureAwait(false);
        return CollectUnappliedReconciledComponents(reconciled, installed);
    }

    internal static IReadOnlyDictionary<string, VerifiedClientComponent> CollectUnappliedReconciledComponents(
        IReadOnlyDictionary<string, VerifiedClientComponent> reconciled,
        IReadOnlyDictionary<string, (string Version, string Sha256)> installed)
    {
        ArgumentNullException.ThrowIfNull(reconciled);
        ArgumentNullException.ThrowIfNull(installed);

        var unapplied = new Dictionary<string, VerifiedClientComponent>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, VerifiedClientComponent component) in reconciled)
        {
            if (installed.TryGetValue(name, out (string Version, string Sha256) current)
                && InstalledComponentSatisfiesCandidate(
                    name,
                    current.Version,
                    current.Sha256,
                    component.Version,
                    component.Sha256))
            {
                continue;
            }

            unapplied[name] = component;
        }
        return unapplied;
    }

    private static HttpClient CreateHttpClient()
        => DualStackHttpClient.Create(
            "SCBL-Component-Updater/3",
            Timeout.InfiniteTimeSpan);

    public async Task<IReadOnlyList<PendingComponentUpdate>> GetPendingComponentUpdatesAsync(
        CancellationToken cancellationToken = default)
        => (await PrepareSelectedChannelUpdateAsync(cancellationToken).ConfigureAwait(false)).PendingUpdates;

    public async Task<PreparedClientComponentUpdate> PrepareSelectedChannelUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        string channel = _runtimeContext.UpdateChannelName;
        ComponentSource source = await GetComponentSourceAsync(channel, cancellationToken)
            .ConfigureAwait(false);
        Uri manifestUri = source.ManifestUri;
        ClientComponentManifest manifest = source.Manifest;
        ReportProgress(ClientUpdateProgressStage.CheckingComponents);
        string manifestSha256 = source.Generation;
        await Task.Run(
                () => AdoptVerifiedInstalledFacts(channel, manifest, manifestUri),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<string, (string Version, string Sha256)> installed =
            await Task.Run(() => ReadInstalledComponentVersions(channel), cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<PendingComponentUpdate> pending =
            CollectPendingComponentUpdates(manifest, installed, manifestUri);
        return new PreparedClientComponentUpdate(channel, manifestSha256, pending);
    }

    private async Task<ComponentSource> GetComponentSourceAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        if (_getServerDescriptorAsync is null)
            throw new InvalidOperationException("客户端组件更新缺少服务器描述来源。");

        ServerDescriptorSnapshot snapshot = await _getServerDescriptorAsync(cancellationToken)
            .ConfigureAwait(false);
        ServerDescriptor descriptor = snapshot.Descriptor;
        if (!descriptor.Server.Mode.Equals(channel, StringComparison.Ordinal))
            throw new InvalidDataException("服务器描述与当前更新通道不一致。");
        int componentCount = descriptor.Client.Components.Count + descriptor.Client.Files.Count;
        string generation = descriptor.Client.ComponentGeneration.Trim().ToLowerInvariant();
        if (componentCount > 0 && !Sha256Pattern.IsMatch(generation))
            throw new InvalidDataException("服务器描述的组件代际无效。");
        if (componentCount == 0)
            generation = snapshot.ContentSha256;

        Uri updateBase = new(descriptor.Client.UpdateBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        Uri sourceUri = new(updateBase, "scbl-server.json");
        var manifest = new ClientComponentManifest
        {
            SchemaVersion = SupportedSchemaVersion,
            Channel = channel,
            GeneratedAt = descriptor.GeneratedAt,
            Components = CloneDescriptorDefinitions(
                descriptor.Client.Components,
                SupportedComponents),
            Files = CloneDescriptorDefinitions(
                descriptor.Client.Files,
                SupportedFileBundles)
        };
        return new ComponentSource(manifest, sourceUri, generation);
    }

    private static Dictionary<string, ClientComponentDefinition> CloneDescriptorDefinitions(
        IReadOnlyDictionary<string, ClientComponentDefinition> definitions,
        IReadOnlyDictionary<string, ComponentSpec> supported)
    {
        var result = new Dictionary<string, ClientComponentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ClientComponentDefinition source) in definitions)
        {
            if (!supported.TryGetValue(name, out ComponentSpec? spec))
                throw new InvalidDataException("服务器描述包含当前启动器不支持的组件：" + name);
            result[name] = new ClientComponentDefinition
            {
                Version = source.Version,
                Sha256 = source.Sha256,
                Size = source.Size,
                Url = source.Url,
                UpdateMode = source.UpdateMode,
                Required = source.Required,
                ReleaseNotes = source.ReleaseNotes
            };
        }
        return result;
    }

    internal static IReadOnlyList<PendingComponentUpdate> CollectPendingComponentUpdates(
        ClientComponentManifest manifest,
        IReadOnlyDictionary<string, (string Version, string Sha256)> installed,
        Uri manifestUri)
    {
        var pending = new List<PendingComponentUpdate>();
        foreach ((string name, ClientComponentDefinition definition) in manifest.Components)
        {
            ValidateDefinition(name, definition, manifestUri);
            if (!installed.TryGetValue(name, out (string Version, string Sha256) current))
            {
                pending.Add(CreatePendingComponentUpdate(name, definition));
                continue;
            }

            if (!InstalledComponentSatisfiesCandidate(
                    name,
                    current.Version,
                    current.Sha256,
                    definition.Version,
                    definition.Sha256))
            {
                pending.Add(CreatePendingComponentUpdate(name, definition));
            }
        }
        foreach ((string name, ClientComponentDefinition definition) in manifest.Files)
        {
            ValidateDefinition(name, definition, manifestUri, SupportedFileBundles);
            if (!installed.TryGetValue(name, out (string Version, string Sha256) current))
            {
                pending.Add(CreatePendingComponentUpdate(name, definition));
                continue;
            }
            if (!InstalledComponentSatisfiesCandidate(
                    name,
                    current.Version,
                    current.Sha256,
                    definition.Version,
                    definition.Sha256))
            {
                pending.Add(CreatePendingComponentUpdate(name, definition));
            }
        }
        return pending;
    }

    private static PendingComponentUpdate CreatePendingComponentUpdate(
        string name,
        ClientComponentDefinition definition)
    {
        (IReadOnlyList<string> zh, IReadOnlyList<string> en) =
            NormalizeReleaseNotes(name, definition.ReleaseNotes);
        return new PendingComponentUpdate(name, definition.Version, zh, en);
    }

    private void ReportProgress(
        ClientUpdateProgressStage stage,
        bool isIndeterminate = true,
        long bytesCompleted = 0,
        long bytesTotal = 0)
        => ProgressChanged?.Invoke(
            this,
            new ClientUpdateProgress(stage, isIndeterminate, bytesCompleted, bytesTotal));

    private void ReportProgress(ClientUpdateProgress progress)
        => ProgressChanged?.Invoke(this, progress);

    private static void ValidateDefinition(string name, ClientComponentDefinition definition, Uri manifestUri)
        => ValidateDefinition(name, definition, manifestUri, SupportedComponents);

    private static void ValidateDefinition(
        string name,
        ClientComponentDefinition definition,
        Uri manifestUri,
        IReadOnlyDictionary<string, ComponentSpec> supported)
    {
        if (!supported.TryGetValue(name, out ComponentSpec? spec))
            throw new InvalidDataException("组件清单包含当前启动器不支持的组件：" + name);
        if (string.IsNullOrWhiteSpace(definition.Version))
            throw new InvalidDataException($"组件 {name} 版本为空。");

        definition.Sha256 = definition.Sha256.Trim();
        if (!Sha256Pattern.IsMatch(definition.Sha256))
            throw new InvalidDataException($"组件 {name} SHA256 格式无效。");
        if (definition.Size < 0)
            throw new InvalidDataException($"组件 {name} 大小无效。");
        if (string.IsNullOrWhiteSpace(definition.Url))
            throw new InvalidDataException($"组件 {name} 下载地址为空。");
        if (!definition.Required)
            throw new InvalidDataException($"组件 {name} 必须标记 required=true。");
        if (!definition.UpdateMode.Equals(spec.UpdateMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"组件 {name} updateMode 不匹配：expected={spec.UpdateMode}, actual={definition.UpdateMode}。");
        _ = NormalizeReleaseNotes(name, definition.ReleaseNotes);

        Uri source = ResolveAndValidateSourceUri(manifestUri, definition.Url);
        if (!source.AbsolutePath.EndsWith("/" + spec.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"组件 {name} 下载文件名无效，必须为 {spec.FileName}。");
    }

    internal static (IReadOnlyList<string> Zh, IReadOnlyList<string> En) NormalizeReleaseNotes(
        string component,
        ClientComponentReleaseNotes? releaseNotes)
    {
        if (releaseNotes == null)
            return (Array.Empty<string>(), Array.Empty<string>());

        return (
            NormalizeReleaseNoteLanguage(component, "zh", releaseNotes.Zh),
            NormalizeReleaseNoteLanguage(component, "en", releaseNotes.En));
    }

    private static IReadOnlyList<string> NormalizeReleaseNoteLanguage(
        string component,
        string language,
        IReadOnlyList<string>? values)
    {
        if (values == null || values.Count is < 1 or > MaximumReleaseNotesPerLanguage)
        {
            throw new InvalidDataException(
                $"组件 {component} releaseNotes.{language} 必须包含 1～{MaximumReleaseNotesPerLanguage} 条说明。");
        }

        var normalized = new string[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            string value = (values[index] ?? "").Trim().Normalize(NormalizationForm.FormC);
            if (string.IsNullOrEmpty(value)
                || CountUnicodeScalars(value) > MaximumReleaseNoteCharacters
                || value.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    $"组件 {component} releaseNotes.{language}[{index}] 为空、过长或包含控制字符。");
            }
            normalized[index] = value;
        }
        return Array.AsReadOnly(normalized);
    }

    private static int CountUnicodeScalars(string value)
    {
        int count = 0;
        foreach (Rune _ in value.EnumerateRunes())
            count++;
        return count;
    }

    private static Uri ResolveAndValidateSourceUri(Uri manifestUri, string value)
    {
        string trimmed = value.Trim();
        Uri source = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(trimmed, UriKind.Absolute)
            : new Uri(manifestUri, trimmed);

        if (!source.Scheme.Equals(manifestUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !source.Host.Equals(manifestUri.Host, StringComparison.OrdinalIgnoreCase)
            || source.Port != manifestUri.Port)
        {
            throw new InvalidDataException("组件下载地址必须与组件清单来自同一更新服务。");
        }
        return source;
    }

    internal static bool InstalledComponentSatisfiesCandidate(
        string name,
        string currentVersion,
        string currentSha256,
        string candidateVersion,
        string candidateSha256)
    {
        return !string.IsNullOrWhiteSpace(currentVersion)
            && currentVersion.Equals(candidateVersion, StringComparison.OrdinalIgnoreCase)
            && Sha256Pattern.IsMatch(currentSha256)
            && Sha256Pattern.IsMatch(candidateSha256)
            && currentSha256.Equals(candidateSha256, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, (string Version, string Sha256)> ReadInstalledComponentVersions(string channel)
    {
        var result = new Dictionary<string, (string Version, string Sha256)>(
            ReadPackagedComponentVersions(_getPackageRoot()),
            StringComparer.OrdinalIgnoreCase);
        // The portable full package contains Route Guard source files, but the
        // component is not active until those files have been installed into
        // the protected ProgramData runtime.  The package receipt must never
        // suppress the signed component download/application transaction.
        result.Remove("route-guard");
        foreach (KeyValuePair<string, (string Version, string Sha256)> receipt in InstalledComponentReceiptService.Read(channel))
            result[receipt.Key] = receipt.Value;
        // component_state.json is staging, not proof that a component is active.
        // Only the full-package baseline and the post-activation receipt may
        // suppress a download. Pending payloads are reused by
        // EnsureCachedComponentAsync and remain eligible for activation.
        return result;
    }

    private void AdoptVerifiedInstalledFacts(
        string channel,
        ClientComponentManifest manifest,
        Uri manifestUri)
    {
        IReadOnlyDictionary<string, (string Version, string Sha256)> currentReceipts =
            InstalledComponentReceiptService.Read(channel);
        foreach ((string name, ClientComponentDefinition definition) in manifest.Components)
        {
            ValidateDefinition(name, definition, manifestUri);
            bool receiptAlreadyMatches = currentReceipts.TryGetValue(
                    name,
                    out (string Version, string Sha256) current)
                && current.Version.Equals(definition.Version, StringComparison.Ordinal)
                && current.Sha256.Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase);
            bool adoptedFromPackage = !receiptAlreadyMatches
                && name.Equals("hooks", StringComparison.OrdinalIgnoreCase)
                && _getServerDescriptorAsync is not null
                && TryAdoptVerifiedPackagedHooksReceipt(channel, definition, _getPackageRoot());
            if (!receiptAlreadyMatches && !adoptedFromPackage)
            {
                InstalledComponentReceiptService.TryAdoptInstalledFact(
                    channel,
                    name,
                    definition.Version,
                    definition.Sha256,
                    definition.Size);
            }
        }
        foreach ((string name, ClientComponentDefinition definition) in manifest.Files)
        {
            ValidateDefinition(name, definition, manifestUri, SupportedFileBundles);
            InstalledComponentReceiptService.TryAdoptInstalledFact(
                channel,
                name,
                definition.Version,
                definition.Sha256,
                definition.Size);
        }
    }

    private static bool TryAdoptVerifiedPackagedHooksReceipt(
        string channel,
        ClientComponentDefinition descriptorDefinition,
        string baseDirectory)
    {
        try
        {
            IReadOnlyDictionary<string, PackagedComponentReceipt> receipts =
                ReadPackagedComponentReceipts(baseDirectory, out bool hasReceiptSection);
            if (!hasReceiptSection
                || !receipts.TryGetValue("hooks", out PackagedComponentReceipt? packaged)
                || !packaged.Version.Equals(descriptorDefinition.Version, StringComparison.Ordinal)
                || !packaged.ArtifactSha256.Equals(descriptorDefinition.Sha256, StringComparison.OrdinalIgnoreCase)
                || packaged.Files.Count != 1)
            {
                return false;
            }

            string root = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string hooksPath = ClientInstallLayout.Resolve(root, ClientInstallLayout.HooksRelativePath);
            if (!packaged.Files.TryGetValue(hooksPath, out string? packagedFileSha)
                || new FileInfo(hooksPath).Length != descriptorDefinition.Size)
            {
                return false;
            }

            string actualSha = ComputeSha256(hooksPath);
            string sidecarPath = hooksPath + ".sha256";
            if (!File.Exists(sidecarPath)
                || (File.GetAttributes(sidecarPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            MatchCollection sidecarHashes = Regex.Matches(
                File.ReadAllText(sidecarPath),
                "(?i)\\b[0-9a-f]{64}\\b",
                RegexOptions.CultureInvariant);
            if (sidecarHashes.Count != 1
                || !actualSha.Equals(descriptorDefinition.Sha256, StringComparison.OrdinalIgnoreCase)
                || !actualSha.Equals(packagedFileSha, StringComparison.OrdinalIgnoreCase)
                || !actualSha.Equals(sidecarHashes[0].Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            InstalledComponentReceiptService.MarkApplied(
                channel,
                "hooks",
                descriptorDefinition.Version,
                descriptorDefinition.Sha256,
                descriptorDefinition.Size,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [hooksPath] = actualSha
                },
                baseDirectory);
            LogService.Info(
                $"Fresh full-package Hooks adopted after descriptor and package verification: channel={channel}, version={descriptorDefinition.Version}, sha256={actualSha}");
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or JsonException
                                   or CryptographicException)
        {
            LogService.Warning("Packaged Hooks receipt was not adopted; server descriptor reconciliation remains required: " + ex.Message);
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, (string Version, string Sha256)> ReadPackagedComponentVersions(
        string baseDirectory)
    {
        var result = new Dictionary<string, (string Version, string Sha256)>(StringComparer.OrdinalIgnoreCase);
        string manifestPath = Path.Combine(baseDirectory, "client_package_manifest.json");
        try
        {
            IReadOnlyDictionary<string, PackagedComponentReceipt> receipts =
                ReadPackagedComponentReceipts(baseDirectory, out bool hasReceiptSection);
            if (hasReceiptSection)
            {
                foreach ((string name, PackagedComponentReceipt receipt) in receipts)
                    result[name] = (receipt.Version, receipt.ArtifactSha256);
                return result;
            }

            if (!File.Exists(manifestPath))
                return result;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("componentVersions", out JsonElement versions)
                || versions.ValueKind != JsonValueKind.Object)
                return result;
            foreach (JsonProperty property in versions.EnumerateObject())
            {
                if (!SupportedComponents.ContainsKey(property.Name))
                    continue;
                string version = property.Value.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(version) && Regex.IsMatch(version, "[0-9]"))
                    result[property.Name] = (version, "");
            }
        }
        catch (Exception ex)
        {
            LogService.Warning("Packaged component versions are unreadable; server reconciliation will verify components: " + ex.Message);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, PackagedComponentReceipt> ReadPackagedComponentReceipts(
        string baseDirectory,
        out bool hasReceiptSection)
    {
        hasReceiptSection = false;
        var result = new Dictionary<string, PackagedComponentReceipt>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string manifestPath = Path.Combine(root, "client_package_manifest.json");
        if (!File.Exists(manifestPath))
            return result;
        FileInfo manifestFile = new(manifestPath);
        if ((manifestFile.Attributes & FileAttributes.ReparsePoint) != 0
            || manifestFile.Length <= 0
            || manifestFile.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("完整包组件回执清单文件无效。");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (!document.RootElement.TryGetProperty("componentReceipts", out JsonElement receipts))
            return result;
        hasReceiptSection = true;
        if (receipts.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("完整包组件回执区段无效。");

        var componentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in receipts.EnumerateObject())
        {
            if (!componentNames.Add(property.Name))
                throw new InvalidDataException("完整包包含重复组件回执。");
            if (!SupportedComponents.ContainsKey(property.Name)
                && !SupportedFileBundles.ContainsKey(property.Name))
            {
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("version", out JsonElement versionElement)
                || !property.Value.TryGetProperty("sha256", out JsonElement shaElement)
                || !property.Value.TryGetProperty("files", out JsonElement filesElement)
                || filesElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string version = versionElement.GetString() ?? "";
            string artifactSha = shaElement.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(version)
                || !Regex.IsMatch(version, "[0-9]")
                || !Sha256Pattern.IsMatch(artifactSha))
            {
                continue;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var relativeNames = new HashSet<string>(StringComparer.Ordinal);
            bool valid = true;
            foreach (JsonProperty file in filesElement.EnumerateObject())
            {
                string expected = file.Value.GetString() ?? "";
                if (!relativeNames.Add(file.Name)
                    || !TryResolvePackagedReceiptPath(root, file.Name, out string? full)
                    || !Sha256Pattern.IsMatch(expected)
                    || !File.Exists(full)
                    || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0
                    || !ComputeSha256(full).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    valid = false;
                    break;
                }
                files[full!] = expected.ToLowerInvariant();
            }
            if (valid
                && files.Count > 0
                && (!property.Name.Equals("hooks", StringComparison.OrdinalIgnoreCase)
                    || PackagedHooksSidecarMatches(root, files)))
                result[property.Name] = new PackagedComponentReceipt(version, artifactSha, files);
        }
        return result;
    }

    private static bool PackagedHooksSidecarMatches(
        string root,
        IReadOnlyDictionary<string, string> files)
    {
        string hooksPath = ClientInstallLayout.Resolve(root, ClientInstallLayout.HooksRelativePath);
        if (files.Count != 1
            || !files.TryGetValue(hooksPath, out string? hooksSha)
            || !File.Exists(hooksPath + ".sha256")
            || (File.GetAttributes(hooksPath + ".sha256") & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }
        MatchCollection hashes = Regex.Matches(
            File.ReadAllText(hooksPath + ".sha256"),
            "(?i)\\b[0-9a-f]{64}\\b",
            RegexOptions.CultureInvariant);
        return hashes.Count == 1
            && hooksSha.Equals(hashes[0].Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolvePackagedReceiptPath(
        string root,
        string relative,
        out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(relative)
            || relative.Contains('\\')
            || relative.Contains(':')
            || relative.IndexOfAny(['?', '#']) >= 0
            || Path.IsPathRooted(relative))
        {
            return false;
        }
        string[] segments = relative.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            return false;
        }
        string normalized = string.Join('/', segments);
        if (!normalized.Equals(relative, StringComparison.Ordinal))
            return false;

        string rootPrefix = root + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        fullPath = resolved;
        return true;
    }

}
