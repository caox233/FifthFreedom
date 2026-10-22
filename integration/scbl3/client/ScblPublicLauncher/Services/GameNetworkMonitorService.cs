using SplinterCellCNLauncher.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

public sealed record GameNetworkObservation(
    bool Active,
    bool LocalIsHost,
    string PeerIp,
    long? LatencyMs,
    string Transport,
    string AddressFamily,
    string NextHop,
    int? HopCount,
    int ActivePeerCount,
    string RoleSource,
    string HostUsername,
    long? SessionId,
    long? LatencyP50Ms,
    long? LatencyP95Ms,
    long? JitterMs,
    double? LossPercent);

internal sealed record GameNetworkMonitorDependencies(
    Func<string, string, CancellationToken, Task<ControlPlaneGameSession?>> GetGameSessionAsync,
    Func<string, string, TimeSpan, Task<EasyTierPeerPath?>> DetectPeerPathAsync,
    Action<string, string, string> StartPeerProbe,
    Func<string, TimeSpan, CancellationToken, Task<(bool Ok, long? LatencyMs)>> ProbePeerLatencyAsync,
    Func<string, int, TimeSpan, string, Task<(bool Ok, long? LatencyMs)>> ProbeServerAsync,
    Action<string> RequestServerPathValidation,
    Action<GameNetworkObservation> WriteQualitySnapshot);

/// <summary>
/// Owns game-session path discovery, sampling and the runtime quality receipt.
/// The WPF host only supplies immutable observations and projects emitted state.
/// </summary>
public sealed class GameNetworkMonitorService : IDisposable
{
    private sealed record QualitySample(DateTime AtUtc, bool Success, long? LatencyMs);

    private readonly GameNetworkMonitorDependencies _dependencies;
    private readonly Func<bool> _isGameSessionActive;
    private readonly Func<string> _getBindIp;
    private readonly Func<string> _getSigningSecret;
    private readonly Func<string> _getLauncherBaseDirectory;
    private readonly Func<string> _getUsername;
    private readonly Func<IReadOnlyList<PeerInfo>> _getPeers;
    private readonly Func<long?> _getLastServerLatency;
    private readonly Func<string> _getServerVirtualIp;
    private readonly string _launcherVersion;
    private readonly object _lifecycleSync = new();
    private readonly object _publishSync = new();
    private readonly object _qualitySync = new();
    private readonly Queue<QualitySample> _qualitySamples = new();
    private readonly Queue<bool> _serverPathSamples = new();
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private long _generation;
    private bool _disposed;
    private string _qualityHostIp = "";
    private long? _p50;
    private long? _p95;
    private long? _jitter;
    private double? _loss;
    private bool _serverPathDegradationSignaled;
    private GameNetworkObservation _current = Empty(active: false);

    public GameNetworkMonitorService(
        ControlPlaneService controlPlaneService,
        PublicTunnelService tunnelService,
        PeerProbeService peerProbeService,
        Func<bool> isGameSessionActive,
        Func<string> getBindIp,
        Func<string> getSigningSecret,
        Func<string> getLauncherBaseDirectory,
        Func<string> getUsername,
        Func<IReadOnlyList<PeerInfo>> getPeers,
        Func<long?> getLastServerLatency,
        string launcherVersion,
        Func<string>? getServerVirtualIp = null)
    {
        ArgumentNullException.ThrowIfNull(controlPlaneService);
        ArgumentNullException.ThrowIfNull(tunnelService);
        ArgumentNullException.ThrowIfNull(peerProbeService);
        _dependencies = new GameNetworkMonitorDependencies(
            controlPlaneService.GetGameSessionAsync,
            tunnelService.DetectPeerPathAsync,
            peerProbeService.StartOrUpdate,
            PeerProbeService.ProbeLatencyAsync,
            TryOpenTcpConnectionAsync,
            tunnelService.RequestServerPathValidation,
            WriteQualitySnapshot);
        _isGameSessionActive = isGameSessionActive ?? throw new ArgumentNullException(nameof(isGameSessionActive));
        _getBindIp = getBindIp ?? throw new ArgumentNullException(nameof(getBindIp));
        _getSigningSecret = getSigningSecret ?? throw new ArgumentNullException(nameof(getSigningSecret));
        _getLauncherBaseDirectory = getLauncherBaseDirectory ?? throw new ArgumentNullException(nameof(getLauncherBaseDirectory));
        _getUsername = getUsername ?? throw new ArgumentNullException(nameof(getUsername));
        _getPeers = getPeers ?? throw new ArgumentNullException(nameof(getPeers));
        _getLastServerLatency = getLastServerLatency ?? throw new ArgumentNullException(nameof(getLastServerLatency));
        _launcherVersion = launcherVersion ?? "";
        _getServerVirtualIp = getServerVirtualIp ?? (() => PublicTunnelConfig.ServerVirtualIp);
    }

