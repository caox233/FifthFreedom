using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

/// <summary>
/// 公网版客户端网络总控器。
/// 目标：所有网络启动、复用、检测、重连、关闭都从这里进入，避免 UI、按钮、watchdog、启动流程多处抢状态。
/// </summary>
public sealed class NetworkOrchestrator : INetworkOrchestrator
{
    private readonly PublicTunnelService _tunnelService;
    private readonly ProcessRouterService _processRouterService;
    private readonly ScblTunnelAdapterService _adapterService;
    private readonly FirewallService _firewallService;
    private readonly NetworkHealthCheckService _healthCheckService;
    private readonly Func<string> _getLauncherBaseDir;
    private readonly Func<string> _getGameDir;
    private readonly Func<string> _getPublicEndpoint;
    private readonly Func<string> _getTunnelSecret;
    private readonly Func<string> _getAssignedIp;
    private readonly Func<EasyTierClientOptions> _getEasyTierOptions;
    private readonly Func<bool> _isGameSessionActive;
    private readonly Func<string> _getActiveGameSessionIp;
    private readonly Action<string, long?> _setAssignedIp;
    private readonly Action _saveSettings;

    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private readonly SemaphoreSlim _networkRecoverySignal = new(0, 1);
    private CancellationTokenSource? _watchdogCts;
    private CancellationTokenSource? _shutdownCts;
    private NetworkPhase _lastPhase = NetworkPhase.Unknown;
    private DateTime _lastGreenUtc = DateTime.MinValue;
    private DateTime _lastYellowUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _shutdownStarted;
    private bool _startupPreparationDone;
    private volatile bool _runtimeRebuildInProgress;
    private string _assignedIp = "";
    private long? _lastLatencyMs;
    private string _lastTransportMode = "";
    private string _lastUnderlayAddressFamily = "";
    private long _lastRuntimeObservationId;
    private string _pendingRecoveryReason = string.Empty;
    private int _activeGameRecoveryExhausted;
    private int _activeGameRuntimeRebuildAttempted;

    private const int GreenHoldSeconds = 15;
    private const int YellowDebounceMs = 650;
    private const int ActiveGameRecoveryMaxAttempts = 6;
    private static readonly TimeSpan ActiveGameRecoveryRetryDelay = TimeSpan.FromSeconds(2);

    public event Action<NetworkStatusSnapshot>? StatusChanged;
    public bool IsReady { get; private set; }
    public string AssignedIp => _assignedIp;
    public long? LastLatencyMs => _lastLatencyMs;
    public string LastTransportMode => _lastTransportMode;
    public string LastUnderlayAddressFamily => _lastUnderlayAddressFamily;
    public long LastRuntimeObservationId => _lastRuntimeObservationId;

    public NetworkOrchestrator(
        PublicTunnelService tunnelService,
        ProcessRouterService processRouterService,
        ScblTunnelAdapterService adapterService,
        FirewallService firewallService,
        Func<string> getLauncherBaseDir,
        Func<string> getGameDir,
        Func<string> getPublicEndpoint,
        Func<string> getTunnelSecret,
        Func<string> getAssignedIp,
        Func<EasyTierClientOptions> getEasyTierOptions,
        Func<bool> isGameSessionActive,
        Action<string, long?> setAssignedIp,
        Action saveSettings,
        Func<string>? getActiveGameSessionIp = null,
        NetworkHealthCheckService? healthCheckService = null)
    {
        _tunnelService = tunnelService;
        _processRouterService = processRouterService;
        _adapterService = adapterService;
        _firewallService = firewallService;
        _getLauncherBaseDir = getLauncherBaseDir;
        _getGameDir = getGameDir;
        _getPublicEndpoint = getPublicEndpoint;
        _getTunnelSecret = getTunnelSecret;
        _getAssignedIp = getAssignedIp;
        _getEasyTierOptions = getEasyTierOptions;
        _isGameSessionActive = isGameSessionActive;
        _getActiveGameSessionIp = getActiveGameSessionIp ?? (() =>
            AuthenticatedSessionStore.Shared.TryGetCurrentBindIp(out string bindIp)
                ? bindIp
                : string.Empty);
        _setAssignedIp = setAssignedIp;
        _saveSettings = saveSettings;
        _healthCheckService = healthCheckService ?? new NetworkHealthCheckService();
        _assignedIp = _getAssignedIp()?.Trim() ?? "";
        _tunnelService.ServerPathValidationRequested += RequestServerPathValidation;
    }

    public async Task<NetworkReadyResult> EnsureReadyAsync(NetworkEnsureMode mode, string reason, CancellationToken cancellationToken = default)
    {
        if (_shutdownStarted)
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Network orchestrator is shutting down.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts?.Token ?? CancellationToken.None);
        var token = linkedCts.Token;

