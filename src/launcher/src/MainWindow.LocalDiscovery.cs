using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private readonly LocalHostDiscoveryFlow _localDiscovery = new();
    private LocalHostDiscoveryResult? _localDiscoveryResult;
    private long _localUiGeneration;
    private bool _localOperationActive;
    private string _localDialogState = "cancel";
    private DateTime _localResultsObservedUtc;
    private sealed record LocalHostRow(LocalDiscoveredHost Host)
    {
        public string DisplayName => $"{Host.HostName}  ·  {Host.Endpoint}";
    }

    private bool CanOpenLocalConnection => IsLocalUiMode && !_isBusy && !_isGameRunning && !_isGameStarting
        && !_ownedGameLaunchActive && !_networkEnvironmentChanging && !_networkEnvironmentClosing && !_closingBackgroundTasks;

    private void CancelLocalDiscovery()
    {
        _localDiscovery.Cancel();
        ++_localUiGeneration;
        _localDiscoveryResult = null;
        _localDialogState = "cancel";
        if (localDiscoveryOverlay is not null) localDiscoveryOverlay.Visibility = Visibility.Collapsed;
    }

    private async void LocalConnection_Click(object sender, RoutedEventArgs e) => await BeginLocalDiscoveryAsync();
    private async void LocalRescan_Click(object sender, RoutedEventArgs e) => await BeginLocalDiscoveryAsync();

    private async Task BeginLocalDiscoveryAsync()
    {
        Dispatcher.VerifyAccess();
        if (!CanOpenLocalConnection || _localOperationActive || _dialogTcs is not null || App.PreviewMode) return;
        CancelLocalDiscovery();
        long generation = _localUiGeneration;
        toolsOverlay.Visibility = guideOverlay.Visibility = Visibility.Collapsed;
        ClearRecoveryFields();
        localDiscoveryOverlay.Visibility = Visibility.Visible;
        SetLocalDialogState("discovery");
        var local = GetLanLocalStatus();
        if (!local.IsOk || local.Bind is null)
        {
            SetLocalDialogState("error", L("没有可用于发现的 IPv4 网卡。请检查网卡，或手动输入房主地址。尚未确认是否有房主。", "No usable IPv4 adapter for discovery. Check the adapter or enter a host manually. Host availability is unknown."));
            return;
        }
        try
        {
            var result = await _localDiscovery.SearchAsync(local.Bind.IpAddress, _windowOneShotCts.Token);
            if (generation != _localUiGeneration || !CanOpenLocalConnection || !_localDiscovery.IsCurrent(result)
                || GetLanLocalStatus().Bind?.IpAddress != result.BindIp) return;
            _localDiscoveryResult = result;
            _localResultsObservedUtc = DateTime.UtcNow;
            if (result.Status == LocalHostDiscoveryStatus.Completed)
            {
                localHostList.ItemsSource = result.Hosts.Select(host => new LocalHostRow(host)).ToArray();
                if (result.Hosts.Count == 0)
                    SetLocalDialogState("create", L("本轮未发现房主。广播可能被网络隔离；可以重新扫描或手动连接，也可以确认创建本机房主。", "No host responded in this scan. Broadcast may be isolated: rescan, enter an address, or confirm creating a local host."));
                else
                {
                    SetLocalDialogState("join", result.Hosts.Count == 1
                        ? L("发现一个房主。请核对地址与名称，确认后检查连接。", "One host found. Review its name and address before confirming the connection.")
                        : L("发现多个房主。请选择要加入的房主，再确认连接。", "Multiple hosts found. Select the host you want, then confirm the connection."));
                    localHostList.SelectedIndex = result.Hosts.Count == 1 ? 0 : -1;
                }
            }
            else if (result.Status is not (LocalHostDiscoveryStatus.Cancelled or LocalHostDiscoveryStatus.Superseded))
                SetLocalDialogState("error", result.Status == LocalHostDiscoveryStatus.TimedOut
                    ? L("房主发现超时，无法确认是否有房主。请重扫或手动输入地址。", "Discovery timed out; host availability is unknown. Rescan or enter an address.")
                    : L("房主发现未能完成，无法确认是否有房主。请检查网卡、重扫或手动输入地址。", "Discovery could not complete; host availability is unknown. Check the adapter, rescan or enter an address."));
        }
        catch (Exception)
        {
            if (generation == _localUiGeneration && CanOpenLocalConnection)
                SetLocalDialogState("error", L("房主发现未能完成。可以重扫或手动输入地址，尚未确认是否有房主。", "Discovery did not complete. Rescan or enter an address; host availability is unknown."));
        }
    }

    private void SetLocalDialogState(string state, string? message = null)
    {
        _localDialogState = state;
        txtLocalTitle.Text = state == "create" ? L("创建本地房主", "Create local host")
            : state is "join" or "manual" ? L("连接本地房主", "Connect to local host") : L("查找本地房主", "Find local hosts");
        txtLocalMessage.Text = message ?? (state == "discovery"
            ? L("正在检查所选网卡上的房主，本轮最多等待 4 秒。", "Checking the selected adapter for hosts; this scan takes at most 4 seconds.")
            : L("确认后才会连接或创建；取消不会启动服务。", "Connection or hosting starts only after confirmation. Cancel starts no service."));
        localHostList.Visibility = state == "join" ? Visibility.Visible : Visibility.Collapsed;
        txtLocalEndpointLabel.Visibility = txtLocalEndpoint.Visibility = state is "join" or "manual" ? Visibility.Visible : Visibility.Collapsed;
        txtLocalEndpoint.IsReadOnly = state != "manual";
        txtLocalEndpoint.Text = "";
        btnLocalConfirm.Content = state == "create" ? L("确认创建", "Create host") : L("确认连接", "Connect");
        btnLocalConfirm.Visibility = state is "join" or "manual" or "create" ? Visibility.Visible : Visibility.Collapsed;
        btnLocalConfirm.IsEnabled = state is "manual" or "create";
        btnLocalRescan.IsEnabled = btnLocalManual.IsEnabled = btnLocalCreate.IsEnabled = btnLocalCancel.IsEnabled = !_localOperationActive;
        btnLocalCancel.Focus();
    }

    private void ApplyLocalDiscoveryLocalization()
    {
        if (btnLocalRescan is null) return;
        btnLocalRescan.Content = L("重新扫描", "Rescan");
        btnLocalManual.Content = L("手动输入", "Manual address");
        btnLocalCreate.Content = L("创建房主…", "Create host…");
        btnLocalCancel.Content = L("取消", "Cancel");
        txtLocalEndpointLabel.Text = L("房主地址（IPv4 或域名:端口）", "Host address (IPv4 or domain:port)");
    }

    private void LocalHost_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_localDialogState != "join") return;
        var row = localHostList.SelectedItem as LocalHostRow;
        txtLocalEndpoint.Text = row?.Host.Endpoint ?? "";
        btnLocalConfirm.IsEnabled = row is not null && !_localOperationActive;
    }

    private void LocalManual_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenLocalConnection || _localOperationActive) return;
        _localDiscovery.Cancel(); ++_localUiGeneration; _localDiscoveryResult = null;
        SetLocalDialogState("manual", L("填写房主提供的地址。广播不可用时也可手动连接；确认后仍会检查服务器兼容性。", "Enter the host's address. Manual connection works without broadcast; server compatibility is still checked after confirmation."));
        txtLocalEndpoint.Text = IsHostMode() ? "" : txtServer.Text;
        txtLocalEndpoint.Focus();
    }

    private void LocalCreate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenLocalConnection || _localOperationActive) return;
        _localDiscovery.Cancel(); ++_localUiGeneration; _localDiscoveryResult = null;
        SetLocalDialogState("create", L("确认后使用所选网卡和当前账号名创建本地房主。其他玩家可以发现或手动连接；现在尚未启动服务。", "Confirm to create a host on the selected adapter using your account name. Others can discover or connect manually. No server has started yet."));
    }

    private void LocalCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_localOperationActive) return;
        CancelLocalDiscovery(); btnLocalConnection.Focus();
    }
    private void LocalDiscovery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { LocalCancel_Click(sender, e); e.Handled = true; }
    }

    private async void LocalConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenLocalConnection || _localOperationActive || !btnLocalConfirm.IsEnabled || App.PreviewMode) return;
        long generation = _localUiGeneration;
        string state = _localDialogState;
        var row = localHostList.SelectedItem as LocalHostRow;
        string endpoint = txtLocalEndpoint.Text.Trim();
        string? confirmedBind = GetLanLocalStatus().Bind?.IpAddress;
        if (state == "join" && (_localDiscoveryResult is null || !_localDiscovery.IsCurrent(_localDiscoveryResult)
            || row is null || GetLanLocalStatus().Bind?.IpAddress != _localDiscoveryResult.BindIp
            || DateTime.UtcNow - _localResultsObservedUtc > TimeSpan.FromSeconds(10)))
        {
            SetLocalDialogState("error", L("发现结果已过期，请重新扫描或手动输入地址。", "Discovery results expired. Rescan or enter the address manually.")); return;
        }
        if (state is "join" or "manual" && !IsValidServerAddressText(endpoint, out _))
        {
            txtLocalMessage.Text = L("请输入有效的 IPv4 或域名，可附加端口。", "Enter a valid IPv4 address or hostname, optionally followed by a port."); return;
        }
        if (state == "create" && (!IsValidUsernameInput(txtUsername.Text.Trim()) || string.IsNullOrWhiteSpace(txtUsername.Text)))
        {
            txtLocalMessage.Text = L("请先取消并填写账号名，它将作为房主名称。", "Cancel and enter an account name first; it will identify your host."); return;
        }
        if (state is not ("join" or "manual" or "create")) return;
        _localOperationActive = true;
        btnLocalConfirm.IsEnabled = btnLocalRescan.IsEnabled = btnLocalManual.IsEnabled = btnLocalCreate.IsEnabled = btnLocalCancel.IsEnabled = false;
        try
        {
            if (state == "create")
            {
                localDiscoveryOverlay.Visibility = Visibility.Collapsed;
                await StartHostModeServerFromUiAsync();
            }
            else
            {
                var changed = await ChangeLanRoleAsync(LanRole.Join, preserveLocalConfirmation: true);
                if (generation != _localUiGeneration || !IsLocalUiMode || changed.Outcome is not (NetworkEnvironmentChangeOutcome.Applied or NetworkEnvironmentChangeOutcome.Unchanged)) return;
                if (_closingBackgroundTasks || _networkEnvironmentClosing || _isGameRunning || _isGameStarting || _ownedGameLaunchActive
                    || GetLanLocalStatus().Bind?.IpAddress != confirmedBind
                    || state == "join" && (_localDiscoveryResult is null || !_localDiscovery.IsCurrent(_localDiscoveryResult)
                        || DateTime.UtcNow - _localResultsObservedUtc > TimeSpan.FromSeconds(10)))
                {
                    SetLocalDialogState("error", L("连接条件或发现结果已变化，请重扫或重新输入。", "Connection conditions or discovery results changed. Rescan or re-enter the address.")); return;
                }
                SetServerAddressProgrammatically(endpoint, state == "join" ? row?.Host.HostName : null);
                localDiscoveryOverlay.Visibility = Visibility.Collapsed;
                SaveSettingsFromUi();
                await RefreshServerStatusAsync(true);
            }
        }
        catch (Exception)
        {
            if (generation == _localUiGeneration && !_networkEnvironmentClosing)
            {
                localDiscoveryOverlay.Visibility = Visibility.Visible;
                SetLocalDialogState("error", L("连接操作未能完成。请检查地址与网卡后重试。", "The connection could not complete. Check the address and adapter before retrying."));
            }
        }
        finally
        {
            _localOperationActive = false;
            if (generation == _localUiGeneration)
            {
                btnLocalRescan.IsEnabled = btnLocalManual.IsEnabled = btnLocalCreate.IsEnabled = btnLocalCancel.IsEnabled = true;
                RenderModePresentation();
            }
        }
    }

    internal void ApplyVisualProbeState(string state)
    {
        if (!App.PreviewMode) throw new InvalidOperationException("Synthetic preview only.");
        CancelLocalDiscovery(); toolsOverlay.Visibility = dialogOverlay.Visibility = guideOverlay.Visibility = Visibility.Collapsed;
        txtUsername.Text = "PreviewPlayer"; txtPassword.Clear();
        _settings.NetworkEnvironment = state == "main" || state == "settings-public" ? "Public" : "Lan";
        cmbNetworkEnvironment.SelectedValue = _settings.NetworkEnvironment;
        RenderModePresentation();
        if (state == "main") { txtServer.Text = "public.example.invalid:11010"; txtServerStatus.Text = L("公网尚未就绪，请完成服务器配置与前置检查。", "Public is not ready. Complete server configuration and prerequisites."); btnLaunch.IsEnabled = false; return; }
        if (state == "settings") { ShowToolsPreview("home"); return; }
        if (state == "settings-public") { ServerSettingsPage_Click(this, new()); return; }
        if (state == "settings-gameplay") { CancelGameplayDraft(); BeginGameplayDraft(); return; }
        txtServer.Text = "192.0.2.10:50051";
        if (state == "cancel") { txtServerStatus.Text = L("已取消房主发现，未连接或创建。", "Discovery cancelled; no connection or host was started."); return; }
        localDiscoveryOverlay.Visibility = Visibility.Visible;
        SetLocalDialogState(state, state == "create"
            ? L("本轮未发现房主。可以重扫、手动输入，或确认创建本机房主。", "No host responded in this scan. Rescan, enter an address or confirm creating a local host.")
            : state == "join" ? L("发现多个房主。请选择房主，再确认连接。", "Multiple hosts found. Choose a host, then confirm the connection.") : null);
        if (state == "join")
        {
            localHostList.ItemsSource = new[] {
                new LocalHostRow(new("192.0.2.10", 50051, "PreviewHost A", "synthetic-a")),
                new LocalHostRow(new("192.0.2.11", 50051, "PreviewHost B — long synthetic host name for layout verification", "synthetic-b")) };
            localHostList.SelectedIndex = 0;
        }
    }
}
