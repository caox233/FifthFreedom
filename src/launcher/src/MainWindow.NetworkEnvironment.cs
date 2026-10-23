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

    private void InitializeNetworkEnvironment()
    {
        Dispatcher.VerifyAccess();
        _networkEnvironment ??= new NetworkEnvironmentController(new LauncherLanAdapter(this),
            () => new NetworkEnvironmentActivity(_isGameRunning, _isGameStarting || _ownedGameLaunchActive));
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

    private async Task<NetworkEnvironmentChangeResult> ChangeLanRoleAsync(LanRole role)
    {
        Dispatcher.VerifyAccess();
        InitializeNetworkEnvironment();
        _ownedGameLaunchActive = _gameLaunchService.SessionId.HasValue;
        _networkEnvironmentChanging = true;
        RenderNetworkEnvironment();
        try
        {
            var result = await _networkEnvironment!.ChangeAsync(NetworkEnvironmentSelection.ForLan(role), _windowOneShotCts.Token);
            RevertConnectionModeSelection(result.Snapshot.Selection?.LanRole == LanRole.Host ? 1 : 0);
            return result;
        }
        finally
        {
            _networkEnvironmentChanging = false;
            RenderNetworkEnvironment();
        }
    }

    private void RenderNetworkEnvironment()
    {
        Dispatcher.VerifyAccess();
        if (_networkEnvironment is null || cmbNetworkEnvironment is null) return;
        var snapshot = _networkEnvironment.Snapshot;
        // Only LAN is available. The disabled Public item remains explicit.
        cmbNetworkEnvironment.SelectedValue = "Lan";
        cmbNetworkEnvironment.IsEnabled = snapshot.CanSwitch && !_isBusy && !_networkEnvironmentChanging && !_networkEnvironmentClosing && !_statusRefreshRunning;
        cmbConnectionMode.IsEnabled = cmbNetworkEnvironment.IsEnabled;
        cmbBindAdapter.IsEnabled = cmbNetworkEnvironment.IsEnabled && !IsDedicatedServerRunning();
        cmbNetworkEnvironment.ToolTip = snapshot.IsReady
            ? L("LAN 服务器已通过兼容性检测", "LAN server compatibility check passed")
            : L("LAN 已选择，服务器尚未就绪", "LAN selected; server is not ready");
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