        if (!await _ensureLock.WaitAsync(0, token).ConfigureAwait(false))
        {
            LogService.Info($"Network orchestrator is already running; waiting. reason={reason}, mode={mode}");
            await _ensureLock.WaitAsync(token).ConfigureAwait(false);
            _ensureLock.Release();
            if (IsReady && NetworkHealthCheckService.IsValidScblClientIp(_assignedIp))
                return NetworkReadyResult.Success(_assignedIp, _lastLatencyMs, transportMode: _lastTransportMode);
            return NetworkReadyResult.Failed(NetworkPhase.ServerFailed, NetworkFailureStage.Server, "Network check did not finish successfully.");
        }

        try
        {
            LogService.Info($"Network orchestrator ensure started. mode={mode}, reason={reason}");

            if (mode == NetworkEnsureMode.Automatic && _isGameSessionActive())
            {
                NetworkReadyResult inPlace = await WaitForActiveGameRuntimeRecoveryAsync(token)
                    .ConfigureAwait(false);
                if (inPlace.Ok)
                    return inPlace;

                if (_isGameSessionActive())
                {
                    if (!TryBeginBoundedRuntimeRebuild(
                            ref _activeGameRuntimeRebuildAttempted))
                    {
                        Interlocked.Exchange(ref _activeGameRecoveryExhausted, 1);
                        return await HandleFailureAsync(
                            NetworkReadyResult.Failed(
                                NetworkPhase.ServerFailed,
                                NetworkFailureStage.Server,
                                "The bounded EasyTier recovery attempt was already used for this game session. Exit and restart the game before trying again."),
                            mode,
                            reason,
                            token).ConfigureAwait(false);
                    }

                    LogService.Warning(
                        "The existing EasyTier generation did not recover in place; "
                        + "performing one controlled runtime rebuild while preserving the launcher-owned game and Route Guard session.");
                }

                if (!_isGameSessionActive())
                {
                    LogService.Info(
                        "The game ended while in-place network recovery was waiting; "
                        + "a normal EasyTier runtime rebuild may now proceed.");
                }
            }

            // 1. 静默快检：能复用就直接绿灯，不显示黄灯。
            NetworkReadyResult reuse = NetworkReadyResult.Failed(
                NetworkPhase.ServerFailed,
                NetworkFailureStage.Server,
                "Automatic recovery requires a new EasyTier runtime generation.");
            if (mode != NetworkEnsureMode.Automatic)
            {
                reuse = await TrySilentReuseAsync(mode, reason, token).ConfigureAwait(false);
                if (reuse.Ok)
                    return reuse;
            }

            if (mode == NetworkEnsureMode.SilentStartup)
            {
                // 二次启动时给旧进程/网卡一个很短的稳定窗口，避免“刚关闭又打开”的半清理状态误判。
                await Task.Delay(350, token).ConfigureAwait(false);
                reuse = await TrySilentReuseAsync(mode, reason + " after warm window", token).ConfigureAwait(false);
                if (reuse.Ok)
                    return reuse;
            }

            ResetTransportObservationForRuntimeRebuild();
            _runtimeRebuildInProgress = true;
            IsReady = false;

            if (mode == NetworkEnsureMode.Repair)
            {
                Emit(NetworkPhase.Preparing, null, "full repair cleanup", force: true);
                await Task.Run(() =>
                {
                    ProcessRouterService.StopAllRouters("network repair");
                    PublicTunnelService.StopAllTunnelClients("network repair");
                    _adapterService.FullRepairCleanupBestEffort();
                }, token).ConfigureAwait(false);
                _startupPreparationDone = false;
            }

            // 2. 确认基础环境。只有没有主网卡时才显示“网络准备中”。已有主网卡时直接显示“隧道连接中”。
            bool hasPrimaryAdapter = _adapterService.HasPrimaryAdapterBestEffort();
            Emit(hasPrimaryAdapter ? NetworkPhase.TunnelConnecting : NetworkPhase.Preparing, null, "prepare/start tunnel", force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);
            await PrepareEnvironmentOnceAsync(token).ConfigureAwait(false);

            // 3. 启动或重建隧道。
            Emit(NetworkPhase.TunnelConnecting, null, "starting tunnel", force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);
            EasyTierClientOptions easyTierOptions = _getEasyTierOptions();
            string ip = await _tunnelService.EnsureStartedAsync(
                _getLauncherBaseDir(),
                TimeSpan.FromSeconds(mode == NetworkEnsureMode.SilentStartup ? 12 : 18),
                _getPublicEndpoint(),
                _getTunnelSecret(),
                easyTierOptions,
                forceRuntimeRestart: mode == NetworkEnsureMode.Automatic).ConfigureAwait(false);

            if (!IsActiveGameSessionAddressCompatible(
                    _isGameSessionActive(),
                    _getActiveGameSessionIp(),
                    ip))
            {
                Interlocked.Exchange(ref _activeGameRecoveryExhausted, 1);
                return await HandleFailureAsync(
                    NetworkReadyResult.Failed(
                        NetworkPhase.ServerFailed,
                        NetworkFailureStage.Server,
                        "EasyTier rebuilt with a different overlay address than the running game session. Exit and restart the game before reconnecting."),
                    mode,
                    reason,
                    token).ConfigureAwait(false);
            }

            SetAssignedIp(ip);
            _adapterService.EnsureRouteBindingBestEffort(ip);

            // 4. EasyTier has a virtual IP. Route Guard is intentionally NOT started here.
            // v0.5.14 starts it only after Process.Start returns the launcher-owned game PID,
            // so idle launchers and games started by other launchers are never intercepted.
            Emit(NetworkPhase.ServerConnecting, null, "waiting EasyTier route/server", force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);

            // 5. A DHCP lease and a local /24 route do not prove that EasyTier's peer route
            // has converged. Poll the real server data path before either publishing Connected
            // or tearing down a runtime that may still be synchronising its peer table.
            NetworkReadyResult check = await WaitForServerDataPathAsync(ip, mode, token)
                .ConfigureAwait(false);

            if (!check.Ok && !_isGameSessionActive())
            {
                string? fallbackIp = await _tunnelService.RestartWithIpv4FallbackAsync(
                    _getLauncherBaseDir(),
                    TimeSpan.FromSeconds(mode == NetworkEnsureMode.SilentStartup ? 12 : 18),
                    _getPublicEndpoint(),
                    _getTunnelSecret(),
                    easyTierOptions).ConfigureAwait(false);
                if (NetworkHealthCheckService.IsValidScblClientIp(fallbackIp))
                {
                    ResetTransportObservationForRuntimeRebuild();
                    ip = fallbackIp!;
                    SetAssignedIp(ip);
                    _adapterService.EnsureRouteBindingBestEffort(ip);
                    Emit(
                        NetworkPhase.ServerConnecting,
                        null,
                        "waiting IPv4 fallback EasyTier route/server",
                        force: mode == NetworkEnsureMode.Manual || mode == NetworkEnsureMode.BeforeLaunch);
                    check = await WaitForServerDataPathAsync(ip, mode, token)
                        .ConfigureAwait(false);
                }
            }

            if (check.Ok)
            {
                EasyTierServerPathObservation serverPath = await DetectExpectedServerPathAsync(
                    easyTierOptions.NetworkMode,
                    token).ConfigureAwait(false);
                MarkConnected(ip, check.LatencyMs, serverPath);
                return NetworkReadyResult.Success(
                    ip,
                    check.LatencyMs,
                    check.Message,
                    serverPath.TransportMode);
            }

            await _tunnelService.CaptureFailureSnapshotBestEffortAsync(
                _getLauncherBaseDir(),
                check.Message,
                token).ConfigureAwait(false);
            if (mode == NetworkEnsureMode.Automatic && _isGameSessionActive())
                Interlocked.Exchange(ref _activeGameRecoveryExhausted, 1);
            return await HandleFailureAsync(check, mode, reason, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NetworkReadyResult.Failed(NetworkPhase.NetworkFailed, NetworkFailureStage.Network, "Network operation was cancelled.");
        }
        catch (Exception ex)
        {
            LogService.Error($"Network orchestrator failed. mode={mode}, reason={reason}, error={ex}");
            var phase = ex is TimeoutException ? NetworkPhase.TunnelFailed : NetworkPhase.TunnelFailed;
            var result = NetworkReadyResult.Failed(phase, NetworkFailureStage.Tunnel, ex.Message + _tunnelService.ReadTunnelLogTail());
            return await HandleFailureAsync(result, mode, reason, token).ConfigureAwait(false);
        }
        finally
        {
            _runtimeRebuildInProgress = false;
            _ensureLock.Release();
        }
    }