    internal GameNetworkMonitorService(
        GameNetworkMonitorDependencies dependencies,
        Func<bool> isGameSessionActive,
        Func<string> getBindIp,
        Func<string> getSigningSecret,
        Func<string> getLauncherBaseDirectory,
        Func<string> getUsername,
        Func<IReadOnlyList<PeerInfo>> getPeers,
        Func<long?> getLastServerLatency,
        string launcherVersion,
        Func<string>? getServerVirtualIp = null)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _isGameSessionActive = isGameSessionActive ?? throw new ArgumentNullException(nameof(isGameSessionActive));
        _getBindIp = getBindIp ?? throw new ArgumentNullException(nameof(getBindIp));
        _getSigningSecret = getSigningSecret ?? throw new ArgumentNullException(nameof(getSigningSecret));
        _getLauncherBaseDirectory = getLauncherBaseDirectory ?? throw new ArgumentNullException(nameof(getLauncherBaseDirectory));
        _getUsername = getUsername ?? throw new ArgumentNullException(nameof(getUsername));
        _getPeers = getPeers ?? throw new ArgumentNullException(nameof(getPeers));
        _getLastServerLatency = getLastServerLatency ?? throw new ArgumentNullException(nameof(getLastServerLatency));
        _launcherVersion = launcherVersion ?? "";
        _getServerVirtualIp = getServerVirtualIp ?? (() => PublicTunnelConfig.ServerVirtualIp);
    }

    public event EventHandler<GameNetworkObservation>? ObservationChanged;

    public GameNetworkObservation Current
    {
        get
        {
            lock (_publishSync)
                return _current;
        }
    }

    internal Task? CurrentLoopTask
    {
        get
        {
            lock (_lifecycleSync)
                return _monitorTask;
        }
    }

    public void Start()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts != null)
                return;
        }

        // MainWindow supplies these delegates.  Capture their immutable values
        // on the caller (WPF) thread before Task.Run so the monitor never reads
        // a DispatcherObject from its background loop.
        string monitorUsername = _getUsername();
        IReadOnlyList<PeerInfo> monitorPeers = _getPeers().ToArray();
        CancellationTokenSource cts;
        long generation;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts != null)
                return;
            generation = ++_generation;
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        ResetQuality();
        ResetServerPathQuality();
        Publish(generation, Empty(active: true));
        lock (_lifecycleSync)
        {
            if (generation != _generation || !ReferenceEquals(_cts, cts))
                return;
            _monitorTask = Task.Run(
                () => MonitorAsync(generation, cts, monitorUsername, monitorPeers),
                CancellationToken.None);
        }
    }

    public void Stop()
        => StopCore(markDisposed: false);

    public void Dispose()
        => StopCore(markDisposed: true);

    private void StopCore(bool markDisposed)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lifecycleSync)
        {
            if (markDisposed)
                _disposed = true;
            _generation++;
            cts = _cts;
            task = _monitorTask;
            _cts = null;
            _monitorTask = null;
        }

        try { cts?.Cancel(); } catch { }
        if (cts != null)
            _ = DisposeAfterCompletionAsync(task, cts);
        ResetQuality();
        ResetServerPathQuality();
        PublishStopped();
    }

    private static async Task DisposeAfterCompletionAsync(Task? task, CancellationTokenSource cts)
    {
        try
        {
            if (task != null)
                await task.ConfigureAwait(false);
        }
        catch
        {
            // MonitorAsync contains its own failure boundary; disposal must remain best effort.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task MonitorAsync(
        long generation,
        CancellationTokenSource cts,
        string monitorUsername,
        IReadOnlyList<PeerInfo> monitorPeers)
    {
        CancellationToken token = cts.Token;
        int missingCycles = 0;
        DateTime lastPathQueryUtc = DateTime.MinValue;
        string cachedPeerIp = "";
        EasyTierPeerPath? cachedPath = null;
        try
        {
            while (!token.IsCancellationRequested && IsCurrentGeneration(generation, cts))
            {
                try
                {
                    if (!_isGameSessionActive())
                        break;

                    string bindIp = _getBindIp();
                    (bool Ok, long? LatencyMs) serverProbe = (false, null);
                    if (PublicTunnelConfig.IsScblClientIp(bindIp))
                    {
                        serverProbe = await _dependencies.ProbeServerAsync(
                            _getServerVirtualIp(),
                            50051,
                            TimeSpan.FromMilliseconds(700),
                            bindIp).ConfigureAwait(false);
                        if (!IsCurrentGeneration(generation, cts))
                            break;
                        RecordServerPathSample(serverProbe.Ok);
                    }

                    ControlPlaneGameSession? authoritative = null;
                    if (PublicTunnelConfig.IsScblClientIp(bindIp))
                    {
                        authoritative = await _dependencies.GetGameSessionAsync(
                            bindIp,
                            _getSigningSecret(),
                            token).ConfigureAwait(false);
                        if (!IsCurrentGeneration(generation, cts))
                            break;
                    }

                    bool authorityActive = authoritative?.Authoritative == true && authoritative.Active;
                    bool localHost = authorityActive && authoritative!.RequesterIsHost;
                    string hostIp = authorityActive ? authoritative!.HostVirtualIp.Trim() : "";
                    string hostUsername = authorityActive ? authoritative!.HostUsername.Trim() : "";
                    int activePeers = authorityActive ? authoritative!.ParticipantCount : 0;
                    string roleSource = authorityActive ? "game-server" : "";

                    if (localHost)
                    {
                        missingCycles = 0;
                        string targetIp = _getServerVirtualIp();
                        EnsureQualityHost(targetIp);
                        cachedPath = await RefreshPathIfNeededAsync(
                            targetIp,
                            cachedPeerIp,
                            cachedPath,
                            lastPathQueryUtc).ConfigureAwait(false);
                        if (!IsCurrentGeneration(generation, cts))
                            break;
                        if (!targetIp.Equals(cachedPeerIp, StringComparison.OrdinalIgnoreCase)
                            || (DateTime.UtcNow - lastPathQueryUtc).TotalSeconds >= 3)
                        {
                            cachedPeerIp = targetIp;
                            lastPathQueryUtc = DateTime.UtcNow;
                        }
                        (bool ok, long? latency) = serverProbe;
                        RecordSample(ok, latency);
                        Publish(generation, CreateObservation(
                            localIsHost: true,
                            peerIp: "",
                            latency ?? cachedPath?.LatencyMs ?? _getLastServerLatency(),
                            cachedPath,
                            activePeers,
                            roleSource,
                            hostUsername,
                            authoritative!.SessionId));
                    }
                    else if (PublicTunnelConfig.IsScblClientIp(hostIp))
                    {
                        missingCycles = 0;
                        EnsureQualityHost(hostIp);
                        cachedPath = await RefreshPathIfNeededAsync(
                            hostIp,
                            cachedPeerIp,
                            cachedPath,
                            lastPathQueryUtc).ConfigureAwait(false);
                        if (!IsCurrentGeneration(generation, cts))
                            break;
                        if (!hostIp.Equals(cachedPeerIp, StringComparison.OrdinalIgnoreCase)
                            || (DateTime.UtcNow - lastPathQueryUtc).TotalSeconds >= 3)
                        {
                            cachedPeerIp = hostIp;
                            lastPathQueryUtc = DateTime.UtcNow;
                        }
                        _dependencies.StartPeerProbe(monitorUsername, bindIp, _launcherVersion);
                        (bool ok, long? latency) = await _dependencies.ProbePeerLatencyAsync(
                            hostIp,
                            TimeSpan.FromMilliseconds(700),
                            token).ConfigureAwait(false);
                        if (!IsCurrentGeneration(generation, cts))
                            break;
                        RecordSample(ok, latency);
                        long? registryLatency = monitorPeers
                            .FirstOrDefault(peer => peer.VirtualIp.Equals(hostIp, StringComparison.OrdinalIgnoreCase))
                            ?.LatencyMs;
                        Publish(generation, CreateObservation(
                            localIsHost: false,
                            hostIp,
                            latency ?? cachedPath?.LatencyMs ?? registryLatency,
                            cachedPath,
                            activePeers,
                            roleSource,
                            hostUsername,
                            authoritative?.SessionId));
                    }
                    else if (activePeers > 0)
                    {
                        missingCycles = 0;
                        Publish(generation, CreateObservation(
                            false, "", null, null, activePeers, roleSource, hostUsername, authoritative?.SessionId));
                    }
                    else if (++missingCycles >= 2)
                    {
                        Publish(generation, CreateObservation(
                            false, "", null, null, 0, authorityActive ? "game-server" : "", "", null));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LogService.Info("Game path monitor skipped one cycle: " + ex.Message);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            bool completedCurrentGeneration;
            lock (_lifecycleSync)
            {
                completedCurrentGeneration = generation == _generation && ReferenceEquals(_cts, cts);
                if (completedCurrentGeneration)
                {
                    _cts = null;
                    _monitorTask = null;
                }
            }
            if (completedCurrentGeneration)
            {
                ResetQuality();
                Publish(generation, Empty(active: false));
                cts.Dispose();
            }
        }
    }

    private async Task<EasyTierPeerPath?> RefreshPathIfNeededAsync(
        string peerIp,
        string cachedPeerIp,
        EasyTierPeerPath? cachedPath,
        DateTime lastQueryUtc)
    {
        if (peerIp.Equals(cachedPeerIp, StringComparison.OrdinalIgnoreCase)
            && cachedPath != null
            && (DateTime.UtcNow - lastQueryUtc).TotalSeconds < 3)
        {
            return cachedPath;
        }
        return await _dependencies.DetectPeerPathAsync(
            _getLauncherBaseDirectory(),
            peerIp,
            TimeSpan.FromMilliseconds(850)).ConfigureAwait(false);
    }

    private GameNetworkObservation CreateObservation(
        bool localIsHost,
        string peerIp,
        long? latency,
        EasyTierPeerPath? path,
        int activePeers,
        string roleSource,
        string hostUsername,
        long? sessionId)
        => new(
            Active: true,
            localIsHost,
            peerIp,
            latency,
            path?.TransportMode ?? "",
            path?.UnderlayAddressFamily ?? "",
            path?.NextHop ?? "",
            path?.HopCount,
            activePeers,
            roleSource,
            hostUsername,
            sessionId,
            _p50,
            _p95,
            _jitter,
            _loss);

    private bool IsCurrentGeneration(long generation, CancellationTokenSource? cts = null)
    {
        lock (_lifecycleSync)
        {
            return !_disposed
                && generation == _generation
                && (cts == null || ReferenceEquals(_cts, cts));
        }
    }

    private void Publish(long generation, GameNetworkObservation observation)
    {
        lock (_publishSync)
        {
            if (!IsCurrentGeneration(generation))
                return;
            GameNetworkObservation previous = _current;
            _current = observation;
            _dependencies.WriteQualitySnapshot(observation);
            if (!Equals(previous, observation))
                ObservationChanged?.Invoke(this, observation);
        }
    }

    private void PublishStopped()
    {
        lock (_publishSync)
        {
            GameNetworkObservation observation = Empty(active: false);
            GameNetworkObservation previous = _current;
            _current = observation;
            _dependencies.WriteQualitySnapshot(observation);
            if (!Equals(previous, observation))
                ObservationChanged?.Invoke(this, observation);
        }
    }

    private void EnsureQualityHost(string hostIp)
    {
        if (!_qualityHostIp.Equals(hostIp, StringComparison.OrdinalIgnoreCase))
            ResetQuality(hostIp);
    }

    private void ResetQuality(string hostIp = "")
    {
        lock (_qualitySync)
        {
            _qualitySamples.Clear();
            _qualityHostIp = hostIp;
            _p50 = null;
            _p95 = null;
            _jitter = null;
            _loss = null;
        }
    }

    private void ResetServerPathQuality()
    {
        lock (_qualitySync)
        {
            _serverPathSamples.Clear();
            _serverPathDegradationSignaled = false;
        }
    }

    private void RecordServerPathSample(bool success)
    {
        string? validationReason = null;
        lock (_qualitySync)
        {
            _serverPathSamples.Enqueue(success);
            while (_serverPathSamples.Count > 5)
                _serverPathSamples.Dequeue();

            int failedSamples = _serverPathSamples.Count(value => !value);
            if (!_serverPathDegradationSignaled
                && ShouldRequestServerPathValidation(
                    _serverPathSamples.Count,
                    failedSamples,
                    latestSucceeded: success))
            {
                _serverPathDegradationSignaled = true;
                double lossPercent = 100d * failedSamples / _serverPathSamples.Count;
                validationReason =
                    $"Server-path loss reached {lossPercent:0}% "
                    + $"({failedSamples}/{_serverPathSamples.Count} recent samples).";
            }
            else if (success && failedSamples == 0)
            {
                _serverPathDegradationSignaled = false;
            }
        }

        if (validationReason != null)
            _dependencies.RequestServerPathValidation(validationReason);
    }

    internal static bool ShouldRequestServerPathValidation(
        int sampleCount,
        int failedSamples,
        bool latestSucceeded)
        => sampleCount >= 5
            && !latestSucceeded
            && failedSamples * 100 >= sampleCount * 40;

    private void RecordSample(bool success, long? latencyMs)
    {
        lock (_qualitySync)
        {
            DateTime now = DateTime.UtcNow;
            _qualitySamples.Enqueue(new QualitySample(now, success && latencyMs.HasValue, latencyMs));
            while (_qualitySamples.Count > 0
                   && now - _qualitySamples.Peek().AtUtc > TimeSpan.FromSeconds(30))
            {
                _qualitySamples.Dequeue();
            }

            QualitySample[] samples = _qualitySamples.ToArray();
            long[] values = samples
                .Where(sample => sample.Success && sample.LatencyMs.HasValue)
                .Select(sample => sample.LatencyMs!.Value)
                .OrderBy(value => value)
                .ToArray();
            _loss = samples.Length == 0 ? null : 100d * samples.Count(sample => !sample.Success) / samples.Length;
            _p50 = Percentile(values, 0.50);
            _p95 = Percentile(values, 0.95);
            long[] chronological = samples
                .Where(sample => sample.Success && sample.LatencyMs.HasValue)
                .Select(sample => sample.LatencyMs!.Value)
                .ToArray();
            _jitter = chronological.Length >= 2
                ? (long)Math.Round(chronological.Zip(
                    chronological.Skip(1),
                    (left, right) => Math.Abs(right - left)).Average())
                : chronological.Length == 1 ? 0 : null;
        }
    }

    internal static long? Percentile(long[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return null;
        int index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private void WriteQualitySnapshot(GameNetworkObservation observation)
    {
        try
        {
            string path = Path.Combine(LogService.PersistentDataDirectory, "runtime", "game-network-quality.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int sampleCount;
            lock (_qualitySync) sampleCount = _qualitySamples.Count;
            var status = new GameNetworkQualityStatus
            {
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Source = observation.RoleSource,
                AuthoritativeSession = observation.RoleSource.Equals("game-server", StringComparison.OrdinalIgnoreCase),
                SessionId = observation.SessionId,
                HostUsername = observation.HostUsername,
                HostVirtualIp = observation.LocalIsHost ? _getBindIp() : observation.PeerIp,
                LocalIsHost = observation.LocalIsHost,
                ParticipantCount = observation.ActivePeerCount,
                CurrentLatencyMs = observation.LatencyMs,
                LatencyP50Ms = observation.LatencyP50Ms,
                LatencyP95Ms = observation.LatencyP95Ms,
                JitterMs = observation.JitterMs,
                LossPercent = observation.LossPercent,
                SampleCount = sampleCount,
                Transport = observation.Transport,
                AddressFamily = observation.AddressFamily,
                NextHop = observation.NextHop,
                HopCount = observation.HopCount
            };
            string json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            string temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(temporary, path, overwrite: true); break; }
                    catch (Exception ex) when (
                        (ex is IOException || ex is UnauthorizedAccessException) && attempt < 4)
                    {
                        Thread.Sleep(attempt * 12);
                    }
                }
            }
            finally { try { File.Delete(temporary); } catch { } }
        }
        catch (Exception ex)
        {
            LogService.Info("Game quality snapshot skipped: " + ex.Message);
        }
    }

    private static async Task<(bool Ok, long? LatencyMs)> TryOpenTcpConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        string bindIp)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        if (IPAddress.TryParse(bindIp, out IPAddress? localAddress)
            && localAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            client.Client.Bind(new IPEndPoint(localAddress, 0));
        }
        DateTime started = DateTime.UtcNow;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return (true, Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds));
        }
        catch { return (false, null); }
    }

    private static GameNetworkObservation Empty(bool active)
        => new(active, false, "", null, "", "", "", null, 0, "", "", null, null, null, null, null);
}
