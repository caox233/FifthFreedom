using Microsoft.Win32;
using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

internal sealed class NetworkReadyRemoteServicesGate
{
    private int _started;

    public bool TryStart(bool isReady, bool isClosing)
        => isReady
            && !isClosing
            && Interlocked.CompareExchange(ref _started, 1, 0) == 0;
}

public partial class MainWindow : Window
{
    private readonly LauncherRuntimeContext _runtimeContext;
    private readonly LauncherSettingsService _settingsService = new();
    private readonly AuthService _authService;
    private readonly GameLocatorService _gameLocatorService = new();
    private readonly HookDllService _hookDllService;
    private readonly LauncherConfigurationAvailabilityService _configurationAvailabilityService;
    private readonly GameLaunchCoordinator _gameLaunchCoordinator;
    private readonly ClientComponentUpdateService _componentUpdateService;
    private readonly ComponentUpdateMonitorCoordinator _componentUpdateMonitor;
    private readonly ClientUpdateAnnouncementService _clientUpdateAnnouncementService;
    private readonly ClientUpdateCoordinator _clientUpdateCoordinator;
    private readonly HookConfigService _hookConfigService;
    private readonly SaveGameService _saveGameService = new();
    private readonly DxModeCompatibilityService _dxModeCompatibilityService = new();
    private readonly GameLaunchService _gameLaunchService = new();
    private readonly GameProcessSessionService _gameProcessSessionService = new();
    private readonly FirewallService _firewallService = new();
    private readonly PublicTunnelService _tunnelService;
    private readonly ProcessRouterService _processRouterService = new();
    private readonly ScblTunnelAdapterService _adapterService;
    private readonly LocalClientUpdateService _localUpdateService;
    private readonly AnnouncementService _announcementService;
    private readonly DiagnosticExportService _diagnosticExportService = new();
    private readonly UpdaterBootstrapService _updaterBootstrapService;
    private readonly WinDivertBootstrapService _winDivertBootstrapService;
    private readonly PeerProbeService _peerProbeService = new();
    private readonly BroadcastProbeService _broadcastProbeService = new();
    private readonly ControlPlaneService _controlPlaneService;
    private readonly ServerConnectionProfile _serverProfile;
    private readonly ServerDescriptorClient _serverDescriptorClient;
    private ServerRuntimeSnapshot? _serverRuntime;
    private readonly GameNetworkMonitorService _gameNetworkMonitorService;
    private readonly ControlPlaneSigningSecretProvider _controlPlaneSigningSecretProvider = new();
    private readonly LauncherProcessCleanupService _launcherProcessCleanupService = new();
    private readonly GameRuntimeValidationService _gameRuntimeValidationService = new();
    private readonly MediaPlayer _musicPlayer = new();
    private NetworkOrchestrator _networkOrchestrator = null!;
    private NetworkSessionCoordinator _networkSessionCoordinator = null!;
    private NetworkModeCoordinator _networkModeCoordinator = null!;

    private LauncherSettings _settings = new();
    private string _gameDir = "";
    private string _assignedIp = "";
    private bool _isBusy;
    private bool _isLaunchFlowActive;
    private bool _isUpdating;
    private bool _isGameStarting;
    private bool _isGameRunning;
    private bool _startupRestartAvailable;
    private bool _isEndingGame;
    private bool _settingsLoaded;
    private bool _allowClose;
    private bool _closeCleanupInProgress;
    private readonly SemaphoreSlim _gameLaunchRequestLock = new(1, 1);
    private CancellationTokenSource? _networkCheckCooldownCts;
    private bool _networkCheckButtonCoolingDown;
    private bool _networkCheckInProgress;
    private bool _networkCheckAnimationActive;
    private bool _networkReady;
    private bool _networkShutdownStarted;
    private readonly NetworkReadyRemoteServicesGate _remoteServicesGate = new();
    private ServerStatusKind _serverStatusKind = ServerStatusKind.Unknown;
    private long? _lastServerLatencyMs;
    private string _lastConnectionTransport = "";
    private string _lastConnectionAddressFamily = "";
    private ControlPlaneBootstrapContext? _lastBootstrapContext;
    private bool _serverUsesTcpFallback;
    private TaskCompletionSource<MessageBoxResult>? _dialogTcs;
    private bool _suppressUsernameTextChanged;
    private int _guideIndex;
    private List<GuideStep> _guideSteps = new();
    private string? _musicTempPath;
    private bool _musicPlayedThisSession;
    private bool _launcherMediaFrozenForGameSession;
    private bool _musicIsPlaying;
    private bool _passwordVisible;
    private CancellationTokenSource? _gameNetworkContinuityCts;
    private CancellationTokenSource? _controlPlaneHeartbeatCts;
    private bool _gameLatencyActive;
    private bool _localIsGameHost;
    private string _gamePeerIp = "";
    private long? _lastGameLatencyMs;
    private string _lastGameTransport = "";
    private string _lastGameAddressFamily = "";
    private string _lastGameNextHop = "";
    private int? _lastGameHopCount;
    private int _gameActivePeerCount;
    private string _gameRoleSource = "";
    private string _gameHostUsername = "";
    private long? _gameSessionId;
    private long? _gameLatencyP50Ms;
    private long? _gameLatencyP95Ms;
    private long? _gameJitterMs;
    private double? _gameLossPercent;
    private string _gameSessionVirtualIp = "";
    private int _gameNetworkContinuityIssueLogged;

    private string PublicServerAddress => GetServerVirtualIp();
    private static readonly string LauncherVersion = GetDisplayVersion();