    public async Task<NetworkReadyResult> QuickVerifyAsync(
        string reason,
        CancellationToken cancellationToken = default,
        bool forceServerPathRefresh = false)
    {
        string ip = _assignedIp;
        if (!NetworkHealthCheckService.IsValidScblClientIp(ip))
            ip = _getAssignedIp();
        if (!NetworkHealthCheckService.IsValidScblClientIp(ip))
            ip = _tunnelService.ReadAssignedIp();

        var result = await _healthCheckService.QuickCheckAsync(ip, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            if (!IsActiveGameSessionAddressCompatible(
                    _isGameSessionActive(),
                    _getActiveGameSessionIp(),
                    result.AssignedIp))
            {
                IsReady = false;
                const string mismatch =
                    "EasyTier is reachable on a different overlay address than the running game session; restart the game before reconnecting.";
                Emit(NetworkPhase.ServerFailed, null, mismatch, force: true);
                return NetworkReadyResult.Failed(
                    NetworkPhase.ServerFailed,
                    NetworkFailureStage.Server,
                    mismatch);
            }

            if (_runtimeRebuildInProgress)
                return result with { TransportMode = "" };

            EasyTierServerPathObservation? serverPath = forceServerPathRefresh
                ? null
                : GetLastConfirmedServerPath();
            if (serverPath == null)
            {
                try
                {
                    serverPath = await DetectExpectedServerPathAsync(
                        _getEasyTierOptions().NetworkMode,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    IsReady = false;
                    LogService.Info("EasyTier server path is not yet confirmed: " + ex.Message);
                    Emit(
                        NetworkPhase.ServerConnecting,
                        result.LatencyMs,
                        "waiting for actual EasyTier server path",
                        force: true);
                    return NetworkReadyResult.Failed(
                        NetworkPhase.ServerConnecting,
                        NetworkFailureStage.Server,
                        "EasyTier server path details are not yet confirmed.");
                }
            }

            MarkConnected(result.AssignedIp, result.LatencyMs, serverPath);
            return result with { TransportMode = serverPath.TransportMode };
        }
        return result;
    }

    public void StartWatchdog()
    {
        if (_watchdogCts != null)
            return;

        _shutdownCts ??= new CancellationTokenSource();
        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var token = _watchdogCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _networkRecoverySignal.WaitAsync(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _shutdownStarted)
                        break;

                    string trigger = Interlocked.Exchange(
                        ref _pendingRecoveryReason,
                        string.Empty);
                    var quick = await QuickVerifyAsync(
                        "network recovery signal",
                        token,
                        forceServerPathRefresh: true).ConfigureAwait(false);
                    if (quick.Ok)
                    {
                        _consecutiveFailures = 0;
                        Interlocked.Exchange(ref _activeGameRecoveryExhausted, 0);
                        Interlocked.Exchange(ref _activeGameRuntimeRebuildAttempted, 0);
                        ClearQueuedRecoverySignals();
                        LogService.Info(
                            "Network recovery signal was a transient spike; "
                            + $"the authoritative server path is still healthy. trigger={trigger}");
                        continue;
                    }

                    IsReady = false;
                    LogService.Error(
                        "Network recovery signal confirmed a server-path failure; "
                        + "recovery starts immediately. "
                        + $"gameActive={_isGameSessionActive()}, trigger={trigger}, message={quick.Message}");
                    Emit(NetworkPhase.Reconnecting, null, "automatic network recovery", force: true);
                    if (_isGameSessionActive()
                        && Volatile.Read(ref _activeGameRecoveryExhausted) != 0)
                    {
                        LogService.Info(
                            "The active-game in-place recovery window is already exhausted; "
                            + "the current runtime is preserved and only server-path validation continues.");
                        continue;
                    }

                    await EnsureReadyAsync(
                        NetworkEnsureMode.Automatic,
                        "automatic network recovery",
                        token).ConfigureAwait(false);
                    ClearQueuedRecoverySignals();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Error("Event-driven network recovery failed: " + ex.Message);
                }
            }
        }, token);
    }

    private void RequestServerPathValidation(string reason)
    {
        if (_shutdownStarted)
            return;

        Interlocked.Exchange(
            ref _pendingRecoveryReason,
            string.IsNullOrWhiteSpace(reason) ? "server-path signal" : reason.Trim());
        try
        {
            if (_networkRecoverySignal.CurrentCount == 0)
                _networkRecoverySignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Multiple EasyTier/quality signals coalesce into one authoritative validation.
        }
        catch (ObjectDisposedException)
        {
            // A late process callback may race the final unsubscribe during shutdown.
        }
    }

    private void ClearQueuedRecoverySignals()
    {
        Interlocked.Exchange(ref _pendingRecoveryReason, string.Empty);
        while (_networkRecoverySignal.Wait(0))
        {
        }
    }

    public async Task ShutdownAsync(string reason, bool preserveTunnelRuntime = false)
    {
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;
        try
        {
            LogService.Info("Network orchestrator shutdown: " + reason);
            try { _watchdogCts?.Cancel(); } catch { }
            try { _shutdownCts?.Cancel(); } catch { }
            // 普通关闭启动器时停止导流/隧道运行时，避免用户用原版启动器时同名游戏进程仍被导流。
            // EasyTier owns the primary virtual adapter. Stop only the packaged runtime and leave full device removal to “修复网络”.
            // Route Guard retains only previously authorised live game PIDs after Launcher exit,
            // then self-releases WinDivert after five continuous idle minutes.
            // The plain EasyTier tunnel, however, can safely remain connected on an ordinary close:
            // it only owns the 10.66.0.0/24 overlay route and enables the next launcher start to use
            // the read-only silent-reuse path. Updates and repairs still request a full shutdown.
            await Task.Run(() =>
            {
                if (preserveTunnelRuntime)
                {
                    _processRouterService.DetachForLauncherExit(reason);
                }
                else
                {
                    _processRouterService.Stop(reason);
                    ProcessRouterService.StopAllRouters(reason);
                }
            }).ConfigureAwait(false);

            if (preserveTunnelRuntime)
            {
                LogService.Info("EasyTier runtime was preserved; Route Guard now owns its five-minute idle release.");
            }
            else
            {
                await Task.Run(() =>
                {
                    _tunnelService.Stop(reason);
                    PublicTunnelService.StopAllTunnelClients(reason);
                }).ConfigureAwait(false);
                LogService.Info("EasyTier network runtime stopped.");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Network orchestrator shutdown failed: " + ex.Message);
        }
    }

    private async Task<NetworkReadyResult> TrySilentReuseAsync(NetworkEnsureMode mode, string reason, CancellationToken token)
    {
        EasyTierClientOptions options = _getEasyTierOptions();
        if (!_tunnelService.CanReuseRuntimeProfile(
                _getLauncherBaseDir(),
                _getPublicEndpoint(),
                _getTunnelSecret(),
                options))
        {
            LogService.Info("EasyTier runtime profile changed or is unknown; silent reuse is disabled for this start.");
            await Task.Run(() =>
            {
                ProcessRouterService.StopAllRouters("runtime profile changed");
                PublicTunnelService.StopAllTunnelClients("runtime profile changed");
            }, token).ConfigureAwait(false);
            return NetworkReadyResult.Failed(NetworkPhase.TunnelConnecting, NetworkFailureStage.Tunnel, "EasyTier runtime profile requires restart.");
        }
        foreach (string ip in GetCandidateIps())
        {
            token.ThrowIfCancellationRequested();
            LogService.Info($"Network orchestrator silent reuse: reason={reason}, ip={ip}");
            var check = await _healthCheckService.QuickCheckAsync(ip, token).ConfigureAwait(false);
            if (!check.Ok)
                continue;

            EasyTierServerPathObservation serverPath;
            try
            {
                serverPath = await DetectExpectedServerPathAsync(
                    options.NetworkMode,
                    token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                LogService.Info("Silent network reuse is waiting for complete EasyTier server path details: " + ex.Message);
                continue;
            }

            SetAssignedIp(ip);
            MarkConnected(ip, check.LatencyMs, serverPath);

            // QuickCheckAsync already proved the existing EasyTier route and server path.
            // Rebinding here starts PowerShell and adds visible latency to every game launch.
            LogService.Info("Silent network reuse passed the read-only route check; route rebinding was skipped.");
            // Route Guard may still be in its native idle grace from a previous Launcher.
            // This instance holds no game authorisation until an actual owned PID is supplied.
            if (!_processRouterService.TryTakeOverIdleGuard())
                _processRouterService.ReleaseGameSession("network reused while no launcher-owned game session is active");

            LogService.Info($"Network orchestrator silent reuse succeeded: ip={ip}, latency={check.LatencyMs?.ToString() ?? "n/a"}ms");
            return NetworkReadyResult.Success(
                ip,
                check.LatencyMs,
                transportMode: serverPath.TransportMode);
        }

        return NetworkReadyResult.Failed(NetworkPhase.ServerFailed, NetworkFailureStage.Server, "Silent reuse failed.");
    }

    private IEnumerable<string> GetCandidateIps()
    {
        return new[]
            {
                _assignedIp,
                _getAssignedIp(),
                _tunnelService.ReadAssignedIp()
            }
            .Where(NetworkHealthCheckService.IsValidScblClientIp)
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task PrepareEnvironmentOnceAsync(CancellationToken token)
    {
        if (_startupPreparationDone)
            return;

        var baseDir = _getLauncherBaseDir();
        var gameDir = _getGameDir();
        try
        {
            await Task.Run(
                () => _firewallService.EnsureFirewallRulesBestEffort(baseDir, gameDir),
                token).ConfigureAwait(false);
            await Task.Run(
                () => _adapterService.CleanupBeforeStartBestEffort(),
                token).ConfigureAwait(false);
            _startupPreparationDone = true;
        }
        catch
        {
            _startupPreparationDone = false;
            throw;
        }
    }

    private async Task<NetworkReadyResult> WaitForServerDataPathAsync(
        string ip,
        NetworkEnsureMode mode,
        CancellationToken token)
    {
        int attempt = 0;
        NetworkReadyResult quick = await ProbeUntilReadyAsync(
            async cancellationToken =>
            {
                attempt++;
                NetworkReadyResult result = await _healthCheckService
                    .QuickCheckAsync(ip, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ok)
                {
                    LogService.Info(
                        $"EasyTier server data path is still converging; attempt={attempt}/8, "
                        + result.Message);
                }
                return result;
            },
            maxAttempts: 8,
            retryDelay: TimeSpan.FromMilliseconds(300),
            token).ConfigureAwait(false);

        if (!quick.Ok)
            return quick;

        if (mode != NetworkEnsureMode.Manual && mode != NetworkEnsureMode.BeforeLaunch)
            return quick;

        // The core route is now proven. The detailed pass records auxiliary service state,
        // while its existing contract still accepts either gRPC or config as the core gate.
        return await _healthCheckService.DetailedCheckAsync(ip, token).ConfigureAwait(false);
    }

    private async Task<NetworkReadyResult> WaitForActiveGameRuntimeRecoveryAsync(
        CancellationToken cancellationToken)
    {
        string sessionIp = _getActiveGameSessionIp()?.Trim() ?? string.Empty;
        long runtimeObservationId = _tunnelService.CurrentRuntimeObservationId;
        string previousUnderlayAddressFamily = _lastUnderlayAddressFamily;
        if (!NetworkHealthCheckService.IsValidScblClientIp(sessionIp))
        {
            return NetworkReadyResult.Failed(
                NetworkPhase.ServerFailed,
                NetworkFailureStage.Server,
                "The running game session does not have a valid EasyTier overlay address.");
        }

        int attempt = 0;
        NetworkReadyResult result = await WaitForInPlaceRecoveryAsync(
            async token =>
            {
                attempt++;
                NetworkReadyResult quick = await QuickVerifyAsync(
                    $"active-game in-place recovery attempt {attempt}",
                    token).ConfigureAwait(false);
                if (!quick.Ok)
                {
                    LogService.Info(
                        $"The active-game EasyTier runtime is reconnecting in place; "
                        + $"attempt={attempt}/{ActiveGameRecoveryMaxAttempts}, {quick.Message}");
                }
                return quick;
            },
            () => _isGameSessionActive()
                && runtimeObservationId == _tunnelService.CurrentRuntimeObservationId
                && sessionIp.Equals(
                    _getActiveGameSessionIp()?.Trim(),
                    StringComparison.OrdinalIgnoreCase),
            ActiveGameRecoveryMaxAttempts,
            ActiveGameRecoveryRetryDelay,
            cancellationToken).ConfigureAwait(false);

        if (result.Ok)
        {
            Interlocked.Exchange(ref _activeGameRecoveryExhausted, 0);
            Interlocked.Exchange(ref _activeGameRuntimeRebuildAttempted, 0);
            if (previousUnderlayAddressFamily.Equals(
                    "IPv6",
                    StringComparison.OrdinalIgnoreCase)
                && _lastUnderlayAddressFamily.Equals(
                    "IPv4",
                    StringComparison.OrdinalIgnoreCase))
            {
                _tunnelService.ConfirmInPlaceIpv6ToIpv4FallbackSucceeded();
            }
            LogService.Info(
                "The active-game EasyTier runtime recovered in place without replacing "
                + $"the virtual adapter or overlay address {sessionIp}.");
        }
        return result;
    }

    internal static async Task<NetworkReadyResult> WaitForInPlaceRecoveryAsync(
        Func<CancellationToken, Task<NetworkReadyResult>> probe,
        Func<bool> canContinue,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (probe == null)
            throw new ArgumentNullException(nameof(probe));
        if (canContinue == null)
            throw new ArgumentNullException(nameof(canContinue));
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        NetworkReadyResult last = NetworkReadyResult.Failed(
            NetworkPhase.ServerFailed,
            NetworkFailureStage.Server,
            "The in-place recovery probe was not run.");
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!canContinue())
            {
                return NetworkReadyResult.Failed(
                    NetworkPhase.ServerFailed,
                    NetworkFailureStage.Server,
                    "The active game or EasyTier runtime generation changed during in-place recovery.");
            }

            last = await probe(cancellationToken).ConfigureAwait(false);
            if (last.Ok)
                return last;
            if (attempt < maxAttempts && retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
        return last;
    }

    internal static async Task<NetworkReadyResult> ProbeUntilReadyAsync(
        Func<CancellationToken, Task<NetworkReadyResult>> probe,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        NetworkReadyResult last = NetworkReadyResult.Failed(
            NetworkPhase.ServerFailed,
            NetworkFailureStage.Server,
            "Server data-path check was not run.");
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await probe(cancellationToken).ConfigureAwait(false);
            if (last.Ok)
                return last;
            if (attempt < maxAttempts && retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
        return last;
    }

    internal static bool TryBeginBoundedRuntimeRebuild(ref int rebuildAttempted)
        => Interlocked.CompareExchange(ref rebuildAttempted, 1, 0) == 0;

    private async Task<NetworkReadyResult> HandleFailureAsync(NetworkReadyResult failure, NetworkEnsureMode mode, string reason, CancellationToken token)
    {
        _consecutiveFailures++;
        IsReady = false;
        LogService.Error($"Network orchestrator failure. mode={mode}, reason={reason}, consecutive={_consecutiveFailures}, message={failure.Message}");

        if (mode == NetworkEnsureMode.SilentStartup)
        {
            // Startup owns the player-facing Fast -> Compatible decision. Do not leave a
            // detached retry racing an explicit mode change after the initial bounded start.
            Emit(failure.Phase, null, failure.Message, force: true);
            return failure;
        }

        Emit(failure.Phase, null, failure.Message, force: true);
        await Task.CompletedTask.ConfigureAwait(false);
        return failure;
    }

    private void SetAssignedIp(string ip, long? latencyMs = null)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return;

        string normalized = ip.Trim();
        if (normalized.Equals(_assignedIp, StringComparison.OrdinalIgnoreCase))
            return;

        _assignedIp = normalized;
        _setAssignedIp(_assignedIp, latencyMs);
        _saveSettings();
    }

    private void MarkConnected(
        string ip,
        long? latencyMs,
        EasyTierServerPathObservation serverPath)
    {
        if (!IsActiveGameSessionAddressCompatible(
                _isGameSessionActive(),
                _getActiveGameSessionIp(),
                ip))
        {
            IsReady = false;
            throw new InvalidOperationException(
                "EasyTier recovered with a different overlay address than the running game session; restart the game before reconnecting.");
        }

        if (serverPath.RuntimeObservationId != _tunnelService.CurrentRuntimeObservationId
            || !NetworkStatusSnapshot.HasCompleteConnectionDetails(
                NetworkPhase.Connected,
                serverPath.TransportMode,
                serverPath.UnderlayAddressFamily,
                serverPath.RuntimeObservationId))
        {
            throw new InvalidOperationException(
                "Connected requires transport and underlay address family from the current EasyTier runtime.");
        }

        SetAssignedIp(ip, latencyMs);
        _lastLatencyMs = latencyMs;
        _lastTransportMode = serverPath.TransportMode.Trim();
        _lastUnderlayAddressFamily = serverPath.UnderlayAddressFamily.Trim();
        _lastRuntimeObservationId = serverPath.RuntimeObservationId;
        _lastGreenUtc = DateTime.UtcNow;
        _consecutiveFailures = 0;
        Interlocked.Exchange(ref _activeGameRecoveryExhausted, 0);
        Interlocked.Exchange(ref _activeGameRuntimeRebuildAttempted, 0);
        IsReady = true;
        Emit(
            NetworkPhase.Connected,
            latencyMs,
            "connected",
            force: true,
            transportMode: _lastTransportMode,
            underlayAddressFamily: _lastUnderlayAddressFamily,
            runtimeObservationId: _lastRuntimeObservationId);
    }

    internal static bool IsActiveGameSessionAddressCompatible(
        bool isGameSessionActive,
        string? gameSessionIp,
        string? runtimeIp)
    {
        if (!isGameSessionActive)
            return true;
        if (!NetworkHealthCheckService.IsValidScblClientIp(gameSessionIp)
            || !NetworkHealthCheckService.IsValidScblClientIp(runtimeIp))
        {
            return false;
        }
        return gameSessionIp!.Trim().Equals(
            runtimeIp!.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private EasyTierServerPathObservation? GetLastConfirmedServerPath()
    {
        long currentRuntimeObservationId = _tunnelService.CurrentRuntimeObservationId;
        return IsReady
            && _lastRuntimeObservationId == currentRuntimeObservationId
            && NetworkStatusSnapshot.HasCompleteConnectionDetails(
                NetworkPhase.Connected,
                _lastTransportMode,
                _lastUnderlayAddressFamily,
                _lastRuntimeObservationId)
            ? new EasyTierServerPathObservation(
                _lastTransportMode,
                _lastUnderlayAddressFamily,
                _lastRuntimeObservationId)
            : null;
    }

    private async Task<EasyTierServerPathObservation> DetectExpectedServerPathAsync(
        ClientNetworkMode networkMode,
        CancellationToken cancellationToken)
    {
        string expected = ExpectedServerTransportForMode(networkMode);
        EasyTierServerPathObservation? detected = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                detected = await _tunnelService.DetectServerPathObservationAsync(
                    _getLauncherBaseDir(),
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                detected = null;
                LogService.Info("EasyTier server path detection skipped: " + ex.Message);
            }
            if (detected != null
                && IsExpectedServerTransport(networkMode, detected.TransportMode))
            {
                return detected;
            }
            if (attempt < 3)
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "EasyTier server path did not converge to complete details for the selected network mode. "
            + $"expected={expected}, detected={detected?.TransportMode ?? ""}, "
            + $"underlay={detected?.UnderlayAddressFamily ?? ""}");
    }

    private void ResetTransportObservationForRuntimeRebuild()
    {
        _lastTransportMode = "";
        _lastUnderlayAddressFamily = "";
        _lastRuntimeObservationId = 0;
        _tunnelService.InvalidateRuntimeObservation();
    }

    internal static string ExpectedServerTransportForMode(ClientNetworkMode networkMode)
        => networkMode == ClientNetworkMode.Compatible ? "WSS" : "QUIC";

    internal static bool IsExpectedServerTransport(
        ClientNetworkMode networkMode,
        string? detectedTransport)
        => ExpectedServerTransportForMode(networkMode).Equals(
            detectedTransport?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private void Emit(
        NetworkPhase phase,
        long? latencyMs,
        string message,
        bool force = false,
        string transportMode = "",
        string underlayAddressFamily = "",
        long runtimeObservationId = 0)
    {
        if (_shutdownStarted)
            return;

        _statusLock.Wait();
        try
        {
            string normalizedTransport = NetworkStatusSnapshot.NormalizeTransportMode(
                phase,
                transportMode);
            string normalizedAddressFamily = NetworkStatusSnapshot.NormalizeUnderlayAddressFamily(
                phase,
                underlayAddressFamily);
            long normalizedRuntimeObservationId = NetworkStatusSnapshot.NormalizeRuntimeObservationId(
                phase,
                runtimeObservationId);
            if (phase == NetworkPhase.Connected
                && !NetworkStatusSnapshot.HasCompleteConnectionDetails(
                    phase,
                    normalizedTransport,
                    normalizedAddressFamily,
                    normalizedRuntimeObservationId))
            {
                LogService.Warning(
                    "Network orchestrator suppressed incomplete Connected snapshot; "
                    + "waiting for current EasyTier server path details.");
                return;
            }

            bool isYellow = phase is NetworkPhase.Preparing or NetworkPhase.TunnelConnecting or NetworkPhase.ServerConnecting or NetworkPhase.Reconnecting;
            if (!force && isYellow)
            {
                if (_lastPhase == NetworkPhase.Connected && (DateTime.UtcNow - _lastGreenUtc).TotalSeconds < GreenHoldSeconds)
                {
                    LogService.Info($"Network orchestrator status debounce: keep green, suppress {phase}.");
                    return;
                }
                if ((DateTime.UtcNow - _lastYellowUtc).TotalMilliseconds < YellowDebounceMs && _lastPhase != NetworkPhase.Unknown)
                {
                    LogService.Info($"Network orchestrator status debounce: suppress rapid yellow {_lastPhase}->{phase}.");
                    return;
                }
                _lastYellowUtc = DateTime.UtcNow;
            }

            if (!force && IsBackwardYellowTransition(_lastPhase, phase))
            {
                LogService.Info($"Network orchestrator status debounce: suppress backward yellow {_lastPhase}->{phase}.");
                return;
            }

            _lastPhase = phase;
            if (phase == NetworkPhase.Connected)
                _lastGreenUtc = DateTime.UtcNow;

            StatusChanged?.Invoke(new NetworkStatusSnapshot(
                phase,
                latencyMs ?? _lastLatencyMs,
                message,
                normalizedTransport,
                normalizedAddressFamily,
                normalizedRuntimeObservationId));
        }
        finally
        {
            _statusLock.Release();
        }
    }


    private static bool IsBackwardYellowTransition(NetworkPhase last, NetworkPhase next)
    {
        if (last is NetworkPhase.Unknown or NetworkPhase.Connected or NetworkPhase.NetworkFailed or NetworkPhase.TunnelFailed or NetworkPhase.ServerFailed)
            return false;
        if (next is not (NetworkPhase.Preparing or NetworkPhase.TunnelConnecting or NetworkPhase.ServerConnecting or NetworkPhase.Reconnecting))
            return false;
        return PhaseOrder(next) < PhaseOrder(last);
    }

    private static int PhaseOrder(NetworkPhase phase) => phase switch
    {
        NetworkPhase.Preparing => 1,
        NetworkPhase.TunnelConnecting => 2,
        NetworkPhase.ServerConnecting => 3,
        NetworkPhase.Reconnecting => 4,
        NetworkPhase.Connected => 5,
        _ => 0
    };

    public void Dispose()
    {
        _tunnelService.ServerPathValidationRequested -= RequestServerPathValidation;
        try { _watchdogCts?.Cancel(); } catch { }
        try { _shutdownCts?.Cancel(); } catch { }
        _watchdogCts?.Dispose();
        _shutdownCts?.Dispose();
        _ensureLock.Dispose();
        _statusLock.Dispose();
        _networkRecoverySignal.Dispose();
    }
}
