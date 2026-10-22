using Microsoft.Win32;
using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void ApplyLocalization()
    {
        try
        {
            txtTopBadge.Text = "SCBL";
            btnSettings.ToolTip = L("设置", "Settings");
            RefreshAnnouncementVisual();
            txtSectionTitle.Text = L("SCBL登录", "SCBL SIGN IN");
            txtUsernameLabel.Text = L("账号", "Username");
            txtPasswordLabel.Text = L("密码", "Password");
            txtUsernamePlaceholder.Text = L("输入账号", "Enter username");
            txtPasswordPlaceholder.Text = L("输入密码", "Enter password");
            chkRememberPassword.Content = L("记住密码", "Remember password");
            btnForgotPassword.Content = L("忘记密码？", "Forgot password?");
            txtConnectionStatusCaption.Text = L("连接状态", "Connection Status");
            UpdateCheckNetworkButtonAvailability();
            txtLaunchModeLabel.Text = L("启动模式", "Launch Mode");
            txtRendererLabel.Text = L("渲染接口", "RENDERER");
            txtGameplayModeLabel.Text = L("游戏模式", "GAME MODE");
            System.Windows.Automation.AutomationProperties.SetName(
                cmbGameExecutable,
                L("渲染接口", "Renderer"));
            System.Windows.Automation.AutomationProperties.SetName(
                cmbGameplayMode,
                L("游戏模式", "Game mode"));
            System.Windows.Automation.AutomationProperties.SetName(
                txtUsername,
                L("账号", "Username"));
            System.Windows.Automation.AutomationProperties.SetName(
                txtPassword,
                L("密码", "Password"));
            System.Windows.Automation.AutomationProperties.SetName(
                txtPasswordVisible,
                L("显示的密码", "Visible password"));
            UpdatePasswordVisibilityAccessibility();
            ApplyResponsiveLanguageLayout();
            UpdateMusicButton();
            RefreshServerStatusTextFromKind();
            RefreshLaunchButtonTextFromState();
            txtFooterNotice.Text = L(
                "基于开源项目 5th Echelon 制作。联机群：709112052\n原项目：https://github.com/unixoide/5th-echelon",
                "Based on the open-source 5th Echelon project. CN group: 709112052\nSource: https://github.com/unixoide/5th-echelon");
            txtLauncherVersion.Text = $"v{GetDisplayVersion()}";
            if (txtPlayersTitle != null)
                txtPlayersTitle.Text = L("在线玩家", "Online Players");
            if (txtPeerHeaderName != null)
                txtPeerHeaderName.Text = L("玩家ID", "Player ID");
            if (txtPeerHeaderLatency != null)
                txtPeerHeaderLatency.Text = L("延迟", "Latency");
            if (btnRefreshPlayers != null)
                btnRefreshPlayers.Content = L("刷新", "Refresh");
            if (btnClosePlayers != null)
            {
                btnClosePlayers.Content = "×";
                btnClosePlayers.ToolTip = L("关闭", "Close");
            }
            UpdatePlayersButtonText();
            RenderPeerList();
            SetBusy(_isBusy);
        }
        catch (Exception ex)
        {
            LogService.Error($"ApplyLocalization failed: {ex}");
        }
    }

    private void ApplyNetworkStatusSnapshot(NetworkStatusSnapshot snapshot)
    {
        _lastServerLatencyMs = snapshot.LatencyMs;
        if (snapshot.Phase == NetworkPhase.Connected)
        {
            if (!NetworkStatusSnapshot.HasCompleteConnectionDetails(
                    snapshot.Phase,
                    snapshot.TransportMode,
                    snapshot.UnderlayAddressFamily,
                    snapshot.RuntimeObservationId))
            {
                ClearServerPathPresentation();
                _networkReady = false;
                SetServerStatus(YellowBrush, "", ServerStatusKind.ServerConnecting);
                return;
            }

            _lastConnectionTransport = snapshot.TransportMode.Trim();
            _lastConnectionAddressFamily = snapshot.UnderlayAddressFamily.Trim();
            _serverUsesTcpFallback = IsNonUdpServerTransport(_lastConnectionTransport);
        }
        else
        {
            ClearServerPathPresentation();
        }

        switch (snapshot.Phase)
        {
            case NetworkPhase.Connected:
                _networkReady = true;
                TryStartRemoteClientServices(isReady: true);
                SetServerStatus(GreenBrush, "", ServerStatusKind.Normal);
                EnsurePeerProbeStarted();
                StartControlPlaneHeartbeat();
                ScheduleAutomaticPeerRefresh();
                break;
            case NetworkPhase.Preparing:
                _networkReady = false;
                SetServerStatus(YellowBrush, "", ServerStatusKind.NetworkCreating);
                break;
            case NetworkPhase.TunnelConnecting:
                _networkReady = false;
                SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelConnecting);
                break;
            case NetworkPhase.ServerConnecting:
                _networkReady = false;
                SetServerStatus(YellowBrush, "", ServerStatusKind.ServerConnecting);
                break;
            case NetworkPhase.Reconnecting:
                _networkReady = false;
                SetServerStatus(YellowBrush, "", ServerStatusKind.TunnelReconnecting);
                break;
            case NetworkPhase.NetworkFailed:
                _networkReady = false;
                SetServerStatus(RedBrush, "", ServerStatusKind.NetworkFailed);
                break;
            case NetworkPhase.TunnelFailed:
                _networkReady = false;
                SetServerStatus(RedBrush, "", ServerStatusKind.TunnelFailed);
                break;
            case NetworkPhase.ServerFailed:
                _networkReady = false;
                SetServerStatus(RedBrush, "", ServerStatusKind.ServerFailed);
                break;
        }
    }

    private void ClearServerPathPresentation()
    {
        _lastConnectionTransport = "";
        _lastConnectionAddressFamily = "";
        _serverUsesTcpFallback = false;
    }

    private void SetServerStatus(Brush brush, string text, ServerStatusKind kind)
    {
        if (_networkShutdownStarted)
            return;

        _serverStatusKind = kind;

        serverStatusLight.Fill = brush;
        string display = string.IsNullOrWhiteSpace(text) ? FormatServerStatusText(kind) : text;
        if (kind == ServerStatusKind.Normal || kind == ServerStatusKind.ServerFailed)
            display = FormatServerStatusText(kind);
        txtServerStatus.Text = display;
        txtServerStatus.ToolTip = display;
        UpdateBottomLatencyDisplay();
        UpdateLaunchButtonAvailability();
    }

    private string FormatServerStatusText(ServerStatusKind kind)
    {
        string normalText = L("连接成功", "Connected");
        if (kind == ServerStatusKind.Normal
            && !string.IsNullOrWhiteSpace(_lastConnectionAddressFamily)
            && !string.IsNullOrWhiteSpace(_lastConnectionTransport))
        {
            string path = FormatPathDescriptor(
                _lastConnectionAddressFamily,
                _lastConnectionTransport,
                null).Replace("/", " · ", StringComparison.Ordinal);
            normalText = IsEnglish
                ? $"{normalText} ({path})"
                : $"{normalText}（{path}）";
        }

        return kind switch
        {
            ServerStatusKind.NetworkCreating => L("网络准备中", "Preparing network"),
            ServerStatusKind.TunnelConnecting => L("网络连接中", "Connecting network"),
            ServerStatusKind.ServerConnecting => L("服务器连接中", "Connecting to server"),
            ServerStatusKind.TunnelReconnecting => L("网络重连中", "Reconnecting network"),
            ServerStatusKind.Normal => normalText,
            ServerStatusKind.NetworkFailed => L("网络准备失败", "Network preparation failed"),
            ServerStatusKind.TunnelFailed => L("网络连接失败", "Network connection failed"),
            ServerStatusKind.ServerFailed => L("服务器连接失败", "Server connection failed"),
            _ => L("未检测", "Not checked")
        };
    }

    private string FormatPathDescriptor(string addressFamily, string transport, int? hopCount)
    {
        string family = (addressFamily ?? "").Trim();
        string mode = (transport ?? "").Trim();
        if (mode.Equals("udp", StringComparison.OrdinalIgnoreCase))
            mode = "UDP";
        else if (mode.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            mode = "TCP";
        else if (mode.Equals("wss", StringComparison.OrdinalIgnoreCase))
            mode = "WSS";

        if (IsEnglish)
        {
            mode = mode
                .Replace("多跳中继", "Relay", StringComparison.OrdinalIgnoreCase)
                .Replace("多跳-", "Relay-", StringComparison.OrdinalIgnoreCase)
                .Replace("UDP中继", "UDP Relay", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(family))
            return mode;
        if (string.IsNullOrWhiteSpace(mode))
            return family;
        return $"{family}/{mode}";
    }

    private void RefreshServerStatusTextFromKind()
    {
        if (txtServerStatus == null)
            return;
        txtServerStatus.Text = FormatServerStatusText(_serverStatusKind);
        txtServerStatus.ToolTip = txtServerStatus.Text;
        UpdateBottomLatencyDisplay();
    }

    private void UpdateBottomLatencyDisplay()
    {
        if (txtBottomLatency == null)
            return;

        long? latency = null;
        string tooltip;
        if (_gameLatencyActive && _localIsGameHost)
        {
            latency = _lastGameLatencyMs ?? _lastServerLatencyMs;
            tooltip = L("本机为房主，显示服务器延迟。", "You are the host; showing server latency.");
        }
        else if (_gameLatencyActive && !string.IsNullOrWhiteSpace(_gamePeerIp))
        {
            latency = _lastGameLatencyMs;
            string descriptor = FormatPathDescriptor(_lastGameAddressFamily, _lastGameTransport, _lastGameHopCount);
            tooltip = string.IsNullOrWhiteSpace(descriptor)
                ? L("当前显示与房主的延迟。", "Showing latency to the host.")
                : L($"当前显示与房主的延迟 · {descriptor}", $"Showing latency to the host · {descriptor}");
        }
        else if (_gameLatencyActive && _gameActivePeerCount > 0)
        {
            tooltip = L("正在识别房主并检测延迟。", "Identifying the host and measuring latency.");
        }
        else if (_networkReady)
        {
            latency = _lastServerLatencyMs;
            string descriptor = FormatPathDescriptor(_lastConnectionAddressFamily, _lastConnectionTransport, null);
            tooltip = string.IsNullOrWhiteSpace(descriptor)
                ? L("当前显示与服务器的延迟。", "Showing latency to the server.")
                : L($"当前显示与服务器的延迟 · {descriptor}", $"Showing latency to the server · {descriptor}");
        }
        else
        {
            tooltip = L("网络尚未连接。", "Network is not connected.");
        }

        txtBottomLatency.Text = latency.HasValue
            ? L($"延迟：{latency.Value} ms", $"Latency: {latency.Value} ms")
            : L("延迟：-- ms", "Latency: -- ms");
        txtBottomLatency.ToolTip = tooltip;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _isBusy = busy;
        if (busy && settingsMenu != null)
            settingsMenu.IsOpen = false;
        UpdateSettingsMenuText();
        UpdateCheckNetworkButtonAvailability();
        UpdateLaunchConfigurationAvailability();
        UpdateLaunchButtonAvailability();

        // 普通游戏流程使用“启动游戏 / 正在启动中 / 结束游戏”三种状态。
        // 客户端更新是唯一例外：按钮禁用并显示“正在更新中”，防止游戏与 Updater 并发。
        // 网络确认、登录、部署组件等启动阶段进度只写日志或通过失败弹窗提示，不再借用启动按钮显示。
        if (!string.IsNullOrWhiteSpace(text))
            LogService.Info("Launch stage: " + text);
        RefreshLaunchButtonTextFromState();
    }

    private void SetUpdatingState(bool updating)
    {
        if (_isUpdating == updating)
            return;

        _isUpdating = updating;
        if (updating && settingsMenu != null)
            settingsMenu.IsOpen = false;
        UpdateSettingsMenuText();
        if (!updating)
            SetClientUpdateProgressAllowed(false);
        LogService.Info(updating ? "Client update state entered." : "Client update state cleared.");
        RefreshLaunchButtonTextFromState();
        UpdateLaunchConfigurationAvailability();
        UpdateLaunchButtonAvailability();
    }

    private void RefreshLaunchButtonTextFromState()
    {
        if (btnLaunch == null)
            return;

        LauncherInteractionState interaction = GetInteractionState();

        // The five-character Chinese progress labels need the full button width.
        // Hide the decorative arrow for progress states instead of clipping text
        // or shrinking the typography used by the cinematic layout.
        btnLaunch.Tag = interaction.PrimaryAction switch
        {
            LauncherPrimaryAction.Updating or LauncherPrimaryAction.Starting => "Progress",
            LauncherPrimaryAction.Restart => "Restart",
            LauncherPrimaryAction.EndGame => "EndGame",
            _ => null
        };

        if (interaction.PrimaryAction == LauncherPrimaryAction.Updating)
        {
            btnLaunch.Content = L("正在更新中", "Updating...");
            btnLaunch.Style = (Style)FindResource("PrimaryButton");
        }
        else if (interaction.PrimaryAction is LauncherPrimaryAction.Starting or LauncherPrimaryAction.Restart)
        {
            btnLaunch.Content = interaction.PrimaryAction == LauncherPrimaryAction.Restart
                ? L("重新启动", "Restart")
                : L("正在启动中", "Starting...");
            btnLaunch.Style = (Style)FindResource("PrimaryButton");
        }
        else if (interaction.PrimaryAction == LauncherPrimaryAction.EndGame)
        {
            btnLaunch.Content = L("结束游戏", "End Game");
            btnLaunch.Style = (Style)FindResource("DangerButton");
        }
        else
        {
            btnLaunch.Content = L("启动游戏", "Launch Game");
            btnLaunch.Style = (Style)FindResource("PrimaryButton");
        }

        System.Windows.Automation.AutomationProperties.SetName(
            btnLaunch,
            btnLaunch.Content?.ToString() ?? L("启动游戏", "Launch Game"));
    }

    private void BeginNetworkCheckButtonCooldown()
    {
        _networkCheckCooldownCts?.Cancel();
        _networkCheckCooldownCts?.Dispose();
        _networkCheckCooldownCts = new CancellationTokenSource();
        _networkCheckButtonCoolingDown = true;
        UpdateCheckNetworkButtonAvailability();
        var token = _networkCheckCooldownCts.Token;
        _ = RunNetworkCheckButtonCooldownAsync(token);
    }

    private async Task RunNetworkCheckButtonCooldownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            _networkCheckButtonCoolingDown = false;
            await Dispatcher.InvokeAsync(UpdateCheckNetworkButtonAvailability);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateCheckNetworkButtonAvailability()
    {
        if (btnCheckNetwork == null)
            return;
        // Network check is independent from game state. It only enters cooldown after a manual click.
        btnCheckNetwork.IsEnabled = GetInteractionState().IsNetworkCheckEnabled;
        txtBottomNetwork.Text = L("检测网络", "Check Network");
        SetNetworkCheckAnimation(_networkCheckInProgress);
    }

    private void SetNetworkCheckAnimation(bool active)
    {
        if (networkCheckIcon == null || _networkCheckAnimationActive == active)
            return;

        _networkCheckAnimationActive = active;
        if (active)
        {
            networkCheckIcon.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.32, 1.0, TimeSpan.FromMilliseconds(650))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
        else
        {
            networkCheckIcon.BeginAnimation(OpacityProperty, null);
            networkCheckIcon.Opacity = 1;
        }
    }

    private async Task<string> EnsureNetworkReadyForCoordinatorAsync()
    {
        if (!await EnsureNetworkReadyBeforeLaunchAsync())
        {
            throw new InvalidOperationException(L(
                "公网隧道未准备好，请稍后重试。",
                "Public tunnel is not ready. Please try again shortly."));
        }

        return _assignedIp;
    }

    private void ReportGameLaunchStage(GameLaunchStage stage)
    {
        string text = stage switch
        {
            GameLaunchStage.ValidatingGame => L("检查游戏...", "Validating game..."),
            GameLaunchStage.CleaningConflicts => L("清理冲突...", "Cleaning conflicts..."),
            GameLaunchStage.ConfirmingNetwork => L("确认网络...", "Confirming network..."),
            GameLaunchStage.ConnectingServer => L("连接服务器...", "Connecting server..."),
            GameLaunchStage.CheckingVirtualLan => L("检查虚拟局域网...", "Checking virtual LAN..."),
            GameLaunchStage.DeployingComponents => L("部署组件...", "Deploying components..."),
            GameLaunchStage.WritingConfiguration => L("写入配置...", "Writing config..."),
            GameLaunchStage.StartingGame => L("启动游戏...", "Starting game..."),
            _ => L("准备启动...", "Preparing...")
        };
        SetBusy(true, text);
    }

    private void ApplyResponsiveLanguageLayout()
    {
        if (btnLaunch != null)
            btnLaunch.Width = IsEnglish ? 286 : 230;

        if (bottomStatusGrid == null)
            return;

        bottomPlayersColumn.Width = new GridLength(IsEnglish ? 170 : 126);
        bottomNetworkColumn.Width = new GridLength(IsEnglish ? 155 : 100);
        bottomLatencyColumn.Width = new GridLength(IsEnglish ? 110 : 100);
        bottomStatusGrid.Width = IsEnglish ? 451 : 342;
    }

    private void UpdateLaunchButtonAvailability()
    {
        if (btnLaunch == null)
            return;

        // Ending an already running game must remain available even if the
        // network drops. Starting a new game, however, is allowed only after
        // the complete network lifecycle has reached Connected with a valid
        // SCBL virtual address. Keep the button disabled throughout the launch
        // hand-off so a second click cannot cancel the bootstrap process while
        // Ubisoft/Steam is replacing the short-lived initial game process.
        btnLaunch.IsEnabled = GetInteractionState().IsLaunchEnabled;
    }

    private void UpdateLaunchConfigurationAvailability()
    {
        if (cmbGameplayMode == null)
            return;

        LauncherConfigurationAvailability availability = _configurationAvailabilityService.Get(
            GetInteractionState(),
            GetPreferredGameplayMode(),
            GetServerDescriptorObservationId());
        cmbGameExecutable.IsEnabled = availability.IsRendererConfigurationEnabled;
        cmbGameplayMode.IsEnabled = availability.IsGameplayConfigurationEnabled;
        itemModMode.IsEnabled = availability.IsModSelectable;
        int effectiveGameplayModeIndex = availability.EffectiveGameplayMode == GameplayMode.Mod
            ? 1
            : 0;
        if (cmbGameplayMode.SelectedIndex != effectiveGameplayModeIndex)
        {
            _suppressGameplayModePresentation = true;
            try
            {
                cmbGameplayMode.SelectedIndex = effectiveGameplayModeIndex;
            }
            finally
            {
                _suppressGameplayModePresentation = false;
            }
        }
    }

    private LauncherInteractionState GetInteractionState()
        => LauncherInteractionPolicy.Evaluate(new LauncherInteractionInput(
            IsBusy: _isBusy,
            IsLaunchFlowActive: _isLaunchFlowActive,
            IsUpdating: _isUpdating,
            IsGameStarting: _isGameStarting,
            IsGameRunning: _isGameRunning,
            IsStartupRestartAvailable: _startupRestartAvailable,
            IsEndingGame: _isEndingGame,
            IsNetworkReady: _networkReady,
            HasValidClientIp: NetworkHealthCheckService.IsValidScblClientIp(_assignedIp),
            IsNetworkCheckCoolingDown: _networkCheckButtonCoolingDown,
            IsNetworkCheckInProgress: _networkCheckInProgress));


    private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = IsEnglish ? "zh-CN" : "en-US";
        _settingsService.Save(_settings);
        ApplyLocalization();
        if (serverSettingsOverlay.Visibility == Visibility.Visible)
            ApplyServerSettingsLocalization(saved: !btnServerSettingsSave.IsEnabled);
        if (guideOverlay.Visibility == Visibility.Visible)
            RefreshGuideStep();
    }

    private void MusicToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.MusicEnabled = !_settings.MusicEnabled;
        _settingsService.Save(_settings);
        UpdateMusicButton();
        if (!_settings.MusicEnabled)
            StopMusic();
        else if (!_launcherMediaFrozenForGameSession)
            PlayStartupMusicIfEnabled(forceReplay: true);
    }

    private void UpdateMusicButton()
    {
        UpdateSettingsMenuText();
    }

    private void PauseMusicForGameSession()
    {
        if (_launcherMediaFrozenForGameSession)
            return;

        _launcherMediaFrozenForGameSession = true;
        if (!_musicIsPlaying)
            return;

        try
        {
            _musicPlayer.Pause();
            _musicIsPlaying = false;
            LogService.Info("Launcher BGM paused for the game session; it will not resume until launcher restart.");
        }
        catch (Exception ex)
        {
            LogService.Warning($"Failed to pause launcher BGM: {ex.Message}");
        }
    }

    private void PlayStartupMusicIfEnabled(bool forceReplay = false)
    {
        if (!_settings.MusicEnabled || _launcherMediaFrozenForGameSession)
            return;
        if (_musicPlayedThisSession && !forceReplay)
            return;

        try
        {
            string? musicPath = ResolveLauncherMusicPath();
            if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath))
                return;

            if (forceReplay)
                StopMusic();

            _musicPlayer.Open(new Uri(musicPath, UriKind.Absolute));
            _musicPlayer.Volume = 0.30;
            _musicPlayer.MediaEnded -= MusicPlayer_MediaEnded;
            _musicPlayer.MediaEnded += MusicPlayer_MediaEnded;
            _musicPlayer.Play();
            _musicPlayedThisSession = true;
            _musicIsPlaying = true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to play launcher music: {ex.Message}");
        }
    }

    private void MusicPlayer_MediaEnded(object? sender, EventArgs e)
    {
        _musicIsPlaying = false;
        StopMusic();
    }

    private string? ResolveLauncherMusicPath()
    {
        string baseDir = GetLauncherBaseDirectory();
        foreach (string candidate in new[]
        {
            Path.Combine(baseDir, "launcher_bgm.mp3"),
            Path.Combine(baseDir, "launcher_bgm.wav"),
            Path.Combine(baseDir, "bgm.mp3"),
            Path.Combine(baseDir, "bgm.wav")
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            string embeddedName = EmbeddedResourceService.EmbeddedFileExists("launcher_bgm.mp3")
                ? "launcher_bgm.mp3"
                : EmbeddedResourceService.EmbeddedFileExists("launcher_bgm.wav") ? "launcher_bgm.wav" : "";
            if (string.IsNullOrWhiteSpace(embeddedName))
                return null;
            string tempDir = Path.Combine(LogService.RuntimeDirectory, "media");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, embeddedName);
            EmbeddedResourceService.ExtractEmbeddedFileStrict(embeddedName, tempPath);
            _musicTempPath = tempPath;
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private void StopMusic()
    {
        try
        {
            _musicPlayer.Volume = 0;
            _musicPlayer.Stop();
            _musicPlayer.Close();
            _musicPlayer.MediaEnded -= MusicPlayer_MediaEnded;
            _musicIsPlaying = false;
        }
        catch { }
    }

    private void ForceEnglishInputForPlainTextBoxes()
    {
        InputMethod.SetIsInputMethodEnabled(txtUsername, false);
        InputMethod.SetPreferredImeState(txtUsername, InputMethodState.Off);
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.AlphanumericHalfWidth));
        txtUsername.InputScope = scope;
    }

    private void UsernameTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !IsValidUsernameInput(e.Text);

    private void UsernameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUsernameTextChanged) return;
        string original = txtUsername.Text;
        string cleaned = FilterUsernameText(original);
        if (cleaned == original)
        {
            EnsurePeerProbeStarted();
            return;
        }
        int caret = Math.Min(cleaned.Length, txtUsername.CaretIndex);
        _suppressUsernameTextChanged = true;
        txtUsername.Text = cleaned;
        txtUsername.CaretIndex = caret;
        _suppressUsernameTextChanged = false;
        EnsurePeerProbeStarted();
    }

    private void UsernameTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        string text = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (FilterUsernameText(text) != text) e.CancelCommand();
    }

    private void PasswordBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(char.IsWhiteSpace);

    private void PasswordBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        string text = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (text.Any(char.IsWhiteSpace)) e.CancelCommand();
    }

    private string GetPasswordInput()
        => _passwordVisible ? txtPasswordVisible.Text : txtPassword.Password;

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_passwordVisible)
            return;
        UpdatePasswordPlaceholder();
    }

    private void PasswordVisibleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_passwordVisible)
            UpdatePasswordPlaceholder();
    }

    private void UpdatePasswordPlaceholder()
    {
        if (txtPasswordPlaceholder is null)
            return;

        txtPasswordPlaceholder.Visibility = string.IsNullOrEmpty(GetPasswordInput())
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdatePasswordVisibilityAccessibility()
    {
        if (btnPasswordVisibility is null)
            return;

        string label = _passwordVisible
            ? L("隐藏密码", "Hide password")
            : L("显示密码", "Show password");
        btnPasswordVisibility.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(btnPasswordVisibility, label);
    }

    private void PasswordVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        if (_passwordVisible)
        {
            txtPasswordVisible.Text = txtPassword.Password;
            txtPassword.Visibility = Visibility.Collapsed;
            txtPasswordVisible.Visibility = Visibility.Visible;
            txtPasswordVisible.Focus();
            txtPasswordVisible.CaretIndex = txtPasswordVisible.Text.Length;
            btnPasswordVisibility.Opacity = 1;
        }
        else
        {
            txtPassword.Password = txtPasswordVisible.Text;
            txtPasswordVisible.Visibility = Visibility.Collapsed;
            txtPassword.Visibility = Visibility.Visible;
            txtPassword.Focus();
            btnPasswordVisibility.Opacity = 0.72;
        }

        UpdatePasswordPlaceholder();
        UpdatePasswordVisibilityAccessibility();
    }

    private static bool IsValidUsernameInput(string text)
        => text.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.');

    private static string FilterUsernameText(string value)
        => new(value.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.').ToArray());


    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void WindowMinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void WindowCloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void BringLauncherToFront(string reason)
    {
        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            LogService.Info($"Launcher brought to front: {reason}");
        }
        catch { }
    }

    private void LoadSettingsToUi()
    {
        _settings = _settingsService.Load();
        txtUsername.Text = _settings.Username;
        txtPassword.Password = _settings.Password;
        txtPasswordVisible.Text = _settings.Password;
        UpdatePasswordPlaceholder();
        cmbGameExecutable.SelectedIndex = _settings.GameExecutable.Equals("Blacklist_DX11_game.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        cmbGameplayMode.SelectedIndex = GetPreferredGameplayModeIndex();
        _settingsLoaded = true;
        _assignedIp = _settings.LastAssignedVirtualIp;
        Directory.CreateDirectory(LogService.LogDirectory);
    }

    private string GetSelectedGameExecutable()
        => cmbGameExecutable.SelectedIndex == 1 ? "Blacklist_DX11_game.exe" : "Blacklist_game.exe";

    private string GetSelectedGameLabel()
        => cmbGameExecutable.SelectedIndex == 1 ? "DX11" : "DX9";

    private string GetSelectedGameplayMode()
        => cmbGameplayMode.SelectedIndex == 1
            ? GameplayModeNames.Mod
            : GameplayModeNames.Official;

    private string GetPreferredGameplayMode()
        => GameplayModeNames.ParseOrOfficial(_settings.GameplayMode) == GameplayMode.Mod
            ? GameplayModeNames.Mod
            : GameplayModeNames.Official;

    private int GetPreferredGameplayModeIndex()
        => GetPreferredGameplayMode() == GameplayModeNames.Mod ? 1 : 0;

    private void SetGameStartingState(string gameExecutable)
    {
        _isGameStarting = true;
        _isGameRunning = false;
        _startupRestartAvailable = false;
        RefreshLaunchButtonTextFromState();
        UpdateLaunchConfigurationAvailability();
        UpdateLaunchButtonAvailability();
        StartGameLatencyMonitor();
        StartGameNetworkContinuityMonitor();
        LogService.Info($"Game launch command sent. Waiting for actual game process: {gameExecutable}, sessionVirtualIp={_gameSessionVirtualIp}");
    }

    private void SetStartupRestartAvailable(bool available)
    {
        if (!_isGameStarting)
            available = false;
        if (_startupRestartAvailable == available)
            return;

        _startupRestartAvailable = available;
        RefreshLaunchButtonTextFromState();
        UpdateLaunchButtonAvailability();
        LogService.Info(available
            ? "Verified game PID remained alive; startup restart action is now available."
            : "Startup restart action is no longer available while waiting for a verified game PID.");
    }

    private void SetGameRunningState(bool running)
    {
        _isGameRunning = running;
        _isGameStarting = false;
        _startupRestartAvailable = false;
        if (!running)
            _isBusy = false;
        RefreshLaunchButtonTextFromState();
        UpdateLaunchConfigurationAvailability();
        UpdateCheckNetworkButtonAvailability();
        UpdateLaunchButtonAvailability();
        if (running)
        {
            StartGameLatencyMonitor();
            StartGameNetworkContinuityMonitor();
        }
        else
        {
            StopGameLatencyMonitor();
            StopGameNetworkContinuityMonitor();
        }
        RefreshServerStatusTextFromKind();

    }

    private static bool IsNonUdpServerTransport(string? transport)
    {
        if (string.IsNullOrWhiteSpace(transport))
            return false;
        string value = transport.Trim();
        return !value.Equals("UDP", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("IPv4 UDP", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("IPv6 UDP", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyGameNetworkObservation(GameNetworkObservation observation)
    {
        bool routeChanged = _gameLatencyActive != observation.Active
            || _localIsGameHost != observation.LocalIsHost
            || !_gamePeerIp.Equals(observation.PeerIp, StringComparison.OrdinalIgnoreCase)
            || !_lastGameTransport.Equals(observation.Transport, StringComparison.OrdinalIgnoreCase)
            || !_lastGameAddressFamily.Equals(observation.AddressFamily, StringComparison.OrdinalIgnoreCase)
            || _lastGameNextHop != observation.NextHop
            || _lastGameHopCount != observation.HopCount
            || _gameSessionId != observation.SessionId;

        _gameLatencyActive = observation.Active;
        _localIsGameHost = observation.LocalIsHost;
        _gamePeerIp = observation.PeerIp;
        _lastGameLatencyMs = observation.LatencyMs;
        _lastGameTransport = observation.Transport;
        _lastGameAddressFamily = observation.AddressFamily;
        _lastGameNextHop = observation.NextHop;
        _lastGameHopCount = observation.HopCount;
        _gameActivePeerCount = observation.ActivePeerCount;
        _gameRoleSource = observation.RoleSource;
        _gameHostUsername = observation.HostUsername;
        _gameSessionId = observation.SessionId;
        _gameLatencyP50Ms = observation.LatencyP50Ms;
        _gameLatencyP95Ms = observation.LatencyP95Ms;
        _gameJitterMs = observation.JitterMs;
        _gameLossPercent = observation.LossPercent;
        if (_localIsGameHost && observation.LatencyMs.HasValue)
            _lastServerLatencyMs = observation.LatencyMs;

        if (routeChanged && observation.Active)
        {
            string target = observation.LocalIsHost ? PublicServerAddress : observation.PeerIp;
            LogService.Info(
                $"Game path updated: source={observation.RoleSource}, session={observation.SessionId?.ToString() ?? "n/a"}, " +
                $"peer={target}, players={observation.ActivePeerCount}, transport={observation.Transport}, " +
                $"underlay={observation.AddressFamily}, latency={observation.LatencyMs?.ToString() ?? "n/a"}ms, " +
                $"nextHop={observation.NextHop}, hops={observation.HopCount?.ToString() ?? "n/a"}.");
        }
        RefreshServerStatusTextFromKind();
    }

}
