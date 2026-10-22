using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

public sealed record EasyTierPeerPath(
    string PeerIp,
    long? LatencyMs,
    string TransportMode,
    string Cost,
    string NextHop = "",
    int? HopCount = null,
    string UnderlayAddressFamily = "",
    string RemoteAddress = "");

public sealed record EasyTierServerPathObservation(
    string TransportMode,
    string UnderlayAddressFamily,
    long RuntimeObservationId);

public sealed record EasyTierBroadcastRelayStatus(
    bool Enabled,
    bool Confirmed,
    bool ExplicitFailure,
    bool Degraded,
    string Message);

public sealed record EasyTierClientOptions(
    string InstanceId,
    string NetworkName,
    bool LatencyFirst = false,
    bool EnableP2P = true,
    bool StableRelayMode = false,
    bool EnableUdpBroadcastRelay = true,
    bool ForceGameVirtualAdapter = true,
    int WssPort = PublicTunnelConfig.DefaultWssPort,
    int QuicPort = PublicTunnelConfig.DefaultQuicPort,
    ClientNetworkMode NetworkMode = ClientNetworkMode.Fast);

/// <summary>
/// Coordinates the SCBL EasyTier lifecycle while preserving the historical public facade.
/// Configuration, CLI parsing/execution, process ownership, and adapter discovery live in
/// dedicated internal boundaries so callers keep one stable network service contract.
/// </summary>
public sealed class PublicTunnelService
{
    private readonly object _stateSync = new();
    private readonly EasyTierConfigWriter _configWriter;
    private readonly EasyTierProfileStore _profileStore;
    private readonly EasyTierCliClient _cliClient;
    private readonly EasyTierProcessSupervisor _processSupervisor;
    private readonly OverlayAddressResolver _addressResolver;
    private readonly Func<bool> _ownedProcessProbe;
    private readonly EasyTierRuntimePresenceTracker _runtimePresenceTracker;
    private readonly Func<TimeSpan, TimeSpan> _ipv6PreferredWindow;
    private readonly Func<TimeSpan, Task> _rpcPortalRelease;
    private readonly Func<string> _getServerVirtualIp;
    private string _lastAssignedIp = string.Empty;
    private string _runningConfigPath = string.Empty;
    private long _runtimeObservationId = 1;
    private DateTime _lastUdpRetrySummaryUtc = DateTime.MinValue;
    private int _suppressedUdpRetryLines;
    private bool _udpBroadcastRelayExpected;
    private bool _udpBroadcastRelayExplicitFailure;
    private bool _udpBroadcastRelayDegraded;
    private bool _udpBroadcastRelayConfirmed;
    private string _udpBroadcastRelayMessage = string.Empty;
    private bool _runtimeUsesIpv4Fallback;
    private bool _ipv6DisabledForLauncherSession;

    internal event Action<string>? ServerPathValidationRequested;

    public PublicTunnelService(Func<string>? getServerVirtualIp = null)
        : this(
            new EasyTierConfigWriter(),
            new EasyTierProfileStore(),
            new EasyTierCliClient(),
            new EasyTierProcessSupervisor(),
            null,
            getServerVirtualIp: getServerVirtualIp)
    {
    }

    internal PublicTunnelService(
        EasyTierConfigWriter configWriter,
        EasyTierProfileStore profileStore,
        EasyTierCliClient cliClient,
        EasyTierProcessSupervisor processSupervisor,
        IEasyTierNetworkSnapshotProvider? networkSnapshots,
        Func<bool>? ownedProcessProbe = null,
        EasyTierRuntimePresenceTracker? runtimePresenceTracker = null,
        Func<TimeSpan, TimeSpan>? ipv6PreferredWindow = null,
        Func<TimeSpan, Task>? rpcPortalRelease = null,
        Func<string>? getServerVirtualIp = null)
    {
        _configWriter = configWriter;
        _profileStore = profileStore;
        _cliClient = cliClient;
        _processSupervisor = processSupervisor;
        _addressResolver = new OverlayAddressResolver(cliClient, networkSnapshots);
        _ownedProcessProbe = ownedProcessProbe ?? EasyTierProcessSupervisor.HasOwnedProcess;
        _runtimePresenceTracker = runtimePresenceTracker ?? new EasyTierRuntimePresenceTracker();
        _ipv6PreferredWindow = ipv6PreferredWindow ?? GetIpv6PreferredWindow;
        _rpcPortalRelease = rpcPortalRelease
            ?? EasyTierProcessSupervisor.EnsureRpcPortalReleasedAsync;
        _getServerVirtualIp = getServerVirtualIp ?? (() => PublicTunnelConfig.ServerVirtualIp);
    }

