using SplinterCellCNLauncher.Services;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.PlacementTarget = btnSettings;
        settingsMenu.Placement = PlacementMode.Bottom;
        settingsMenu.IsOpen = true;
    }

    private void SettingsMenu_Opened(object sender, RoutedEventArgs e)
        => UpdateSettingsMenuText();

    private void UpdateSettingsMenuText()
    {
        if (miGuide == null)
            return;
        btnSettings.ToolTip = L("设置", "Settings");
        miGuide.Header = L("❓  使用指引", "❓  Guide");
        miLanguageToggle.Header = L("🌐  中文 / English", "🌐  中文 / English");
        miMusicToggle.Header = _settings.MusicEnabled
            ? L("🔊  声音：开启", "🔊  Sound: On")
            : L("🔇  声音：关闭", "🔇  Sound: Off");
        miServerSettings.Header = L("🖧  服务器设置", "🖧  Server Settings");
        string testChannelHeader = App.ComponentUpdateChannel == Models.ClientUpdateChannel.Test
            ? L("🧪  测试通道（当前）", "🧪  Test Channel (Active)")
            : L("🧪  测试通道", "🧪  Test Channel");
        miTestChannel.Header = testChannelHeader;
        AutomationProperties.SetName(miTestChannel, testChannelHeader);
        AutomationProperties.SetHelpText(
            miTestChannel,
            L("切换客户端组件更新通道", "Switch the client component update channel"));
        miOverwriteSaves.Header = L("💾  覆盖全解锁存档", "💾  Overwrite Full-Unlock Saves");
        miRepairNetwork.Header = L("🛠  修复网络", "🛠  Repair Network");
        miExportDiagnostics.Header = L("📦  导出诊断包", "📦  Export Diagnostics");
        bool gameActive = _isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning();
        miOverwriteSaves.IsEnabled = !gameActive && !_isUpdating;
        miRepairNetwork.IsEnabled = !gameActive && !_isUpdating && !_isBusy;
        miServerSettings.IsEnabled = !_isUpdating;
        miTestChannel.IsEnabled = !gameActive && !_isUpdating && !_isBusy;
    }

    private void ServerSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        txtServerEndpoint.Text = _settings.PublicEndpoint;
        txtServerUpdatePort.Text = _settings.PublicUpdatePort.ToString();
        txtTestServerEndpoint.Text = _settings.TestPublicEndpoint;
        txtTestServerUpdatePort.Text = _settings.TestPublicUpdatePort.ToString();
        txtServerEndpoint.IsEnabled = true;
        txtServerUpdatePort.IsEnabled = true;
        txtTestServerEndpoint.IsEnabled = true;
        txtTestServerUpdatePort.IsEnabled = true;
        btnServerSettingsReset.IsEnabled = true;
        btnServerSettingsSave.IsEnabled = true;
        txtServerSettingsMessage.Text = "";
        ApplyServerSettingsLocalization(saved: false);
        serverSettingsOverlay.Visibility = Visibility.Visible;
        txtServerEndpoint.Focus();
        txtServerEndpoint.SelectAll();
    }

    private void ApplyServerSettingsLocalization(bool saved)
    {
        txtServerSettingsTitle.Text = L("服务器设置", "Server Settings");
        txtServerSettingsDescription.Text = L(
            "修改联机服务器。保存后启动器会自动重启。",
            "Change the online server. The launcher restarts after saving.");
        txtServerEndpointLabel.Text = L("正式服务器地址（域名/IP:端口）", "Stable server address (domain/IP:port)");
        txtServerUpdatePortLabel.Text = L("正式服务器更新端口", "Stable server update port");
        txtTestServerEndpointLabel.Text = L("测试服务器地址（域名/IP:端口）", "Test server address (domain/IP:port)");
        txtTestServerUpdatePortLabel.Text = L("测试服务器更新端口", "Test server update port");
        btnServerSettingsReset.Content = L("恢复默认", "Defaults");
        btnServerSettingsCancel.Content = saved ? L("关闭", "Close") : L("取消", "Cancel");
        btnServerSettingsSave.Content = saved ? L("已保存", "Saved") : L("保存并重启", "Save & Restart");
    }

    private void ServerSettingsResetButton_Click(object sender, RoutedEventArgs e)
    {
        txtServerEndpoint.Text = PublicTunnelConfig.DefaultPublicEndpoint;
        txtServerUpdatePort.Text = PublicTunnelConfig.DefaultPublicUpdatePort.ToString();
        txtTestServerEndpoint.Text = PublicTunnelConfig.DefaultTestPublicEndpoint;
        txtTestServerUpdatePort.Text = PublicTunnelConfig.DefaultTestPublicUpdatePort.ToString();
        txtServerSettingsMessage.Text = "";
    }

    private void ServerSettingsCancelButton_Click(object sender, RoutedEventArgs e)
        => serverSettingsOverlay.Visibility = Visibility.Collapsed;

    private async void ServerSettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        bool stableValid = ServerSettingsValidator.TryValidate(
            txtServerEndpoint.Text,
            txtServerUpdatePort.Text,
            out ValidatedServerSettings? stable,
            out ServerSettingsValidationError stableError);
        bool testValid = ServerSettingsValidator.TryValidate(
            txtTestServerEndpoint.Text,
            txtTestServerUpdatePort.Text,
            out ValidatedServerSettings? test,
            out ServerSettingsValidationError testError);
        if (!stableValid || stable == null || !testValid || test == null)
        {
            ServerSettingsValidationError error = stable == null ? stableError : testError;
            txtServerSettingsMessage.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            txtServerSettingsMessage.Text = error switch
            {
                ServerSettingsValidationError.EndpointRequired => L("请输入服务器地址。", "Enter a server address."),
                ServerSettingsValidationError.UpdatePortInvalid => L("更新端口必须是 1–65535。", "The update port must be between 1 and 65535."),
                _ => L("服务器地址格式无效，例如：public.example.invalid:11010", "Invalid server address. Example: public.example.invalid:11010")
            };
            return;
        }

        string previousEndpoint = _settings.PublicEndpoint;
        int previousUpdatePort = _settings.PublicUpdatePort;
        string previousTestEndpoint = _settings.TestPublicEndpoint;
        int previousTestUpdatePort = _settings.TestPublicUpdatePort;
        bool changed = !previousEndpoint.Equals(stable.PublicEndpoint, StringComparison.OrdinalIgnoreCase)
            || previousUpdatePort != stable.UpdatePort
            || !previousTestEndpoint.Equals(test.PublicEndpoint, StringComparison.OrdinalIgnoreCase)
            || previousTestUpdatePort != test.UpdatePort;
        if (changed && (_isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning()))
        {
            await ShowInfoDialogAsync(
                L("请先结束游戏", "End the Game First"),
                L("请先结束游戏，再更换服务器。", "End the game before changing servers."));
            return;
        }

        _settings.PublicEndpoint = stable.PublicEndpoint;
        _settings.PublicUpdatePort = stable.UpdatePort;
        _settings.TestPublicEndpoint = test.PublicEndpoint;
        _settings.TestPublicUpdatePort = test.UpdatePort;
        _settingsService.Save(_settings);

        LogService.Info(
            $"Server settings saved: stable={stable.PublicEndpoint}/{stable.UpdatePort}, "
            + $"test={test.PublicEndpoint}/{test.UpdatePort}, changed={changed}, takesEffect=next-launch");

        txtServerEndpoint.Text = stable.PublicEndpoint;
        txtServerUpdatePort.Text = stable.UpdatePort.ToString();
        txtTestServerEndpoint.Text = test.PublicEndpoint;
        txtTestServerUpdatePort.Text = test.UpdatePort.ToString();
        txtServerEndpoint.IsEnabled = false;
        txtServerUpdatePort.IsEnabled = false;
        txtTestServerEndpoint.IsEnabled = false;
        txtTestServerUpdatePort.IsEnabled = false;
        btnServerSettingsReset.IsEnabled = false;
        btnServerSettingsSave.IsEnabled = false;
        txtServerSettingsMessage.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        txtServerSettingsMessage.Text = changed
            ? L("已保存，正在重新启动启动器……", "Saved. Restarting the launcher...")
            : L("设置没有变化。", "No settings were changed.");
        ApplyServerSettingsLocalization(saved: true);

        if (!changed)
            return;

        if (_localUpdateService.ScheduleLauncherRestartAfterExit(Environment.ProcessId))
        {
            serverSettingsOverlay.Visibility = Visibility.Collapsed;
            Close();
            return;
        }

        txtServerSettingsMessage.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        txtServerSettingsMessage.Text = L(
            "设置已保存，但无法自动重启。请手动关闭并重新打开启动器。",
            "Settings were saved, but automatic restart failed. Close and reopen the launcher manually.");
        btnServerSettingsCancel.Content = L("关闭", "Close");
    }

    private void ServerPort_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(ch => !char.IsDigit(ch));

    private void ServerPort_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)
            || e.DataObject.GetData(DataFormats.Text) is not string text
            || text.Any(ch => !char.IsDigit(ch)))
        {
            e.CancelCommand();
        }
    }

    private async void RepairNetworkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        if (_isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning())
        {
            await ShowInfoDialogAsync(
                L("请先关闭游戏", "Close the Game First"),
                L("修复网络前请先关闭游戏。", "Close the game before repairing the network."));
            return;
        }

        MessageBoxResult confirm = await ShowConfirmDialogAsync(
            L("修复网络", "Repair Network"),
            L("将重新创建联机网络。是否继续？", "The online connection will be rebuilt. Continue?"),
            L("开始修复", "Repair"),
            L("取消", "Cancel"));
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true, L("正在修复网络...", "Repairing network..."));
        try
        {
            var result = await _networkOrchestrator.EnsureReadyAsync(NetworkEnsureMode.Repair, "settings network repair");
            _networkReady = result.Ok;
            if (result.Ok)
            {
                _assignedIp = result.AssignedIp;
                _lastServerLatencyMs = result.LatencyMs;
                await ShowInfoDialogAsync(
                    L("网络修复完成", "Network Repair Complete"),
                    L("联机网络已恢复。", "The online connection has been restored."));
            }
            else
            {
                await ShowNetworkFailureDialogAsync(result);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }
}