    private enum ServerStatusKind
    {
        Unknown,
        NetworkCreating,
        TunnelConnecting,
        ServerConnecting,
        TunnelReconnecting,
        Normal,
        NetworkFailed,
        TunnelFailed,
        ServerFailed
    }

    private enum FriendlyErrorKind
    {
        Tunnel,
        Server,
        GamePath,
        HookFiles,
        Account,
        General
    }

    private sealed class GuideStep
    {
        public FrameworkElement Target { get; init; } = null!;
        public string TitleZh { get; init; } = "";
        public string TitleEn { get; init; } = "";
        public string MessageZh { get; init; } = "";
        public string MessageEn { get; init; } = "";
    }

    public MainWindow()
    {
        _runtimeContext = new LauncherRuntimeContext(App.ComponentUpdateChannel);
        _localUpdateService = new LocalClientUpdateService(_runtimeContext);
        _updaterBootstrapService = new UpdaterBootstrapService(_runtimeContext);
        _winDivertBootstrapService = new WinDivertBootstrapService(_runtimeContext);
        InitializeComponent();
        _hookDllService = new HookDllService(_runtimeContext);
        _configurationAvailabilityService = new LauncherConfigurationAvailabilityService(_hookDllService);
        InitializeAnnouncementTicker();
        ForceEnglishInputForPlainTextBoxes();
        LoadSettingsToUi();
        _serverProfile = ServerConnectionProfiles.Resolve(_settings, App.ComponentUpdateChannel);
        _serverDescriptorClient = new ServerDescriptorClient(_serverProfile);
        _tunnelService = new PublicTunnelService(GetServerVirtualIp);
        _adapterService = new ScblTunnelAdapterService(GetServerVirtualIp);
        _authService = new AuthService(
            getGrpcAddress: GetDedicatedAuthAddress,
            getConfigServerHost: GetServerVirtualIp);
        _hookConfigService = new HookConfigService(
            GetServerVirtualIp,
            GetDedicatedAuthAddress);
        _controlPlaneService = new ControlPlaneService(
            _runtimeContext,
            getServerAddress: GetServerVirtualIp,
            getServerPort: GetControlPlanePort);
        bool isTestChannel = App.ComponentUpdateChannel == ClientUpdateChannel.Test;
        txtTestChannelBadge.Visibility = isTestChannel ? Visibility.Visible : Visibility.Collapsed;
        Title = isTestChannel ? "SCBL CONNECT（TEST）" : "SCBL CONNECT";
        _componentUpdateService = new ClientComponentUpdateService(
            _runtimeContext,
            getServerDescriptorAsync: RefreshServerDescriptorAsync);
        _componentUpdateMonitor = new ComponentUpdateMonitorCoordinator(
            _componentUpdateService,
            () => _allowClose,
            () => IsGameSessionActiveForNetworkControl() || _isLaunchFlowActive,
            () => _isUpdating || dialogOverlay.Visibility == Visibility.Visible,
            HandleBackgroundComponentUpdatesAsync);
        _clientUpdateAnnouncementService = new ClientUpdateAnnouncementService(
            () => _serverRuntime?.DescriptorSnapshot);
        _gameNetworkMonitorService = new GameNetworkMonitorService(
            _controlPlaneService,
            _tunnelService,
            _peerProbeService,
            () => _isGameStarting || _isGameRunning,
            () => _assignedIp,
            GetControlPlaneSigningSecret,
            GetLauncherBaseDirectory,
            GetCurrentPeerUsername,
            () => _lastPeers,
            () => _lastServerLatencyMs,
            LauncherVersion,
            GetServerVirtualIp);
        _gameNetworkMonitorService.ObservationChanged += (_, observation) =>
            Dispatcher.InvokeAsync(() => ApplyGameNetworkObservation(observation));
        _gameLaunchCoordinator = new GameLaunchCoordinator(
            _gameLocatorService,
            _dxModeCompatibilityService,
            _controlPlaneService,
            _authService,
            _hookDllService,
            _saveGameService,
            _hookConfigService, modSelectionPreparationService: App.ModSelectionPreparation);
        _announcementService = new AnnouncementService(
            () => _serverRuntime?.DescriptorSnapshot);
        _networkOrchestrator = new NetworkOrchestrator(
            _tunnelService,
            _processRouterService,
            _adapterService,
            _firewallService,
            GetLauncherBaseDirectory,
            () => _gameDir,
            GetConfiguredPublicEndpoint,
            GetConfiguredTunnelSecret,
            () => _assignedIp,
            GetEasyTierClientOptions,
            IsGameSessionActiveForNetworkControl,
            (ip, latencyMs) =>
            {
                _assignedIp = ip;
                _settings.LastAssignedVirtualIp = ip;
                _settings.LastServerVirtualIp = GetServerVirtualIp();
                _settings.LastTunnelConnectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (latencyMs.HasValue)
                    _settings.LastLatencyMs = latencyMs.Value;
            },
            () => _settingsService.Save(_settings),
            healthCheckService: new NetworkHealthCheckService(
                GetServerVirtualIp,
                () => _serverRuntime?.DedicatedAuthPort ?? 50051,
                () => _serverRuntime?.DedicatedConfigPort ?? 80,
                GetControlPlanePort));
        _clientUpdateCoordinator = new ClientUpdateCoordinator(
            _componentUpdateService,
            _runtimeContext,
            _clientUpdateAnnouncementService,
            () => _componentUpdateMonitor.Stop(),
            reason => _networkOrchestrator.ShutdownAsync(
                reason,
                preserveTunnelRuntime: false));
        _clientUpdateCoordinator.ProgressChanged += ClientUpdateCoordinator_ProgressChanged;
        _networkOrchestrator.StatusChanged += snapshot =>
        {
            Dispatcher.InvokeAsync(() => ApplyNetworkStatusSnapshot(snapshot));
        };
        _networkSessionCoordinator = new NetworkSessionCoordinator(
            _networkOrchestrator,
            () => _allowClose);
        _networkModeCoordinator = new NetworkModeCoordinator(
            () => ClientNetworkModeNames.ParseOrFast(_settings.NetworkMode),
            mode => _settings.NetworkMode = ClientNetworkModeNames.ToName(mode),
            () => _settingsService.Save(_settings),
            IsGameSessionActiveForNetworkControl,
            token => _networkOrchestrator.EnsureReadyAsync(
                NetworkEnsureMode.Manual,
                "network mode changed",
                token));
        _networkSessionCoordinator.StateChanged += state =>
        {
            Dispatcher.InvokeAsync(() => ApplyNetworkSessionState(state));
        };
        ApplyLocalization();
    }