    public bool IsRunning => _processSupervisor.IsRunning;

    public string GetTunnelExePath(string launcherBaseDir)
        => EasyTierProcessSupervisor.GetCorePath(launcherBaseDir);

    public string GetCliExePath(string launcherBaseDir)
        => EasyTierCliClient.GetCliPath(launcherBaseDir);

    public bool HasRunningTunnelClientProcess()
        => IsRunning || HasDefinitiveRunningTunnelClientProcess();

    public bool HasContinuousRuntimePresence()
        => _runtimePresenceTracker.Observe(HasRunningTunnelClientProcess());

    internal bool HasDefinitiveRunningTunnelClientProcess()
        => _ownedProcessProbe();

    public async Task<string> EnsureStartedAsync(
        string launcherBaseDir,
        TimeSpan timeout,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        bool forceRuntimeRestart = false)
    {
        using FileStream startupLock = await EasyTierProcessSupervisor
            .AcquireStartupLockAsync(timeout)
            .ConfigureAwait(false);

        if (!forceRuntimeRestart
            && (IsRunning || HasDefinitiveRunningTunnelClientProcess())
            && (!Ipv6DisabledForLauncherSession || RuntimeUsesIpv4Fallback))
        {
            string existing = await _addressResolver.ResolveAsync(
                launcherBaseDir,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (NetworkHealthCheckService.IsValidScblClientIp(existing))
            {
                SetLastAssignedIp(existing);
                _runtimePresenceTracker.Observe(runtimePresent: true);
                LogService.Info(
                    $"Reusing EasyTier runtime found through the fixed RPC portal. Assigned IP={existing}");
                return existing;
            }
        }

        Stop("restart EasyTier runtime");
        StopAllTunnelClients("restart EasyTier runtime");
        await EasyTierProcessSupervisor.EnsureRpcPortalReleasedAsync(
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return await StartAsync(
            launcherBaseDir,
            timeout,
            publicEndpoint,
            tunnelSecret,
            options).ConfigureAwait(false);
    }

    internal async Task<string?> RestartWithIpv4FallbackAsync(
        string launcherBaseDir,
        TimeSpan timeout,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        EasyTierServerPeerPlan peerPlan = _configWriter.BuildServerPeerPlan(
            publicEndpoint,
            options);
        if (RuntimeUsesIpv4Fallback)
            return null;

        DisableIpv6ForLauncherSession();

        using FileStream startupLock = await EasyTierProcessSupervisor
            .AcquireStartupLockAsync(timeout)
            .ConfigureAwait(false);
        if (RuntimeUsesIpv4Fallback)
            return null;

        if (!peerPlan.HasIpv4Fallback)
        {
            Stop("disable failed IPv6 EasyTier server data path");
            StopAllTunnelClients("disable failed IPv6 EasyTier server data path");
            throw new InvalidOperationException(
                "The IPv6 server path failed and no explicit IPv4 fallback address is available for this Launcher session.");
        }

        LogService.Warning(
            "The IPv6-preferred EasyTier server data path did not remain usable; "
            + "disabling IPv6 for this Launcher session and restarting the selected transport with explicit IPv4.");
        Stop("switch failed EasyTier server data path to IPv4 fallback");
        StopAllTunnelClients("switch failed EasyTier server data path to IPv4 fallback");
        await _rpcPortalRelease(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return await StartAsync(
            launcherBaseDir,
            timeout,
            publicEndpoint,
            tunnelSecret,
            options,
            forceIpv4Fallback: true).ConfigureAwait(false);
    }

    internal async Task<string> StartAsync(
        string launcherBaseDir,
        TimeSpan timeout,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        bool forceIpv4Fallback = false)
    {
        string coreExecutable = GetTunnelExePath(launcherBaseDir);
        string cliExecutable = GetCliExePath(launcherBaseDir);
        string packetDll = ClientInstallLayout.Resolve(
            launcherBaseDir,
            ClientInstallLayout.PacketDllRelativePath);
        string wintunDll = ClientInstallLayout.Resolve(
            launcherBaseDir,
            ClientInstallLayout.WintunDllRelativePath);
        if (!File.Exists(coreExecutable))
        {
            throw new FileNotFoundException(
                "未找到 easytier-core.exe。请先运行 client\\easytier\\download_easytier_windows.ps1 或重新执行完整构建。",
                coreExecutable);
        }
        if (!File.Exists(cliExecutable))
        {
            throw new FileNotFoundException(
                "未找到 easytier-cli.exe。请确认 EasyTier 官方核心包已完整复制到 publish-single\\tools。",
                cliExecutable);
        }
        if (!File.Exists(packetDll))
            throw new FileNotFoundException("EasyTier 运行依赖 Packet.dll 缺失，请先更新客户端组件。", packetDll);
        if (!File.Exists(wintunDll))
            throw new FileNotFoundException("EasyTier 运行依赖 wintun.dll 缺失，请先更新客户端组件。", wintunDll);

        Directory.CreateDirectory(LogService.LogDirectory);
        Directory.CreateDirectory(LogService.NetworkDirectory);
        string configPath = Path.Combine(
            LogService.NetworkDirectory,
            "scbl-easytier-client.toml");
        EasyTierServerPeerPlan peerPlan = _configWriter.BuildServerPeerPlan(
            publicEndpoint,
            options);
        if (forceIpv4Fallback)
            DisableIpv6ForLauncherSession();
        bool requireIpv4ForSession = forceIpv4Fallback || Ipv6DisabledForLauncherSession;
        if (requireIpv4ForSession && !peerPlan.HasIpv4Fallback)
        {
            throw new InvalidOperationException(
                "IPv6 is disabled for this Launcher session, but no explicit IPv4 server address is available.");
        }
        IReadOnlyList<string> activePeerUris = requireIpv4ForSession
            ? peerPlan.Ipv4FallbackPeerUris
            : peerPlan.PreferredPeerUris;
        bool usingIpv4Fallback = requireIpv4ForSession;
        _configWriter.WriteClientConfig(
            configPath,
            publicEndpoint,
            tunnelSecret,
            options,
            activePeerUris);
        ResetForStart(configPath, options.EnableUdpBroadcastRelay);
        SetRuntimeUsesIpv4Fallback(usingIpv4Fallback);

        string ingress = options.NetworkMode == ClientNetworkMode.Compatible
            ? $"wss:{options.WssPort}"
            : $"quic:{options.QuicPort}";
        LogService.Info(
            $"Starting EasyTier. endpoint={PublicTunnelConfig.NormalizePublicEndpoint(publicEndpoint)}, "
            + $"network={options.NetworkName}, networkMode={options.NetworkMode}, "
            + $"p2p={options.EnableP2P}, serverIngress={ingress}, "
            + "tcpHolePunching=false, clientDataRelay=false, protocolFallback=false, "
            + $"latencyFirst={options.LatencyFirst}, "
            + $"underlayIpv6={activePeerUris.Any(EasyTierConfigWriter.ContainsIpv6LiteralHost)}");
        _processSupervisor.Start(coreExecutable, configPath, HandleProcessLine);

        DateTime deadline = DateTime.UtcNow + timeout;
        bool ipv4FallbackPending = peerPlan.PrefersIpv6
            && peerPlan.HasIpv4Fallback
            && !usingIpv4Fallback;
        DateTime ipv6PreferredDeadline = ipv4FallbackPending
            ? DateTime.UtcNow + _ipv6PreferredWindow(timeout)
            : DateTime.MaxValue;
        Exception? lastCliError = null;
        string stableCandidate = string.Empty;
        int stableSamples = 0;
        DateTime stableSinceUtc = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            bool assignedIpStable = false;
            if (_processSupervisor.TryGetExitCode(out int exitCode))
            {
                throw new Exception(
                    $"EasyTier 网络核心已退出，exit={exitCode}。" + ReadTunnelLogTail());
            }

            try
            {
                string address = await _addressResolver.ResolveAsync(
                    launcherBaseDir,
                    TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
                if (NetworkHealthCheckService.IsValidScblClientIp(address))
                {
                    if (address.Equals(stableCandidate, StringComparison.OrdinalIgnoreCase))
                    {
                        stableSamples++;
                    }
                    else
                    {
                        stableCandidate = address;
                        stableSamples = 1;
                        stableSinceUtc = DateTime.UtcNow;
                    }

                    SetLastAssignedIp(address);
                    assignedIpStable = stableSamples >= 3
                        && (DateTime.UtcNow - stableSinceUtc).TotalMilliseconds >= 500;
                }
                else
                {
                    stableCandidate = string.Empty;
                    stableSamples = 0;
                    stableSinceUtc = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                lastCliError = ex;
                stableCandidate = string.Empty;
                stableSamples = 0;
                stableSinceUtc = DateTime.MinValue;
            }

            if (ipv4FallbackPending && DateTime.UtcNow >= ipv6PreferredDeadline)
            {
                EasyTierServerPathObservation? observation = null;
                try
                {
                    observation = await DetectServerPathObservationAsync(
                        launcherBaseDir,
                        TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastCliError = ex;
                }

                string expectedTransport = options.NetworkMode == ClientNetworkMode.Compatible
                    ? "WSS"
                    : "QUIC";
                if (observation != null
                    && observation.TransportMode.Equals(
                        expectedTransport,
                        StringComparison.OrdinalIgnoreCase)
                    && observation.UnderlayAddressFamily.Equals(
                        "IPv6",
                        StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Info(
                        $"EasyTier IPv6-preferred {expectedTransport} ingress connected; "
                        + "retaining the single IPv6 server connection.");
                    ipv4FallbackPending = false;
                }
                else
                {
                    LogService.Warning(
                        $"EasyTier IPv6-preferred {expectedTransport} ingress did not converge; "
                        + "disabling IPv6 for this Launcher session and restarting the same transport with explicit IPv4. "
                        + "Cross-protocol fallback remains disabled.");
                    DisableIpv6ForLauncherSession();
                    Stop("switch EasyTier server ingress from IPv6 preference to IPv4 fallback");
                    await _rpcPortalRelease(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    activePeerUris = peerPlan.Ipv4FallbackPeerUris;
                    _configWriter.WriteClientConfig(
                        configPath,
                        publicEndpoint,
                        tunnelSecret,
                        options,
                        activePeerUris);
                    ResetForStart(configPath, options.EnableUdpBroadcastRelay);
                    _processSupervisor.Start(coreExecutable, configPath, HandleProcessLine);
                    SetRuntimeUsesIpv4Fallback(value: true);
                    ipv4FallbackPending = false;
                    lastCliError = null;
                    stableCandidate = string.Empty;
                    stableSamples = 0;
                    stableSinceUtc = DateTime.MinValue;
                    continue;
                }
            }

            if (assignedIpStable && !ipv4FallbackPending)
            {
                TryWriteRuntimeProfile(
                    launcherBaseDir,
                    publicEndpoint,
                    tunnelSecret,
                    options,
                    stableCandidate,
                    activePeerUris);
                LogService.Info(
                    $"EasyTier virtual network established. Assigned IP={stableCandidate}, "
                    + $"addressing=dhcp, stableSamples={stableSamples}, "
                    + $"serverIngress={(RuntimeUsesIpv4Fallback
                        ? "ipv4-fallback"
                        : activePeerUris.Any(EasyTierConfigWriter.ContainsIpv6LiteralHost)
                            ? "ipv6-preferred"
                            : "ipv4-only")}");
                return stableCandidate;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string detail = lastCliError == null ? string.Empty : " CLI: " + lastCliError.Message;
        throw new TimeoutException(
            "EasyTier 启动超时，未获取到 10.66.0.x 虚拟 IP。"
            + detail
            + ReadTunnelLogTail());
    }

    internal static TimeSpan GetIpv6PreferredWindow(TimeSpan timeout)
    {
        double milliseconds = Math.Clamp(
            timeout.TotalMilliseconds / 3d,
            3_000d,
            6_000d);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, timeout.TotalMilliseconds));
    }

    internal bool RuntimeUsesIpv4Fallback
    {
        get
        {
            lock (_stateSync)
                return _runtimeUsesIpv4Fallback;
        }
    }

    internal bool Ipv6DisabledForLauncherSession
    {
        get
        {
            lock (_stateSync)
                return _ipv6DisabledForLauncherSession;
        }
    }

    private void DisableIpv6ForLauncherSession()
    {
        lock (_stateSync)
            _ipv6DisabledForLauncherSession = true;
    }

    internal void ConfirmInPlaceIpv6ToIpv4FallbackSucceeded()
    {
        lock (_stateSync)
        {
            _ipv6DisabledForLauncherSession = true;
            _runtimeUsesIpv4Fallback = true;
        }
        LogService.Warning(
            "The existing EasyTier runtime recovered from IPv6 to IPv4; "
            + "IPv6 remains disabled only for the current Launcher session.");
    }

    private void SetRuntimeUsesIpv4Fallback(bool value)
    {
        lock (_stateSync)
            _runtimeUsesIpv4Fallback = value;
    }

    public bool CanReuseRuntimeProfile(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options)
    {
        try
        {
            if (!HasDefinitiveRunningTunnelClientProcess())
                return false;

            EasyTierServerPeerPlan peerPlan = _configWriter.BuildServerPeerPlan(
                publicEndpoint,
                options);
            bool requireIpv4Fallback = Ipv6DisabledForLauncherSession;
            IReadOnlyList<string> expectedPeerUris = requireIpv4Fallback
                ? peerPlan.Ipv4FallbackPeerUris
                : peerPlan.PreferredPeerUris;
            bool matches = _profileStore.Matches(
                launcherBaseDir,
                publicEndpoint,
                tunnelSecret,
                options,
                requireIpv4Fallback,
                expectedPeerUris);
            if (matches)
                _runtimePresenceTracker.Observe(runtimePresent: true);
            return matches;
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier runtime profile validation skipped: " + ex.Message);
            return false;
        }
    }

    private void TryWriteRuntimeProfile(
        string launcherBaseDir,
        string publicEndpoint,
        string tunnelSecret,
        EasyTierClientOptions options,
        string assignedIp,
        IReadOnlyList<string> activePeerUris)
    {
        try
        {
            _profileStore.Write(
                launcherBaseDir,
                publicEndpoint,
                tunnelSecret,
                options,
                assignedIp,
                RuntimeUsesIpv4Fallback,
                activePeerUris);
        }
        catch (Exception ex)
        {
            LogService.Warning("EasyTier runtime profile marker write skipped: " + ex.Message);
        }
    }

    private void HandleProcessLine(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string safeLine = LogService.SanitizeSensitiveText(line);
        ObserveUdpBroadcastRelayLine(safeLine);
        if (IsServerPathFailureSignal(safeLine))
        {
            RequestServerPathValidation(
                "EasyTier reported a server-path failure: " + safeLine);
        }
        else if (IsServerPathRecoverySignal(safeLine))
        {
            RequestServerPathValidation(
                "EasyTier reported a successful reconnect: " + safeLine);
        }
        if (IsBenignEasyTierNoise(safeLine))
            return;

        if (IsUdpRetryNoise(safeLine))
        {
            int suppressed = 0;
            lock (_stateSync)
            {
                _suppressedUdpRetryLines++;
                DateTime now = DateTime.UtcNow;
                if (_lastUdpRetrySummaryUtc == DateTime.MinValue
                    || (now - _lastUdpRetrySummaryUtc).TotalSeconds >= 60)
                {
                    suppressed = _suppressedUdpRetryLines;
                    _suppressedUdpRetryLines = 0;
                    _lastUdpRetrySummaryUtc = now;
                }
            }
            if (suppressed > 0)
            {
                LogService.ComponentWarning(
                    "EasyTier",
                    "EasyTier selected transport has not connected yet and continues retrying without protocol fallback. "
                    + $"Suppressed repeated lines={suppressed}.");
            }
            return;
        }

        LogService.ComponentProcessLine("EasyTier", safeLine, isError);
        string? address = OverlayAddressResolver.ExtractScblIp(safeLine);
        if (NetworkHealthCheckService.IsValidScblClientIp(address))
            SetLastAssignedIp(address!);
    }

    private static bool IsBenignEasyTierNoise(string line)
        => line.Contains("peer rpc transport read aborted", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("bind addr fail", StringComparison.OrdinalIgnoreCase)
                && line.Contains("169.254.", StringComparison.OrdinalIgnoreCase))
            || line.Contains("Retained buckets:", StringComparison.OrdinalIgnoreCase);

    private static bool IsUdpRetryNoise(string line)
        => line.Contains("udp connect:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("udp send syn", StringComparison.OrdinalIgnoreCase)
            || line.Contains("connect timeout after", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ConnectError(\"udp://", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("connect to peer error", StringComparison.OrdinalIgnoreCase)
                && line.Contains("udp://", StringComparison.OrdinalIgnoreCase))
            || (line.Contains("reconn_tasks done", StringComparison.OrdinalIgnoreCase)
                && line.Contains("connect timeout", StringComparison.OrdinalIgnoreCase));

    internal static bool IsServerPathFailureSignal(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line.Contains("easytier::connector::manual", StringComparison.OrdinalIgnoreCase)
            && line.Contains("reconn_tasks done", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Some(Err", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // EasyTier reports a lost data path from several subsystems.  These are
        // only wake-up signals: NetworkOrchestrator still opens fresh 50051/80
        // probes and confirms the current server route before it mutates a
        // healthy runtime.  Generic hole-punch and OSPF chatter stays excluded.
        return (line.Contains("pingpong loss too much", StringComparison.OrdinalIgnoreCase)
                && line.Contains("closing the connection", StringComparison.OrdinalIgnoreCase))
            || line.Contains("PeerNoConnectionError", StringComparison.OrdinalIgnoreCase)
            || line.Contains("RouteError(None)", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("sync_route_info failed", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Timeout", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsServerPathRecoverySignal(string line)
        => !string.IsNullOrWhiteSpace(line)
            && line.Contains("easytier::connector::manual", StringComparison.OrdinalIgnoreCase)
            && line.Contains("reconn result: Some(Ok", StringComparison.OrdinalIgnoreCase);

    internal void RequestServerPathValidation(string reason)
    {
        Action<string>? handler = ServerPathValidationRequested;
        if (handler == null)
            return;

        string safeReason = LogService.SanitizeSensitiveText(reason ?? string.Empty);
        try
        {
            handler(safeReason);
        }
        catch (Exception ex)
        {
            LogService.Info(
                "Server-path validation signal subscriber failed: " + ex.GetType().Name);
        }
    }

    private void ObserveUdpBroadcastRelayLine(string line)
    {
        lock (_stateSync)
        {
            if (!_udpBroadcastRelayExpected || string.IsNullOrWhiteSpace(line))
                return;
            if (line.Contains(
                    "UDP broadcast relay start failed",
                    StringComparison.OrdinalIgnoreCase))
            {
                _udpBroadcastRelayExplicitFailure = true;
                _udpBroadcastRelayMessage = line.Trim();
            }
            else if (line.Contains(
                         "UDP broadcast relay started",
                         StringComparison.OrdinalIgnoreCase))
            {
                _udpBroadcastRelayConfirmed = true;
                _udpBroadcastRelayMessage =
                    "EasyTier reported that UDP broadcast relay started.";
            }
            else if (line.Contains(
                         "WinDivert UDP broadcast capture unavailable",
                         StringComparison.OrdinalIgnoreCase)
                     && line.Contains("falling back", StringComparison.OrdinalIgnoreCase))
            {
                _udpBroadcastRelayDegraded = true;
                _udpBroadcastRelayMessage =
                    "EasyTier broadcast capture fell back from WinDivert to the raw-socket backend.";
            }
        }
    }

    public EasyTierBroadcastRelayStatus GetUdpBroadcastRelayStatus()
    {
        lock (_stateSync)
        {
            if (!_udpBroadcastRelayExpected)
            {
                return new EasyTierBroadcastRelayStatus(
                    false,
                    false,
                    false,
                    false,
                    "UDP broadcast relay is disabled or the runtime configuration has not been validated yet.");
            }
            return new EasyTierBroadcastRelayStatus(
                true,
                _udpBroadcastRelayConfirmed,
                _udpBroadcastRelayExplicitFailure,
                _udpBroadcastRelayDegraded,
                string.IsNullOrWhiteSpace(_udpBroadcastRelayMessage)
                    ? "UDP broadcast relay is configured; no startup failure has been observed."
                    : _udpBroadcastRelayMessage);
        }
    }

    public bool ValidateDynamicDhcpConfig(out string message)
    {
        message = string.Empty;
        try
        {
            string configPath;
            lock (_stateSync)
            {
                configPath = !string.IsNullOrWhiteSpace(_runningConfigPath)
                             && File.Exists(_runningConfigPath)
                    ? _runningConfigPath
                    : Path.Combine(
                        LogService.PersistentDataDirectory,
                        "network",
                        "scbl-easytier-client.toml");
                _runningConfigPath = configPath;
            }

            bool valid = _profileStore.ValidateDynamicDhcpConfig(
                configPath,
                out bool? broadcastConfigured,
                out message);
            if (broadcastConfigured.HasValue)
            {
                lock (_stateSync)
                {
                    _udpBroadcastRelayExpected = broadcastConfigured.Value;
                    if (broadcastConfigured.Value
                        && string.IsNullOrWhiteSpace(_udpBroadcastRelayMessage))
                    {
                        _udpBroadcastRelayMessage =
                            "UDP broadcast relay is configured; no startup failure has been observed.";
                    }
                }
            }
            return valid;
        }
        catch (Exception ex)
        {
            message = "Failed to validate EasyTier runtime configuration: " + ex.Message;
            return false;
        }
    }

    public async Task<bool> VerifyAssignedIpStableAsync(
        string launcherBaseDir,
        string expectedIp,
        TimeSpan observationWindow,
        CancellationToken cancellationToken = default)
    {
        expectedIp = (expectedIp ?? string.Empty).Trim().Split('/')[0];
        if (!NetworkHealthCheckService.IsValidScblClientIp(expectedIp))
            return false;

        DateTime deadline = DateTime.UtcNow + observationWindow;
        int matchingSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_processSupervisor.TrackedProcessHasExited
                || (!IsRunning && !HasDefinitiveRunningTunnelClientProcess()))
                return false;

            string current = await _addressResolver.ResolveAsync(
                launcherBaseDir,
                TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
            if (!current.Equals(expectedIp, StringComparison.OrdinalIgnoreCase))
                return false;

            matchingSamples++;
            if (matchingSamples >= 4)
                return true;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return matchingSamples >= 3;
    }

    internal void InvalidateRuntimeObservation()
    {
        lock (_stateSync)
            AdvanceRuntimeObservationUnsafe();
    }

    internal long CurrentRuntimeObservationId
    {
        get
        {
            lock (_stateSync)
                return _runtimeObservationId;
        }
    }

    public async Task<EasyTierPeerPath?> DetectPeerPathAsync(
        string launcherBaseDir,
        string peerIp,
        TimeSpan timeout)
    {
        peerIp = (peerIp ?? string.Empty).Trim().Split('/')[0];
        bool isServerPeer = peerIp.Equals(
            _getServerVirtualIp(),
            StringComparison.OrdinalIgnoreCase);
        if (!isServerPeer && !PublicTunnelConfig.IsScblClientIp(peerIp))
            return null;

        string cliPath = GetCliExePath(launcherBaseDir);
        if (!File.Exists(cliPath) || (!IsRunning && !HasDefinitiveRunningTunnelClientProcess()))
            return null;
        string output = await _cliClient.QueryPeersJsonAsync(cliPath, timeout)
            .ConfigureAwait(false);
        return EasyTierCliParsers.ParsePeerPath(output, peerIp);
    }

    public async Task<EasyTierServerPathObservation?> DetectServerPathObservationAsync(
        string launcherBaseDir,
        TimeSpan timeout)
    {
        long runtimeObservationId = CurrentRuntimeObservationId;
        EasyTierPeerPath? path = await DetectPeerPathAsync(
            launcherBaseDir,
            _getServerVirtualIp(),
            timeout).ConfigureAwait(false);
        return SelectServerPathObservation(
            path,
            runtimeObservationId,
            CurrentRuntimeObservationId);
    }

    internal static EasyTierServerPathObservation? SelectServerPathObservation(
        EasyTierPeerPath? detectedPath,
        long capturedRuntimeObservationId,
        long currentRuntimeObservationId)
    {
        if (detectedPath == null
            || capturedRuntimeObservationId <= 0
            || capturedRuntimeObservationId != currentRuntimeObservationId
            || string.IsNullOrWhiteSpace(detectedPath.TransportMode))
        {
            return null;
        }

        string family = detectedPath.UnderlayAddressFamily?.Trim() ?? string.Empty;
        family = family.Equals("IPv4", StringComparison.OrdinalIgnoreCase)
            ? "IPv4"
            : family.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
                ? "IPv6"
                : string.Empty;
        if (string.IsNullOrWhiteSpace(family))
            return null;

        return new EasyTierServerPathObservation(
            detectedPath.TransportMode.Trim().ToUpperInvariant(),
            family,
            capturedRuntimeObservationId);
    }

    public async Task<IReadOnlyList<string>> ListVirtualPeerIpsAsync(
        string launcherBaseDir,
        TimeSpan timeout)
    {
        string cliPath = GetCliExePath(launcherBaseDir);
        if (!File.Exists(cliPath) || (!IsRunning && !HasDefinitiveRunningTunnelClientProcess()))
            return Array.Empty<string>();
        string output = await _cliClient.QueryPeersJsonAsync(cliPath, timeout)
            .ConfigureAwait(false);
        return EasyTierCliParsers.ParseVirtualPeerIps(output);
    }

    public async Task CaptureFailureSnapshotBestEffortAsync(
        string launcherBaseDir,
        string reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string cliPath = GetCliExePath(launcherBaseDir);
            if (!File.Exists(cliPath) || (!IsRunning && !HasDefinitiveRunningTunnelClientProcess()))
                return;

            Task<string> peersTask = _cliClient.QueryPeersJsonAsync(
                cliPath,
                TimeSpan.FromMilliseconds(900));
            Task<string> routesTask = _cliClient.QueryRoutesJsonAsync(
                cliPath,
                TimeSpan.FromMilliseconds(900));
            await Task.WhenAll(peersTask, routesTask).WaitAsync(cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(LogService.RuntimeDirectory);
            string snapshot = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    capturedAt = DateTimeOffset.Now,
                    reason = LogService.SanitizeSensitiveText(reason),
                    assignedIp = ReadAssignedIp(),
                    runtimeObservationId = CurrentRuntimeObservationId,
                    peers = ParseDiagnosticJson(peersTask.Result),
                    routes = ParseDiagnosticJson(routesTask.Result),
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string path = Path.Combine(LogService.RuntimeDirectory, "last-network-failure.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, snapshot, new System.Text.UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            LogService.Info("Captured EasyTier peer/route state before runtime recovery.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Info("EasyTier failure snapshot was unavailable: " + ex.GetType().Name);
        }
    }

    private static object? ParseDiagnosticJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(value);
        }
        catch
        {
            return new { invalidJson = true, length = value.Length };
        }
    }

    public string ReadAssignedIp()
    {
        lock (_stateSync)
        {
            if (NetworkHealthCheckService.IsValidScblClientIp(_lastAssignedIp))
                return _lastAssignedIp;
        }
        return _addressResolver.ReadFromAdapter();
    }

    public string ReadTunnelLogTail(int maxLines = 60) => LogService.ReadTail(maxLines);

    public void Stop(string reason)
    {
        _runtimePresenceTracker.Reset();
        _processSupervisor.Stop(reason);
        lock (_stateSync)
        {
            _lastAssignedIp = string.Empty;
            AdvanceRuntimeObservationUnsafe();
            _udpBroadcastRelayExpected = false;
            _udpBroadcastRelayExplicitFailure = false;
            _udpBroadcastRelayDegraded = false;
            _udpBroadcastRelayConfirmed = false;
            _udpBroadcastRelayMessage = string.Empty;
        }
    }

    public static void StopAllTunnelClients(string reason)
        => EasyTierProcessSupervisor.StopAllOwnedProcesses(reason);

    private void ResetForStart(string configPath, bool broadcastRelayExpected)
    {
        lock (_stateSync)
        {
            _runningConfigPath = configPath;
            _lastAssignedIp = string.Empty;
            AdvanceRuntimeObservationUnsafe();
            _udpBroadcastRelayExpected = broadcastRelayExpected;
            _udpBroadcastRelayExplicitFailure = false;
            _udpBroadcastRelayDegraded = false;
            _udpBroadcastRelayConfirmed = false;
            _udpBroadcastRelayMessage = broadcastRelayExpected
                ? "UDP broadcast relay is enabled; waiting for EasyTier startup status."
                : "UDP broadcast relay is disabled by configuration.";
        }
    }

    private void SetLastAssignedIp(string address)
    {
        lock (_stateSync)
            _lastAssignedIp = address;
    }

    private void AdvanceRuntimeObservationUnsafe()
        => _runtimeObservationId = _runtimeObservationId == long.MaxValue
            ? 1
            : _runtimeObservationId + 1;
}
