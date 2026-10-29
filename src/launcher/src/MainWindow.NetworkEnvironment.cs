using SplinterCellCNLauncher.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private NetworkEnvironmentController? _networkEnvironment;
    private Task _networkEnvironmentInitialization = Task.CompletedTask;
    private LanEnvironmentLease? _activeLanEnvironmentLease;
    private volatile bool _ownedGameLaunchActive;
    private bool _networkEnvironmentChanging;
    private bool _networkEnvironmentClosing;
    private bool _restoreJoinAddressAfterHost;
    private bool _renderingNetworkEnvironment;
    private bool _publicSettingsSaving;
    private PublicNetworkEnvironmentAdapter? _publicNetworkAdapter;
    private NetworkEnvironmentKind SelectedNetworkEnvironment => NetworkEnvironmentPreferences.Environment(_settings.NetworkEnvironment);

    private void InitializeNetworkEnvironment()
    {
        Dispatcher.VerifyAccess();
        _publicNetworkAdapter ??= new PublicNetworkEnvironmentAdapter(() => _settings);
        _networkEnvironment ??= new NetworkEnvironmentController(new LauncherLanAdapter(this),
            () => new NetworkEnvironmentActivity(_isGameRunning, _isGameStarting || _ownedGameLaunchActive), _publicNetworkAdapter);
    }

    private async void NetworkEnvironment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingNetworkEnvironment || !IsLoaded || App.PreviewMode || _networkEnvironmentChanging) return;
        if (_isBusy || _toolsOperation || _publicSettingsSaving || _statusRefreshRunning || _networkEnvironmentClosing)
        { RenderNetworkEnvironment(); return; }
        try
        {
            var mode = NetworkEnvironmentPreferences.Environment(cmbNetworkEnvironment.SelectedValue as string);
            var result = await ChangeSelectedNetworkEnvironmentAsync(mode);
            if (result.Outcome is not (NetworkEnvironmentChangeOutcome.Applied or NetworkEnvironmentChangeOutcome.Unchanged))
                await ShowInfoDialogAsync(L("未能切换模式", "Mode Unchanged"),
                    L("当前游戏或网络会话尚未停止，或设置未能保存。请稍后重试。", "The current game/network session has not stopped, or the preference could not be saved. Retry after it closes."));
        }
        catch (Exception) { LogService.Error("Network environment selection failed without publishing readiness."); RenderNetworkEnvironment(); }
    }

    private Task<NetworkEnvironmentChangeResult> ChangeSelectedNetworkEnvironmentAsync(NetworkEnvironmentKind mode, bool persist = true)
        => ChangeNetworkSelectionAsync(mode == NetworkEnvironmentKind.Lan ? NetworkEnvironmentSelection.ForLan(LanRole.Join)
            : NetworkEnvironmentSelection.ForPublic(NetworkEnvironmentPreferences.Transport(_settings.NetworkMode)), persist,
            requestLocalDiscovery: persist);

    private async Task<NetworkEnvironmentChangeResult> ChangeNetworkSelectionAsync(NetworkEnvironmentSelection requested, bool persist,
        bool preserveLocalConfirmation = false, bool requestLocalDiscovery = false)
    {
        Dispatcher.VerifyAccess(); InitializeNetworkEnvironment();
        _ownedGameLaunchActive = _gameLaunchService.SessionId.HasValue;
        if (_networkEnvironmentChanging) return new(NetworkEnvironmentChangeOutcome.ConnectionFailed, NetworkEnvironmentRollback.NotNeeded, requested, _networkEnvironment!.Snapshot);
        string previousPreference = _settings.NetworkEnvironment;
        var previousSelection = _networkEnvironment!.Snapshot.Selection;
        bool beginLocalDiscovery = false;
        if (preserveLocalConfirmation && (requested.Environment != NetworkEnvironmentKind.Lan || requested.LanRole != LanRole.Join))
            throw new ArgumentException("Only an already-confirmed LAN join may retain its discovery generation.");
        _networkEnvironmentChanging = true;
        if (!preserveLocalConfirmation) CancelLocalDiscovery();
        RenderNetworkEnvironment();
        try
        {
            var result = await _networkEnvironment.ChangeAsync(requested, _windowOneShotCts.Token);
            if (result.Outcome is not (NetworkEnvironmentChangeOutcome.Applied or NetworkEnvironmentChangeOutcome.Unchanged)) return result;
            _settings.NetworkEnvironment = requested.Environment.ToString();
            try { if (persist) _settingsService.Save(_settings); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _settings.NetworkEnvironment = previousPreference;
                var restore = previousSelection ?? (NetworkEnvironmentPreferences.Environment(previousPreference) == NetworkEnvironmentKind.Lan
                    ? NetworkEnvironmentSelection.ForLan(LanRole.Join) : NetworkEnvironmentSelection.ForPublic(NetworkEnvironmentPreferences.Transport(_settings.NetworkMode)));
                var rollback = await _networkEnvironment.ChangeAsync(restore, _windowOneShotCts.Token);
                return new(NetworkEnvironmentChangeOutcome.ConnectionFailed,
                    rollback.Outcome is NetworkEnvironmentChangeOutcome.Applied or NetworkEnvironmentChangeOutcome.Unchanged ? NetworkEnvironmentRollback.Restored : NetworkEnvironmentRollback.Failed,
                    requested, _networkEnvironment.Snapshot);
            }
            if (requested.Environment == NetworkEnvironmentKind.Public)
            {
                await _hostDiscovery.StopAsync(); await _lanMonitor.StopAsync();
                SetCredentialEnvironment(CredentialEnvironmentContext.UnconfiguredPublic);
                RevertConnectionModeSelection(0);
                SetServerAddressProgrammatically(_settings.PublicEndpoint, null, resetStatus: false);
                _lastLanLocalReady = false; _lastServerReachable = false;
            }
            else
            {
                SetCredentialEnvironment(CredentialEnvironmentContext.Lan);
                if (NetworkEnvironmentPreferences.Environment(previousPreference) != NetworkEnvironmentKind.Lan)
                    SetServerAddressProgrammatically(_settings.ServerAddress, null, resetStatus: false);
                if (!App.PreviewMode) { StartLanMonitor(); StartHostDiscoveryListener(); }
                beginLocalDiscovery = requestLocalDiscovery && previousSelection?.Environment != NetworkEnvironmentKind.Lan && !preserveLocalConfirmation;
            }
            return result;
        }
        finally
        {
            _networkEnvironmentChanging = false; RenderNetworkEnvironment();
            if (beginLocalDiscovery && IsLoaded && !App.PreviewMode && SelectedNetworkEnvironment == NetworkEnvironmentKind.Lan
                && _networkEnvironment.Snapshot.Selection?.LanRole == LanRole.Join && !_closingBackgroundTasks)
                _ = BeginLocalDiscoveryAsync();
        }
    }

    private string GetPublicReadinessMessage()
    {
        InitializeNetworkEnvironment();
        return _publicNetworkAdapter!.CurrentReadiness.Stage switch
        {
            PublicReadinessStage.ServerConfigurationRequired => L("请在服务器设置中填写公网地址和更新端口。", "Configure the public endpoint and update port in Server Settings."),
            PublicReadinessStage.DeploymentConfigurationRequired => L("公网地址已保存；还需导入经审查的部署配置（服务身份和组件校验信息）。", "Endpoint saved; import a reviewed deployment configuration with service identity and component hashes."),
            PublicReadinessStage.DeploymentConfigurationInvalid => L("部署配置无法读取、格式无效，或与当前服务器/传输模式不一致。", "The deployment configuration is unavailable, invalid, or does not match the server/transport."),
            PublicReadinessStage.RuntimeInputsInvalid => L("部署配置已读取，但所需公网组件缺失或校验不匹配。", "Deployment configuration loaded; required public components are missing or fail verification."),
            _ => L("服务器和组件配置已校验；Windows 公网连接的安全启动、专用网卡及游戏会话验证尚未完成，暂不能启动公网游戏。", "Server and component configuration verified; Windows public connection startup, owned adapter and game-session admission are not yet complete. Public game launch remains unavailable.")
        };
    }

    private async Task<bool> ApplyPublicServerSettingsAsync(ValidatedPublicServerSettings stable,
        ValidatedPublicServerSettings test, PublicTransport transport, string? configurationPath = null)
    {
        if (_publicSettingsSaving || _isBusy || _isGameRunning || _isGameStarting || _gameLaunchService.SessionId.HasValue
            || _networkEnvironmentChanging || _closingBackgroundTasks) return false;
        _publicSettingsSaving = true;
        RenderNetworkEnvironment();
        try
        {
        if (!Enum.IsDefined(transport)) return false;
        if (!string.IsNullOrWhiteSpace(configurationPath))
        {
            try { _ = PublicDeploymentConfiguration.Load(configurationPath); }
            catch (IOException) { return false; }
        }
        var previous = (_settings.PublicEndpoint, _settings.PublicUpdatePort, _settings.TestPublicEndpoint,
            _settings.TestPublicUpdatePort, _settings.NetworkMode, _settings.PublicDeploymentConfigurationPath);
        bool saved = TryPersistInteractiveSetting(() =>
        {
            _settings.PublicEndpoint = stable.PublicEndpoint; _settings.PublicUpdatePort = stable.UpdatePort;
            _settings.TestPublicEndpoint = test.PublicEndpoint; _settings.TestPublicUpdatePort = test.UpdatePort;
            _settings.NetworkMode = transport.ToString(); _settings.PublicDeploymentConfigurationPath = configurationPath?.Trim() ?? "";
        }, () => (_settings.PublicEndpoint, _settings.PublicUpdatePort, _settings.TestPublicEndpoint,
            _settings.TestPublicUpdatePort, _settings.NetworkMode, _settings.PublicDeploymentConfigurationPath) = previous);
        if (!saved) return false;
        InitializeNetworkEnvironment();
        await _publicNetworkAdapter!.RefreshAsync(_windowOneShotCts.Token);
        if (SelectedNetworkEnvironment == NetworkEnvironmentKind.Public)
            await ChangeSelectedNetworkEnvironmentAsync(NetworkEnvironmentKind.Public, persist: false);
        RenderNetworkEnvironment();
        return true;
        }
        finally { _publicSettingsSaving = false; RenderNetworkEnvironment(); }
    }

    /// <summary>All WPF reads and runtime mutations execute on the owning dispatcher.</summary>
    private sealed class LauncherLanAdapter(MainWindow owner) : INetworkEnvironmentAdapter
    {
        public NetworkEnvironmentKind Environment => NetworkEnvironmentKind.Lan;
        public bool IsAvailable => true;
        public async Task<INetworkEnvironmentLease> ConnectAsync(NetworkEnvironmentSelection selection, CancellationToken token)
        {
            if (selection.Environment != NetworkEnvironmentKind.Lan || selection.LanRole is null)
                throw new InvalidOperationException("A LAN role is required.");
            return await (await owner.Dispatcher.InvokeAsync(
                () => owner.ActivateLanRoleAsync(selection.LanRole.Value, token), DispatcherPriority.Normal, token));
        }
    }

    private async Task<INetworkEnvironmentLease> ActivateLanRoleAsync(LanRole role, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        if (_closingBackgroundTasks || _networkEnvironmentClosing || _activeLanEnvironmentLease is not null)
            throw new InvalidOperationException("The previous LAN role has not closed.");
        LanEnvironmentLease? owned = null;
        owned = new LanEnvironmentLease(role == LanRole.Join, async () =>
        {
            await (await Dispatcher.InvokeAsync(async () =>
            {
                if (!ReferenceEquals(_activeLanEnvironmentLease, owned))
                    throw new InvalidOperationException("LAN ownership changed before cleanup.");
                if (role == LanRole.Host)
                {
                    if (!StopDedicatedServer("owned environment lease closed"))
                        throw new IOException("The owned host has not stopped.");
                    // Drain the old broadcast before the next environment can start.
                    await _hostBroadcast.StopAsync();
                    _restoreJoinAddressAfterHost = true;
                }
                _activeLanEnvironmentLease = null;
            }));
        }, role == LanRole.Host ? () => _hostRuntime.IsRunning : null);
        _activeLanEnvironmentLease = owned;
        RevertConnectionModeSelection(role == LanRole.Host ? 1 : 0);
        if (role == LanRole.Join)
        {
            StopHostBroadcast();
            if (_restoreJoinAddressAfterHost)
            {
                ClearServerAddressIfCurrentLanAddress();
                _restoreJoinAddressAfterHost = false;
            }
            _lastServerReachable = false;
            // Role selection can succeed before the user enters a remote endpoint.
            // Readiness remains false until the existing capability check succeeds.
            return owned;
        }
        try
        {
            var local = GetLanLocalStatus();
            if (!local.IsOk || local.Bind is null) throw new IOException("No usable local IPv4 address.");
            string name = txtUsername.Text.Trim();
            if (!IsValidUsernameInput(name) || string.IsNullOrWhiteSpace(name))
                throw new IOException("A valid host name is required.");
            string endpoint = await EnsureHostServerReadyAsync(local.Bind.IpAddress, name, token);
            token.ThrowIfCancellationRequested();
            SetServerAddressProgrammatically(endpoint, null, resetStatus: false);
            owned.ReportCapabilities(true);
        }
        catch (Exception)
        {
            // Return the owned failed candidate to the controller for mandatory
            // cleanup; a thrown constructor must never lose an owned host handle.
            owned.ReportCapabilities(false);
            LogService.Error("LAN environment host activation failed; owned cleanup is required.");
        }
        return owned;
    }

    private async Task<NetworkEnvironmentChangeResult> ChangeLanRoleAsync(LanRole role, bool preserveLocalConfirmation = false)
    {
        var result = await ChangeNetworkSelectionAsync(NetworkEnvironmentSelection.ForLan(role), persist: !App.PreviewMode,
            preserveLocalConfirmation: preserveLocalConfirmation);
        RevertConnectionModeSelection(result.Snapshot.Selection?.LanRole == LanRole.Host ? 1 : 0);
        return result;
    }

    private void RenderNetworkEnvironment()
    {
        Dispatcher.VerifyAccess();
        if (_networkEnvironment is null || cmbNetworkEnvironment is null) return;
        var snapshot = _networkEnvironment.Snapshot;
        _renderingNetworkEnvironment = true;
        try { cmbNetworkEnvironment.SelectedValue = SelectedNetworkEnvironment.ToString(); }
        finally { _renderingNetworkEnvironment = false; }
        cmbNetworkEnvironment.IsEnabled = snapshot.CanSwitch && !_isBusy && !_toolsOperation && !_publicSettingsSaving && !_networkEnvironmentChanging && !_networkEnvironmentClosing && !_statusRefreshRunning;
        cmbConnectionMode.IsEnabled = cmbNetworkEnvironment.IsEnabled && SelectedNetworkEnvironment == NetworkEnvironmentKind.Lan;
        cmbBindAdapter.IsEnabled = cmbConnectionMode.IsEnabled && !IsDedicatedServerRunning();
        cmbNetworkEnvironment.ToolTip = SelectedNetworkEnvironment == NetworkEnvironmentKind.Public ? GetPublicReadinessMessage() : snapshot.IsReady
            ? L("LAN 服务器已通过兼容性检测", "LAN server compatibility check passed")
            : L("LAN 已选择，服务器尚未就绪", "LAN selected; server is not ready");
        RenderModePresentation();
    }

    private bool TryPublishLanCapabilities(LanEnvironmentLease? checkedLease, string checkedEndpoint,
        string checkedBind, bool ready, long? latencyMilliseconds)
    {
        Dispatcher.VerifyAccess();
        if (checkedLease is null || !ReferenceEquals(checkedLease, _activeLanEnvironmentLease)
            || _closingBackgroundTasks || _networkEnvironmentClosing || _networkEnvironmentChanging
            || checkedBind != _settings.BindOverride || HasServerAddressChangedSinceCheckStarted(checkedEndpoint)) return false;
        checkedLease.ReportCapabilities(ready, latencyMilliseconds);
        return true;
    }

    private void InvalidateLanCapabilities()
    {
        _activeLanEnvironmentLease?.ReportCapabilities(false);
    }

    private async Task StopOwnedLaunchAndRefreshEnvironmentAsync(Guid? expectedSession)
    {
        try
        {
            if (expectedSession.HasValue) await _gameLaunchService.StopAsync(expectedSession).ConfigureAwait(false);
        }
        finally
        {
            // Exit UI can run before the named-pipe session finishes draining.
            // Re-read the CURRENT owner afterwards: an older monitor must never
            // clear the activity lock of a replacement launch session.
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_closingBackgroundTasks) return;
                        _ownedGameLaunchActive = _gameLaunchService.SessionId.HasValue;
                        RenderNetworkEnvironment();
                    });
                }
                catch (OperationCanceledException) when (_closingBackgroundTasks || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { }
                catch (InvalidOperationException) when (_closingBackgroundTasks || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { }
            }
        }
    }

    private async Task<bool> CloseNetworkEnvironmentAsync()
    {
        if (_networkEnvironment is null) return true;
        CancelLocalDiscovery();
        _networkEnvironmentClosing = true;
        RenderNetworkEnvironment();
        try { await _networkEnvironment.DisposeAsync(); return true; }
        catch (Exception)
        {
            LogService.Error("Owned LAN environment cleanup did not complete; close can be retried.");
            return false;
        }
        finally { RenderNetworkEnvironment(); }
    }
}