    private bool IsEnglish => _settings.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);
    private string L(string zh, string en) => IsEnglish ? en : zh;
    private static Brush YellowBrush => Brushes.Goldenrod;
    private static Brush GreenBrush => Brushes.LimeGreen;
    private static Brush RedBrush => Brushes.IndianRed;

    private void SaveSuccessfulLoginCredentials(string username, string password)
    {
        _settings.Username = username.Trim();
        _settings.Password = chkRememberPassword.IsChecked == true ? password : "";
        SaveRuntimeSettings();
        LogService.Info("Login credentials saved for current Windows user.");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() => BringLauncherToFront("launcher loaded"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // The previous updater cannot replace its own executable while it is running.
        // Complete that hand-off before the remote manifest is checked, otherwise the
        // freshly updated launcher can be mistaken for an incomplete same-version repair.
        // Hashing an EXE/SYS on the first cold start can be delayed by disk cache and antivirus;
        // keep those synchronous file operations off the WPF dispatcher.
        SetBusy(true, L("正在准备启动器...", "Preparing launcher..."));
        var bootstrapStopwatch = Stopwatch.StartNew();
        try
        {
            Task<ServerDescriptorSnapshot> descriptorTask = _serverDescriptorClient.GetAsync();
            await Task.WhenAll(
                Task.Run(async () => await _updaterBootstrapService.EnsureCurrentUpdaterAsync().ConfigureAwait(false)),
                Task.Run(async () => await _winDivertBootstrapService.EnsureCurrentDriverAsync().ConfigureAwait(false)),
                descriptorTask);
            ApplyServerDescriptor(await descriptorTask.ConfigureAwait(true));
        }
        catch (ServerDescriptorUnavailableException ex)
        {
            SetBusy(false);
            LogService.Error(ex);
            await ShowInfoDialogAsync(
                L("服务器配置不可用", "Server Configuration Unavailable"),
                L(
                    "无法读取当前服务器配置，请检查服务器地址和网络后重试。",
                    "The selected server configuration could not be loaded. Check the server address and network, then try again."));
            return;
        }
        LogService.Info($"Launcher bootstrap checks completed: elapsedMs={bootstrapStopwatch.ElapsedMilliseconds}");

        // The component manifest is the single source of truth for routine
        // updates. Full packages are reserved for first install and repair.
        if (!await EnsureIndependentComponentUpdatesAsync() || _allowClose)
            return;

        InitializeLocalStateAfterVersionCheck();
        await _configurationAvailabilityService.RefreshModRuntimeObservationAsync();
        SetGameRunningState(false);
        PlayStartupMusicIfEnabled();
        StartComponentUpdateMonitor();

        await HandleFirstRunSaveOverwritePromptAsync();

        // 耗时的进程清理和网络初始化放到后台，避免窗口打开时卡顿。
        _ = Task.Run(() => _launcherProcessCleanupService.CloseOriginalLaunchers("launcher startup"));

        // 网络轻量化总控：启动时先静默复用已有隧道/网卡，能通就直接绿灯；复用失败才进入创建流程。
        _ = StartNetworkLifecycleAsync("launcher loaded");

        if (!_settings.GuideCompleted)
        {
            await Task.Delay(450);
            BringLauncherToFront("show guide");
            ShowGuide(markCompletedOnClose: true);
        }
    }

    private async Task StartNetworkLifecycleAsync(string reason)
    {
        try
        {
            await _networkSessionCoordinator.StartAsync(reason);
            NetworkSessionState state = _networkSessionCoordinator.State;
            if (state.LastResult is { Ok: false } failure)
                await OfferCompatibleModeAfterFastStartupFailureAsync(failure);
        }
        finally
        {
            // The watchdog starts after the one-time Fast-mode recovery decision so an
            // automatic probe cannot race the player's explicit switch to Compatible.
            if (!_allowClose)
                _networkSessionCoordinator.StartWatchdog();
        }
    }

    private async Task<bool> EnsurePublicNetworkOrchestratedAsync(bool showFailureDialog, string reason)
    {
        var mode = showFailureDialog ? NetworkEnsureMode.Manual : NetworkEnsureMode.Automatic;
        // Manual checks can enter synchronous adapter/profile probes before the
        // orchestrator reaches its first asynchronous wait. Run that core work
        // off the dispatcher so the window and check animation keep rendering.
        var result = await _networkSessionCoordinator.EnsureReadyAsync(
            mode,
            reason,
            runCoreOffCallingContext: showFailureDialog);
        ApplyNetworkSessionState(_networkSessionCoordinator.State);
        if (result.Ok)
        {
            return true;
        }

        if (showFailureDialog)
            await ShowNetworkFailureDialogAsync(result);
        return false;
    }

    private void ApplyNetworkSessionState(NetworkSessionState state)
    {
        _networkReady = state.IsReady;
        _assignedIp = state.AssignedIp;
        _lastServerLatencyMs = state.LatencyMs;
        UpdateLaunchButtonAvailability();
        TryStartRemoteClientServices(state.IsReady);
    }

    private void TryStartRemoteClientServices(bool isReady)
    {
        if (_remoteServicesGate.TryStart(isReady, _allowClose))
            _ = CheckRemoteClientServicesAfterNetworkAsync();
    }

    private async Task CheckRemoteClientServicesAfterNetworkAsync()
    {
        if (_networkReady && !_allowClose)
            await CheckRemoteAnnouncementsAfterNetworkAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        // Closing 是同步事件。只要后续存在 await，就必须先阻止窗口被系统直接销毁，
        // 否则 OnClosed 可能在网络/DLL 清理完成前提前 Dispose 服务。
        e.Cancel = true;
        if (_closeCleanupInProgress)
            return;

        _closeCleanupInProgress = true;
        bool closeAfterCleanup = false;
        try
        {
            if (_isGameStarting)
            {
                var result = await ShowConfirmDialogAsync(
                    title: L("游戏正在启动", "Game Is Starting"),
                    message: L("游戏正在启动，是否取消并关闭启动器？", "The game is starting. Cancel and close the launcher?"),
                    yesText: L("取消等待", "Cancel Waiting"),
                    noText: L("返回", "Back"));
                if (result != MessageBoxResult.Yes)
                    return;
                await EndRunningGameAsync("launcher closing during startup");
            }
            else if (_isGameRunning)
            {
                var result = await ShowConfirmDialogAsync(
                    title: L("游戏仍在运行", "Game Is Running"),
                    message: L("游戏仍在运行。关闭启动器前是否结束游戏？", "The game is still running. Stop it before closing the launcher?"),
                    yesText: L("结束游戏", "End Game"),
                    noText: L("取消", "Cancel"));
                if (result != MessageBoxResult.Yes)
                    return;
                await EndRunningGameAsync("launcher closing");
            }

            closeAfterCleanup = true;
            // Visually close immediately; runtime cleanup continues for only a short bounded window.
            // This avoids the previous one-second frozen-window feeling on ordinary exit.
            Visibility = Visibility.Collapsed;
            ShowInTaskbar = false;
            StopMusic();
            LogService.Info("Launcher close cleanup started.");
            _networkShutdownStarted = true;
            _networkCheckCooldownCts?.Cancel();
            _peerRefreshCts?.Cancel();
            _controlPlaneHeartbeatCts?.Cancel();
            _announcementRefreshCts?.Cancel();
            _componentUpdateMonitor.Stop();
            _peerProbeService.Stop();
            _broadcastProbeService.Dispose();
            if (!_isGameRunning && !_isGameStarting)
            {
                _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
                _hookDllService.RestoreOriginalDllBestEffort(_gameDir);
            }
            await _networkSessionCoordinator.ShutdownAsync("launcher closing", preserveTunnelRuntime: true);
            LogService.Info("Launcher close cleanup completed.");
        }
        catch (Exception ex)
        {
            // 清理异常不能把窗口永久卡在无法关闭的状态。
            closeAfterCleanup = true;
            LogService.Error("Launcher close cleanup failed: " + ex);
        }
        finally
        {
            _closeCleanupInProgress = false;
            if (closeAfterCleanup)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _networkShutdownStarted = true;
        CancelGameMonitor();
        _networkCheckCooldownCts?.Cancel();
        _networkCheckCooldownCts?.Dispose();
        _networkCheckCooldownCts = null;
        _peerRefreshCts?.Cancel();
        _peerRefreshCts?.Dispose();
        _peerRefreshCts = null;
        _controlPlaneHeartbeatCts?.Cancel();
        _controlPlaneHeartbeatCts?.Dispose();
        _controlPlaneHeartbeatCts = null;
        _announcementRefreshCts?.Cancel();
        _announcementRefreshCts?.Dispose();
        _announcementRefreshCts = null;
        _componentUpdateMonitor.Dispose();
        _announcementScrollClock?.Controller?.Stop();
        _announcementScrollClock = null;
        StopGameLatencyMonitor();
        StopGameNetworkContinuityMonitor();
        _peerProbeService.Dispose();
        _broadcastProbeService.Dispose();
        _controlPlaneService.Dispose();
        _gameLaunchRequestLock.Dispose();
        _networkOrchestrator.Dispose();
        _processRouterService.Dispose();
        // v0.5.0：普通关闭停止 EasyTier/route-guard 运行时，完整网卡删除仅由“修复网络”执行。
        // Window_Closing 已调用 NetworkOrchestrator.ShutdownAsync 进行“保留运行时”的轻量关闭。
        StopMusic();
        base.OnClosed(e);
    }

    private async Task HandleFirstRunSaveOverwritePromptAsync()
        => await ConfirmAndOverwriteFullUnlockSavesAsync(firstRun: true);

    private async void CheckNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _networkCheckButtonCoolingDown)
            return;

        BeginNetworkCheckButtonCooldown();
        _networkCheckInProgress = true;
        SetBusy(true);
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            // 手动检测也走网络总控。能复用就不重建，失败才给出阶段化弹窗。
            await EnsurePublicNetworkOrchestratedAsync(showFailureDialog: true, reason: "manual check");
        }
        finally
        {
            _networkCheckInProgress = false;
            SetBusy(false);
        }
    }

    private async Task<bool> EnsureNetworkReadyBeforeLaunchAsync()
    {
        var result = await _networkOrchestrator.EnsureReadyAsync(NetworkEnsureMode.BeforeLaunch, "before launch");
        _networkReady = result.Ok;
        if (result.Ok)
        {
            _assignedIp = result.AssignedIp;
            _lastServerLatencyMs = result.LatencyMs;
            return true;
        }

        await ShowNetworkFailureDialogAsync(result);
        return false;
    }


    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _gameLaunchRequestLock.WaitAsync(0))
        {
            LogService.Info("Launch request ignored because another launch/restart request is already being handled.");
            return;
        }

        try
        {
            if (_isUpdating)
            {
                LogService.Info("Launch request ignored because a client update is in progress.");
                return;
            }

            if (_isGameRunning)
            {
                await EndRunningGameWithConfirmAsync();
                return;
            }

            if (_isGameStarting)
            {
                var result = await ShowConfirmDialogAsync(
                    title: L("是否重新启动游戏", "Restart the Game?"),
                    message: L(
                        "游戏正在启动。是否终止并重新启动？",
                        "The game is starting. Stop it and restart?"),
                    yesText: L("重新启动", "Restart"),
                    noText: L("继续等待", "Keep Waiting"));
                if (result != MessageBoxResult.Yes)
                    return;

                await EndRunningGameAsync("restart requested during startup");
                await Task.Delay(300);
            }

            await HandleFirstRunFovPromptAsync();
            if (_allowClose)
                return;

            if (!await EnsureLatestComponentsBeforeLaunchAsync())
                return;

            await RunLaunchFlowAsync();
        }
        finally
        {
            _gameLaunchRequestLock.Release();
        }
    }

    private async Task RunLaunchFlowAsync()
    {
        if (_isBusy || _isUpdating || _isLaunchFlowActive)
        {
            LogService.Info($"Launch flow suppressed: busy={_isBusy}, updating={_isUpdating}, active={_isLaunchFlowActive}.");
            return;
        }

        _isLaunchFlowActive = true;
        SetBusy(true, L("准备启动...", "Preparing..."));
        await Task.Yield();
        try
        {
            string username = txtUsername.Text.Trim();
            string password = GetPasswordInput();
            if (string.IsNullOrWhiteSpace(username))
                throw new Exception(L("请先填写账号。", "Please enter your username."));
            if (string.IsNullOrWhiteSpace(password))
                throw new Exception(L("请先填写密码。", "Please enter your password."));

            EnsureGameDirectoryReady();
            string selectedExecutable = GetSelectedGameExecutable();
            LogService.Info($"Launch flow selected: mode={(selectedExecutable.Equals("Blacklist_game.exe", StringComparison.OrdinalIgnoreCase) ? "DX9" : "DX11")}, executable={selectedExecutable}, gameDir={_gameDir}");

            string gameplayMode = GetSelectedGameplayMode();
            var request = new GameLaunchRequest(
                username,
                password,
                _gameDir,
                selectedExecutable,
                gameplayMode,
                LauncherVersion,
                GetConfiguredTunnelSecret(), _settings.ModFovScale);
            var callbacks = new GameLaunchCallbacks(
                SaveRuntimeSettings,
                ReportGameLaunchStage,
                () => Task.Run(() => _launcherProcessCleanupService.CloseOriginalLaunchers("before launch")),
                EnsureNetworkReadyForCoordinatorAsync,
                EnsureDynamicNetworkReadyForGameAsync,
                ApplyBootstrapContextOrThrow,
                context => EnsureAccountReadyAsync(
                    context.Username,
                    context.Password,
                    context.AccountId,
                    context.BindIp,
                    context.InitialLogin,
                    context.AccountKnownMissing),
                bindIp => _ = RunBackgroundVirtualLanDiagnosticsAsync(bindIp),
                StartGameAndMonitorAsync);

            await _gameLaunchCoordinator.ExecuteAsync(request, callbacks);
        }
        catch (OperationCanceledException)
        {
            LogService.Info("Launch cancelled by user.");
            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
            SetGameRunningState(false);
        }
        catch (Exception ex)
        {
            LogService.Error($"Launch failed: {ex}");
            _dxModeCompatibilityService.RestoreAfterGameExit(_gameDir);
            await ShowFriendlyErrorDialogAsync(ClassifyLaunchError(ex), ex);
            SetGameRunningState(false);
        }
        finally
        {
            _isLaunchFlowActive = false;
            SetBusy(false);
        }
    }

    private bool IsGameSessionActiveForNetworkControl()
        => _isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning();

    private async Task EnsureDynamicNetworkReadyForGameAsync(string bindIp)
    {
        if (!PublicTunnelConfig.IsScblClientIp(bindIp))
            throw new InvalidOperationException(L("EasyTier没有获得有效动态虚拟IP。", "EasyTier has no valid dynamic virtual IP."));

        if (!_tunnelService.ValidateDynamicDhcpConfig(out string configMessage))
            throw new InvalidOperationException(L("EasyTier动态IP配置检查失败：", "EasyTier DHCP configuration check failed: ") + configMessage);

        bool stable = await _tunnelService.VerifyAssignedIpStableAsync(
            GetLauncherBaseDirectory(),
            bindIp,
            TimeSpan.FromSeconds(1.3));
        if (!stable && !_tunnelService.IsRunning && !_tunnelService.HasRunningTunnelClientProcess())
            throw new InvalidOperationException(L(
                "EasyTier动态虚拟IP尚未稳定，已阻止启动游戏。请稍后重试。",
                "The EasyTier dynamic virtual IP is not stable yet. Game launch was blocked; please retry shortly."));

        int interfaceIndex = _adapterService.GetInterfaceIndexForIp(bindIp);
        if (interfaceIndex <= 0)
            throw new InvalidOperationException(L(
                "没有找到当前EasyTier虚拟IP对应的网卡路由。",
                "No adapter route was found for the current EasyTier virtual IP."));

        if (!stable)
        {
            // The CLI portal can briefly be busy while first-run account and peer discovery
            // requests run in parallel. The live process and OS adapter checks remain strict.
            LogService.Warning($"EasyTier assigned-IP stability sampling was inconclusive; continuing because the tunnel process and OS adapter are healthy. ip={bindIp}, ifIndex={interfaceIndex}");
        }

        EasyTierBroadcastRelayStatus broadcast = _tunnelService.GetUdpBroadcastRelayStatus();
        if (!broadcast.Enabled)
            throw new InvalidOperationException(L(
                "UDP广播中继未启用，游戏可能无法搜索局域网房间。",
                "UDP broadcast relay is disabled, so LAN room discovery may fail."));
        if (broadcast.ExplicitFailure)
            throw new InvalidOperationException(L(
                "UDP广播中继启动失败，已阻止启动游戏：",
                "UDP broadcast relay failed to start; game launch was blocked: ") + broadcast.Message);

        if (broadcast.Degraded)
            LogService.Warning("EasyTier UDP broadcast relay is using a fallback capture backend: " + broadcast.Message);
        else if (broadcast.Confirmed)
            LogService.Info("EasyTier UDP broadcast relay confirmed ready.");
        else
            LogService.Info("EasyTier UDP broadcast relay is configured and no startup failure was observed.");

        LogService.Info($"Dynamic virtual LAN fast preflight passed. ip={bindIp}, ifIndex={interfaceIndex}, addressing=dhcp, stabilityConfirmed={stable}, broadcastEnabled={broadcast.Enabled}, broadcastConfirmed={broadcast.Confirmed}, broadcastDegraded={broadcast.Degraded}");
    }

    private void ApplyBootstrapContextOrThrow(ControlPlaneBootstrapContext? bootstrap)
    {
        if (bootstrap == null)
        {
            LogService.Info("SCBL control plane is unavailable; continuing with direct gRPC and local checks.");
            return;
        }

        _lastBootstrapContext = bootstrap;
        if (bootstrap.Maintenance)
            throw new InvalidOperationException(L("服务器当前处于维护状态，请稍后再试。", "The server is currently under maintenance. Please try again later."));
        if (!bootstrap.ClientVersionAccepted || bootstrap.UpdateRequired)
        {
            throw new InvalidOperationException(L(
                $"客户端版本与服务器当前版本不一致。请重新打开启动器并完成 v{bootstrap.RequiredClientVersion} 更新。",
                $"The client version does not match the server. Reopen the launcher and complete the v{bootstrap.RequiredClientVersion} update."));
        }

        if (bootstrap.Health.Overall.Equals("down", StringComparison.OrdinalIgnoreCase))
            LogService.Warning("Control plane reports the server as down; direct account/network checks will determine whether launch can continue.");
        else if (bootstrap.Health.Overall.Equals("degraded", StringComparison.OrdinalIgnoreCase))
            LogService.Warning("Control plane reports degraded server health.");

        ControlPlaneCapabilities capabilities = bootstrap.Capabilities;
        if (!string.IsNullOrWhiteSpace(capabilities.VirtualSubnet)
            && !capabilities.VirtualSubnet.Equals(PublicTunnelConfig.VirtualNetworkCidr, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warning($"Server/client virtual subnet mismatch: server={capabilities.VirtualSubnet}, client={PublicTunnelConfig.VirtualNetworkCidr}.");
        }
        if (capabilities.Mtu > 0 && capabilities.Mtu != PublicTunnelConfig.Mtu)
            LogService.Warning($"Server/client MTU mismatch: server={capabilities.Mtu}, client={PublicTunnelConfig.Mtu}.");

        LogService.Info($"Control plane bootstrap: serverTool={bootstrap.ServerToolVersion}, requiredClient={bootstrap.RequiredClientVersion}, online={bootstrap.OnlineCount}, accountExists={bootstrap.AccountExists?.ToString() ?? "unknown"}, health={bootstrap.Health.Overall}.");
    }

    private async Task RunBackgroundVirtualLanDiagnosticsAsync(string bindIp)
    {
        string username = GetCurrentPeerUsername();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ControlPlanePeersResponse? registry = await _controlPlaneService.GetPeersAsync(
                bindIp,
                GetControlPlaneSigningSecret(),
                cts.Token).ConfigureAwait(false);

            int knownRemotePeers;
            string source;
            if (registry != null)
            {
                knownRemotePeers = registry.Peers.Count(p => PublicTunnelConfig.IsScblClientIp(p.VirtualIp)
                    && !p.VirtualIp.Equals(bindIp, StringComparison.OrdinalIgnoreCase));
                source = "server-registry";
            }
            else
            {
                IReadOnlyList<string> routes = await _tunnelService.ListVirtualPeerIpsAsync(
                    GetLauncherBaseDirectory(),
                    TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
                knownRemotePeers = routes.Count(ip => !ip.Equals(bindIp, StringComparison.OrdinalIgnoreCase));
                source = "local-route-fallback";
            }

            _broadcastProbeService.StartOrUpdate(bindIp, username);
            BroadcastProbeResult probe = await _broadcastProbeService.ProbeAsync(
                bindIp,
                username,
                knownRemotePeers,
                TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
            if (knownRemotePeers > 0 && probe.Responders.Count == 0)
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
                probe = await _broadcastProbeService.ProbeAsync(
                    bindIp,
                    username,
                    knownRemotePeers,
                    TimeSpan.FromMilliseconds(1100)).ConfigureAwait(false);
            }

            if (knownRemotePeers > 0 && probe.Responders.Count == 0)
                LogService.Warning($"Background UDP broadcast coverage check failed. source={source}, expectedPeers={knownRemotePeers}, message={probe.Message}");
            else
                LogService.Info($"Background virtual LAN diagnostics completed. source={source}, expectedPeers={knownRemotePeers}, broadcastResponders={probe.Responders.Count}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Info("Background virtual LAN diagnostics skipped: " + ex.Message);
        }
    }

    private void StartControlPlaneHeartbeat()
    {
        if (_controlPlaneHeartbeatCts != null || !PublicTunnelConfig.IsScblClientIp(_assignedIp))
            return;

        _controlPlaneHeartbeatCts = new CancellationTokenSource();
        CancellationToken token = _controlPlaneHeartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            int consecutiveFailures = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string bindIp = _assignedIp;
                    if (PublicTunnelConfig.IsScblClientIp(bindIp))
                    {
                        ControlPlaneHeartbeat heartbeat = await Dispatcher.InvokeAsync(BuildControlPlaneHeartbeat);
                        bool ok = await _controlPlaneService.SendHeartbeatAsync(
                            heartbeat,
                            bindIp,
                            GetControlPlaneSigningSecret(),
                            token).ConfigureAwait(false);
                        consecutiveFailures = ok ? 0 : consecutiveFailures + 1;
                        if (!ok && consecutiveFailures == 3)
                            LogService.Info("Control plane heartbeat is unavailable; local EasyTier gameplay is unaffected and merged route discovery remains enabled.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Info("Control plane heartbeat skipped: " + ex.Message);
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private ControlPlaneHeartbeat BuildControlPlaneHeartbeat()
    {
        string role = !_isGameStarting && !_isGameRunning
            ? "idle"
            : _localIsGameHost ? "host"
            : !string.IsNullOrWhiteSpace(_gamePeerIp) ? "client"
            : "running";
        return new ControlPlaneHeartbeat
        {
            Username = GetCurrentPeerUsername(),
            VirtualIp = _assignedIp,
            InstanceId = _settings.EasyTierInstanceId,
            ClientVersion = LauncherVersion,
            EasyTierVersion = PublicTunnelConfig.EasyTierVersion,
            GameplayMode = GetSelectedGameplayMode(),
            GameplayIntegrityValid = true,
            GameRunning = _isGameStarting || _isGameRunning,
            GameRole = role,
            GamePeerIp = _gamePeerIp,
            ServerLatencyMs = _lastServerLatencyMs,
            ServerTransport = _lastConnectionTransport,
            ServerAddressFamily = _lastConnectionAddressFamily,
            GameLatencyMs = _lastGameLatencyMs,
            GameTransport = _lastGameTransport,
            GameAddressFamily = _lastGameAddressFamily,
            NextHop = _lastGameNextHop,
            HopCount = _lastGameHopCount,
            GameLatencyP50Ms = _gameLatencyP50Ms,
            GameLatencyP95Ms = _gameLatencyP95Ms,
            GameJitterMs = _gameJitterMs,
            GameLossPercent = _gameLossPercent
        };
    }

    private void StartGameNetworkContinuityMonitor()
    {
        string sessionIp = PublicTunnelConfig.IsScblClientIp(_gameSessionVirtualIp)
            ? _gameSessionVirtualIp
            : _assignedIp;
        if (!PublicTunnelConfig.IsScblClientIp(sessionIp))
            return;

        StopGameNetworkContinuityMonitor();
        _gameSessionVirtualIp = sessionIp;
        _gameNetworkContinuityCts = new CancellationTokenSource();
        CancellationToken token = _gameNetworkContinuityCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                        break;

                    string currentIp = _tunnelService.ReadAssignedIp();
                    bool runtimePresent = _tunnelService.HasContinuousRuntimePresence();
                    if (runtimePresent && currentIp.Equals(sessionIp, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Interlocked.Exchange(ref _gameNetworkContinuityIssueLogged, 1) == 0)
                    {
                        string detail = !runtimePresent
                            ? "EasyTier runtime stopped while the game was active."
                            : $"EasyTier virtual IP changed during the game: session={sessionIp}, current={currentIp}.";
                        LogService.Error(detail + " Automatic network restart remains suppressed to avoid further damaging the room session.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Warning("Game network continuity monitor failed: " + ex.Message);
                }
            }
        }, token);
    }

    private void StopGameNetworkContinuityMonitor()
    {
        try { _gameNetworkContinuityCts?.Cancel(); } catch { }
        _gameNetworkContinuityCts?.Dispose();
        _gameNetworkContinuityCts = null;
        _gameSessionVirtualIp = "";
        Interlocked.Exchange(ref _gameNetworkContinuityIssueLogged, 0);
    }

    private async Task EnsureAccountReadyAsync(
        string username,
        string password,
        string accountId,
        string bindIp,
        LoginResult? initialLogin = null,
        bool accountKnownMissing = false)
    {
        var login = initialLogin ?? (accountKnownMissing
            ? new LoginResult { Status = LoginStatus.UserNotFound, Message = "账号不存在。" }
            : await _authService.LoginPublicAsync(username, password, bindIp));
        if (login.Status == LoginStatus.Success)
        {
            LogService.Info($"Login succeeded: {username}");
            SaveSuccessfulLoginCredentials(username, password);
            return;
        }

        if (login.Status == LoginStatus.InvalidPassword || LooksLikePasswordError(login.Message))
            throw new Exception(L("密码不正确，请使用该账号原来的密码。", "Incorrect password. Use the existing password for this account."));

        if (login.Status == LoginStatus.UserNotFound || LooksLikeUserNotFound(login.Message))
        {
            var confirmRegister = await ShowConfirmDialogAsync(
                title: L("账号不存在", "Account Not Found"),
                message: L($"账号“{username}”未注册。是否现在注册并启动？", $"Account '{username}' is not registered. Register and launch now?"),
                yesText: L("注册并启动", "Register and Launch"),
                noText: L("取消", "Cancel"));
            if (confirmRegister != MessageBoxResult.Yes)
                throw new OperationCanceledException("Account registration cancelled by user.");

            LogService.Info($"Account not found, registering: {username}");
            var register = await _authService.RegisterPublicAsync(username, password, accountId, bindIp);
            if (register.Status != RegisterStatus.Success && register.Status != RegisterStatus.AlreadyExists)
                throw new Exception(register.Message);
            if (register.Status == RegisterStatus.Success && !string.IsNullOrWhiteSpace(register.RecoveryCode))
                await StoreAndShowRecoveryCodeAsync(username, register.RecoveryCode, isReplacement: false);

            login = await _authService.LoginPublicAsync(username, password, bindIp);
            if (login.Status != LoginStatus.Success)
                throw new Exception(login.Message);
            SaveSuccessfulLoginCredentials(username, password);
            return;
        }

        throw new Exception(login.Message);
    }

    private static bool LooksLikeUserNotFound(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("不存在", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePasswordError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("密码", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureGameDirectoryReady()
    {
        if (_gameLocatorService.IsValidGameDirectory(_gameDir))
            return;

        var dialog = new OpenFileDialog
        {
            Title = L("选择游戏 SYSTEM 目录中的游戏程序", "Select the game executable in the SYSTEM folder"),
            Filter = "Blacklist game|Blacklist_DX11_game.exe;Blacklist_game.exe|Executable|*.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            throw new OperationCanceledException("Game folder selection cancelled by user.");

        string dir = Path.GetDirectoryName(dialog.FileName) ?? "";
        if (!_gameLocatorService.IsValidGameDirectory(dir))
            throw new Exception(L("选择的目录不是有效的游戏 SYSTEM 目录。", "The selected folder is not a valid game SYSTEM directory."));

        _gameDir = dir;
        _settings.GameDirectory = dir;
        _settingsService.Save(_settings);
        LogService.Info($"User selected game directory: {_gameDir}");
    }

    private async Task StartGameAndMonitorAsync(string gameExecutable)
    {
        string gamePath = Path.Combine(_gameDir, gameExecutable);
        string hookPath = Path.Combine(_gameDir, "uplay_r1_loader.dll");
        DateTime launchSessionStartedUtc = DateTime.UtcNow;
        HashSet<int> preExistingMatchingPids = _gameProcessSessionService.CaptureExistingMatchingProcessIds(gamePath);

        // DX9 的 dxgi.dll 切换必须紧贴 Process.Start，避免网络登录、组件部署期间
        // 被原版启动器、覆盖层或同步工具重新写回。该方法内部会执行最终强校验。
        _dxModeCompatibilityService.PrepareImmediatelyBeforeProcessStart(_gameDir, gameExecutable);
        _gameSessionVirtualIp = _assignedIp;
        _gameNetworkContinuityIssueLogged = 0;
        using GameLaunchService.SuspendedGameProcess suspended = _gameLaunchService.StartGameSuspended(_gameDir, gameExecutable);
        Process process = suspended.Process;
        _gameProcess = process;
        lock (_gameProcessSync)
        {
            _launcherOwnedGamePids.Clear();
            _launcherOwnedGamePids.Add(process.Id);
        }

        try
        {
            if (_settings.ForceGameVirtualAdapter)
            {
                int interfaceIndex = _adapterService.GetInterfaceIndexForIp(_assignedIp);
                if (interfaceIndex <= 0)
                    throw new InvalidOperationException("未找到 EasyTier 虚拟网卡接口，无法启动游戏严格导流。");
                await _processRouterService.EnsureStartedAsync(
                    GetLauncherBaseDirectory(),
                    _assignedIp,
                    TimeSpan.FromSeconds(6),
                    interfaceIndex,
                    new[] { process.Id },
                    allowEmptyGamePidsDuringStartup: true);
            }
            else
            {
                _processRouterService.Stop("strict game routing disabled");
            }
            suspended.Resume();
            PauseMusicForGameSession();
        }
        catch
        {
            _processRouterService.Stop("route guard failed before suspended game resume");
            ClearLauncherOwnedGameTracking();
            throw;
        }

        SetGameStartingState(gameExecutable);
        StartGameMonitor(
            gameExecutable,
            gamePath,
            hookPath,
            process.Id,
            launchSessionStartedUtc,
            preExistingMatchingPids);
    }

    private void StartGameLatencyMonitor()
        => _gameNetworkMonitorService.Start();

    private void StopGameLatencyMonitor()
        => _gameNetworkMonitorService.Stop();

    private static string GetDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0].Trim();

        var version = assembly.GetName().Version;
        return version == null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

}
