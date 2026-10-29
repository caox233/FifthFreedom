using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool IsLocalUiMode => Equals(cmbNetworkEnvironment.SelectedValue, "Lan");

    private void RenderModePresentation()
    {
        if (networkModeDescription is null) return;
        bool local = IsLocalUiMode;
        txtNetworkModeLabel.Text = L("联机模式", "Connection mode");
        itemPublicEnvironment.Content = L("公网模式", "Public mode");
        itemLocalEnvironment.Content = L("本地模式", "Local mode");
        networkModeDescription.Text = local
            ? L("查找同一网络的房主，或手动输入地址。连接与创建均需确认。", "Find hosts on your network or enter an address. Joining and hosting require confirmation.")
            : L("使用已配置的公网服务器。启动前必须通过连接与安全检查。", "Use your configured public server. Connection and safety checks must pass before launch.");
        btnPublicConfiguration.Content = L("服务器设置", "Server settings");
        btnLocalConnection.Content = L("查找 / 连接房主", "Find / connect to host");
        btnPublicConfiguration.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        btnLocalConnection.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        bool editable = !_toolsOperation && !_isBusy && !_isGameRunning && !_isGameStarting && !_ownedGameLaunchActive && !_networkEnvironmentChanging && !_networkEnvironmentClosing;
        btnPublicConfiguration.IsEnabled = btnLocalConnection.IsEnabled = editable;
        txtBindAdapterLabel.Visibility = cmbBindAdapter.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        btnForgotPassword.IsEnabled = btnRecoveryPage.IsEnabled = local && editable;
        btnForgotPassword.ToolTip = btnRecoveryPage.ToolTip = local ? null : L("公网密码恢复尚不可用。", "Public password recovery is not available.");
        chkObserverMode.IsEnabled = local && editable;
        chkRememberPassword.IsEnabled = txtPassword.IsEnabled = local && editable;
        txtPassword.ToolTip = local ? null : L("公网登录须先完成连接与安全检查。", "Public login requires connection and safety prerequisites.");
        if (!local)
        {
            _lastPresence = null;
            RenderPresence();
            txtServerStatus.Text = GetPublicReadinessMessage();
            serverStatusLight.Fill = System.Windows.Media.Brushes.Goldenrod;
            btnLaunch.IsEnabled = false;
        }
        txtServerLabel.Text = local ? L("房主地址", "Host address") : L("公网服务器", "Public server");
        AutomationProperties.SetName(cmbNetworkEnvironment, txtNetworkModeLabel.Text);
        AutomationProperties.SetName(txtServer, txtServerLabel.Text);
    }

    private bool TryContainAuxiliaryOverlayFocus(KeyboardFocusChangedEventArgs e)
    {
        UIElement? overlay = localDiscoveryOverlay.Visibility == Visibility.Visible ? localDiscoveryOverlay
            : toolsOverlay.Visibility == Visibility.Visible ? toolsOverlay : null;
        if (overlay is null || e.NewFocus is DependencyObject target && (ReferenceEquals(target, overlay) || overlay.IsAncestorOf(target))) return false;
        e.Handled = true;
        if (ReferenceEquals(overlay, localDiscoveryOverlay)) btnLocalCancel.Focus(); else btnToolsClose.Focus();
        return true;
    }

    private void ApplyUnifiedSettingsLocalization()
    {
        RenderModePresentation();
        btnServerSettingsPage.Content = L("公网服务器", "Public server");
        btnGameplaySettingsPage.Content = L("玩法 / FOV", "Gameplay / FOV");
        btnOpenGameOptions.Content = L("调整玩法", "Game options");
        btnGameplaySave.Content = L("保存", "Save");
        btnGameplayCancel.Content = L("取消", "Cancel");
        txtGameplaySettingsHelp.Text = L("修改先作为草稿。保存后用于下次启动；取消或 Esc 保留之前的设置。DX11 是唯一支持的渲染方式。", "Changes are a draft. Save to use them on the next launch; Cancel or Esc retains previous settings. DX11 is the supported renderer.");
        RenderGameplaySummary();
        btnLanSettingsPage.Content = L("本地 / OB", "Local / OB");
        txtServerSettingsDescription.Text = L(
            "正式与测试服务器分别保存。快速 / 兼容是公网传输方式。保存配置不会自动连接、更新组件或修改防火墙；未满足的前置条件会在下方说明。",
            "Stable and test servers are stored separately. Fast / Compatible are public transports. Saving does not connect, update components or change the firewall. Missing prerequisites are shown below.");
        txtServerEndpointLabel.Text = L("正式服务器地址（域名/IP:端口）", "Stable server (domain/IP:port)");
        txtServerUpdatePortLabel.Text = L("正式更新端口", "Stable update port");
        txtTestServerEndpointLabel.Text = L("测试服务器地址（域名/IP:端口）", "Test server (domain/IP:port)");
        txtTestServerUpdatePortLabel.Text = L("测试更新端口", "Test update port");
        txtPublicTransportLabel.Text = L("公网传输方式", "Public transport");
        itemPublicFast.Content = L("快速", "Fast");
        itemPublicCompatible.Content = L("兼容", "Compatible");
        txtPublicDeploymentLabel.Text = L("部署配置（由服务器维护者提供）", "Deployment configuration (from the server operator)");
        btnPublicDeploymentBrowse.Content = L("导入…", "Import…");
        btnServerSettingsReset.Content = L("恢复默认", "Defaults");
        btnServerSettingsSave.Content = L("保存", "Save");
        foreach (var pair in new[] {
            (txtServerEndpoint, txtServerEndpointLabel.Text), (txtServerUpdatePort, txtServerUpdatePortLabel.Text),
            (txtTestServerEndpoint, txtTestServerEndpointLabel.Text), (txtTestServerUpdatePort, txtTestServerUpdatePortLabel.Text),
            (txtPublicDeploymentPath, txtPublicDeploymentLabel.Text) })
            AutomationProperties.SetName(pair.Item1, pair.Item2);
        ApplyLocalDiscoveryLocalization();
    }

    private void ServerSettingsPage_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsOperation || _isBusy || _dialogTcs is not null) return;
        txtServerEndpoint.Text = _settings.PublicEndpoint;
        txtServerUpdatePort.Text = _settings.PublicUpdatePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        txtTestServerEndpoint.Text = _settings.TestPublicEndpoint;
        txtTestServerUpdatePort.Text = _settings.TestPublicUpdatePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        cmbPublicTransport.SelectedValue = _settings.NetworkMode;
        txtPublicDeploymentPath.Text = _settings.PublicDeploymentConfigurationPath;
        txtServerSettingsMessage.Text = GetPublicReadinessMessage();
        bool mutable = !_isGameRunning && !_isGameStarting && !_ownedGameLaunchActive && !_networkEnvironmentClosing;
        txtServerEndpoint.IsEnabled = txtServerUpdatePort.IsEnabled = txtTestServerEndpoint.IsEnabled = txtTestServerUpdatePort.IsEnabled = mutable;
        cmbPublicTransport.IsEnabled = txtPublicDeploymentPath.IsEnabled = btnPublicDeploymentBrowse.IsEnabled = mutable;
        btnServerSettingsSave.IsEnabled = btnServerSettingsReset.IsEnabled = mutable;
        ShowTools("server");
    }

    private void ServerSettingsReset_Click(object sender, RoutedEventArgs e)
    {
        if (!btnServerSettingsReset.IsEnabled || _toolsOperation) return;
        var defaults = new LauncherSettings();
        txtServerEndpoint.Text = defaults.PublicEndpoint;
        txtServerUpdatePort.Text = defaults.PublicUpdatePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        txtTestServerEndpoint.Text = defaults.TestPublicEndpoint;
        txtTestServerUpdatePort.Text = defaults.TestPublicUpdatePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        cmbPublicTransport.SelectedValue = defaults.NetworkMode;
        txtPublicDeploymentPath.Text = "";
        txtServerSettingsMessage.Text = L("已填入示例值；保存前不会更改现有配置。", "Example values filled in. Existing settings are unchanged until saved.");
    }

    private void PublicDeploymentBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsOperation || !btnPublicDeploymentBrowse.IsEnabled || App.PreviewMode) return;
        var chooser = new Microsoft.Win32.OpenFileDialog {
            Title = L("选择部署配置", "Select deployment configuration"),
            Filter = "JSON|*.json", CheckFileExists = true, Multiselect = false
        };
        if (chooser.ShowDialog(this) == true) txtPublicDeploymentPath.Text = chooser.FileName;
    }

    private void SetPublicSettingsOperation(bool active)
    {
        // Reuse the existing tools transaction boundary, without pretending this
        // local settings write is a game/network operation.
        _toolsOperation = active;
        btnToolsClose.IsEnabled = btnRecoveryPage.IsEnabled = btnSavesPage.IsEnabled = btnPlayersPage.IsEnabled = btnAdvancedDiagnosticsPage.IsEnabled = !active;
        btnServerSettingsPage.IsEnabled = btnLanSettingsPage.IsEnabled = btnGameplaySettingsPage.IsEnabled = !active;
        txtServerEndpoint.IsEnabled = txtServerUpdatePort.IsEnabled = txtTestServerEndpoint.IsEnabled = txtTestServerUpdatePort.IsEnabled = !active;
        txtPublicDeploymentPath.IsEnabled = cmbPublicTransport.IsEnabled = btnPublicDeploymentBrowse.IsEnabled = !active;
        btnServerSettingsSave.IsEnabled = btnServerSettingsReset.IsEnabled = !active && !_isGameRunning && !_isGameStarting && !_ownedGameLaunchActive;
        if (active) cmbNetworkEnvironment.IsEnabled = false;
        else RenderNetworkEnvironment();
        RenderModePresentation();
    }

    private async void ServerSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsOperation || !btnServerSettingsSave.IsEnabled || _isGameRunning || _isGameStarting || _ownedGameLaunchActive) return;
        bool stableValid = PublicServerConfiguration.TryValidate(txtServerEndpoint.Text, txtServerUpdatePort.Text, out var stable, out _);
        bool testValid = PublicServerConfiguration.TryValidate(txtTestServerEndpoint.Text, txtTestServerUpdatePort.Text, out var test, out _);
        if (!stableValid || stable is null || !testValid || test is null)
        {
            txtServerSettingsMessage.Text = L("请填写有效的服务器地址和 1–65535 的更新端口；地址不能含账号、密码或 URL 路径。", "Enter valid server addresses and update ports from 1 to 65535. Addresses cannot include credentials or URL paths.");
            return;
        }
        if (App.PreviewMode) { txtServerSettingsMessage.Text = L("预览使用合成数据，不保存设置。", "Preview uses synthetic data; settings are not saved."); return; }
        var transport = Equals(cmbPublicTransport.SelectedValue, "Compatible") ? PublicTransport.Compatible : PublicTransport.Fast;
        string path = txtPublicDeploymentPath.Text.Trim();
        SetPublicSettingsOperation(true);
        try
        {
            bool saved = await ApplyPublicServerSettingsAsync(stable, test, transport, path);
            txtServerSettingsMessage.Text = (saved ? L("配置已保存。", "Settings saved. ") : L("配置未保存。", "Settings were not saved. ")) + GetPublicReadinessMessage();
        }
        catch (Exception)
        {
            txtServerSettingsMessage.Text = L("配置未能确认保存。请检查部署配置文件及本机写入权限。", "Save could not be confirmed. Check the deployment file and local write permissions.");
        }
        finally { SetPublicSettingsOperation(false); }
    }
}
