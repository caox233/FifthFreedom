using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _modeSelectionUiInitialized;
    private bool _suppressNetworkModeSelection;
    private bool _suppressGameplayModePresentation;
    private bool _networkModeUiChanging;
    private bool _modSelectionPreparing;
    private int _startupCompatibleModeOfferState;
    private int _confirmedGameplayModeIndex;
    private TaskCompletionSource<bool>? _firstRunFovPromptTcs;

    private void ModeSelectionPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_modeSelectionUiInitialized)
        {
            _modeSelectionUiInitialized = true;
            miLanguageToggle.Click += ModeSelectionLanguageToggle_Click;
        }

        _confirmedGameplayModeIndex = GetPreferredGameplayModeIndex();
        ApplyModeSelectionLocalization();
        SynchronizeNetworkModeSelection();
    }

    private void ModeSelectionLanguageToggle_Click(object sender, RoutedEventArgs e)
        => ApplyModeSelectionLocalization();

    private void ApplyModeSelectionLocalization()
    {
        if (txtNetworkModeLabel == null)
            return;

        txtNetworkModeLabel.Text = L("网络模式", "NETWORK MODE");
        txtGameplayModeLabel.Text = L("游戏模式", "GAME MODE");
        txtRendererLabel.Text = L("渲染模式", "RENDER MODE");

        string fastDescription = L(
            "默认连接方式，适合大多数网络。",
            "The default connection mode for most networks.");
        string compatibleDescription = L(
            "如遇启动器连接失败、游戏网络不稳定等网络问题，可尝试兼容模式。",
            "If the Launcher cannot connect or game networking is unstable, try Compatible mode.");
        string officialDescription = L(
            "官方模式使用联机核心，不加载玩法或诊断模组。",
            "Official mode uses the online core without gameplay or diagnostic mods.");
        string modDescription = L(
            "模组模式加载玩法 DLL，并默认启用已完成的玩法功能。",
            "MOD loads the gameplay DLL and enables all completed gameplay features.");

        ApplyChoiceAccessibility(itemFastNetworkMode, L("快速", "FAST"), fastDescription);
        ApplyChoiceAccessibility(itemCompatibleNetworkMode, L("兼容", "COMPATIBLE"), compatibleDescription);
        ApplyChoiceAccessibility(itemOfficialMode, L("官方", "Official"), officialDescription);
        ApplyChoiceAccessibility(itemModMode, L("模组", "MOD"), modDescription);

        AutomationProperties.SetName(cmbNetworkMode, L("网络模式", "Network mode"));
        AutomationProperties.SetHelpText(
            cmbNetworkMode,
            L("选择快速或兼容网络模式", "Choose Fast or Compatible network mode"));
        AutomationProperties.SetName(cmbGameplayMode, L("游戏模式", "Game mode"));
        AutomationProperties.SetHelpText(
            cmbGameplayMode,
            L(
                "选择官方或模组模式；两种模式都可以调整游戏视野",
                "Choose Official or MOD; field of view is adjustable in both modes"));
        AutomationProperties.SetName(cmbGameExecutable, L("渲染模式", "Render mode"));
        AutomationProperties.SetHelpText(
            cmbGameExecutable,
            L("选择 DX11 或 DX9 渲染模式", "Choose the DX11 or DX9 render mode"));

        txtModModeDialogTitle.Text = L("游戏视野设置", "Game Field of View");
        txtModModeDialogDescription.Text = L(
            "默认使用官方模式。视野范围适用于官方和模组模式，以后也可以点击当前游戏模式再次调整。",
            "Official is the default mode. Field of view applies to both Official and MOD, and can be adjusted later by selecting the current game mode again.");
        txtModFovLabel.Text = L("视野范围（FOV）", "Field of View (FOV)");
        txtModFovHint.Text = L(
            "范围 1.0–1.3，默认 1.2。此设置由 SCBL 单独应用。",
            "Range 1.0–1.3, default 1.2. SCBL applies this setting independently.");
        btnModFovCancel.Content = L("取消", "Cancel");
        btnModFovSave.Content = L("保存", "Save");
        AutomationProperties.SetName(modModeOverlay, L("游戏视野设置", "Game field of view settings"));
        AutomationProperties.SetName(modFovSlider, L("视野范围倍率", "Field of view multiplier"));
        AutomationProperties.SetHelpText(
            modFovSlider,
            L("范围 1.0 到 1.3，默认 1.2", "Range 1.0 to 1.3, default 1.2"));
        UpdateModFovValueText(modFovSlider.Value);
    }

    private static void ApplyChoiceAccessibility(ListBoxItem item, string label, string description)
    {
        item.Content = label;
        item.ToolTip = description;
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetHelpText(item, description);
    }

    private async void NetworkMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_modeSelectionUiInitialized || _suppressNetworkModeSelection || _networkModeUiChanging)
            return;

        ClientNetworkMode requestedMode = cmbNetworkMode.SelectedIndex == 1
            ? ClientNetworkMode.Compatible
            : ClientNetworkMode.Fast;
        NetworkModeState state = GetNetworkModeState();
        if (state.Mode == requestedMode)
            return;

        if (requestedMode == ClientNetworkMode.Compatible)
        {
            MessageBoxResult confirmation = await ShowNetworkModeConfirmDialogAsync(
                L("兼容模式", "Compatible Mode"),
                L(
                    "如遇启动器连接失败、游戏网络不稳定等网络问题，可尝试兼容模式。",
                    "If the Launcher cannot connect or game networking is unstable, try Compatible mode."),
                L("切换", "Switch"),
                L("返回", "Back"));
            if (confirmation != MessageBoxResult.Yes)
            {
                SynchronizeNetworkModeSelection();
                return;
            }
        }

        state = GetNetworkModeState();
        if (!state.CanChange)
        {
            await ShowNetworkModeUnavailableAsync(state);
            SynchronizeNetworkModeSelection();
            return;
        }

        _networkModeUiChanging = true;
        cmbNetworkMode.SetCurrentValue(IsEnabledProperty, false);
        try
        {
            NetworkModeChangeResult result = await ChangeNetworkModeAsync(requestedMode, CancellationToken.None);
            SynchronizeNetworkModeSelection(result.Mode);

            if (result.Outcome == NetworkModeChangeOutcome.RejectedGameActive)
            {
                await ShowInfoDialogAsync(
                    L("无法切换网络模式", "Unable to Change Network Mode"),
                    L("游戏运行期间无法切换网络模式，请退出游戏后重试。",
                        "Network mode cannot be changed while the game is running. Close the game and try again."));
            }
            else if (result.Outcome == NetworkModeChangeOutcome.ConnectionFailed)
            {
                await ShowInfoDialogAsync(
                    L("网络连接失败", "Network Connection Failed"),
                    L("已保留所选网络模式，但当前连接未恢复。请检查网络后重新检测。",
                        "The selected network mode was kept, but the connection was not restored. Check your network and run the check again."));
            }
        }
        catch (OperationCanceledException)
        {
            SynchronizeNetworkModeSelection();
        }
        catch (Exception ex)
        {
            LogService.Error("Network mode UI command failed: " + ex.GetType().Name);
            SynchronizeNetworkModeSelection();
            await ShowInfoDialogAsync(
                L("无法切换网络模式", "Unable to Change Network Mode"),
                L("请稍后重试。", "Try again shortly."));
        }
        finally
        {
            _networkModeUiChanging = false;
            NetworkModeState current = GetNetworkModeState();
            cmbNetworkMode.SetCurrentValue(IsEnabledProperty, current.CanChange);
        }
    }

    private async Task ShowNetworkModeUnavailableAsync(NetworkModeState state)
    {
        if (state.IsGameActive)
        {
            await ShowInfoDialogAsync(
                L("无法切换网络模式", "Unable to Change Network Mode"),
                L("游戏运行期间无法切换网络模式，请退出游戏后重试。",
                    "Network mode cannot be changed while the game is running. Close the game and try again."));
            return;
        }

        await ShowInfoDialogAsync(
            L("正在切换网络模式", "Changing Network Mode"),
            L("请稍候。", "Please wait."));
    }

    private async Task OfferCompatibleModeAfterFastStartupFailureAsync(
        NetworkReadyResult failure)
    {
        NetworkModeState initialState = GetNetworkModeState();
        if (failure.Ok
            || _allowClose
            || initialState.Mode != ClientNetworkMode.Fast
            || Interlocked.CompareExchange(ref _startupCompatibleModeOfferState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            NetworkModeState state = GetNetworkModeState();
            if (!state.CanChange || state.Mode != ClientNetworkMode.Fast)
                return;

            MessageBoxResult confirmation = await ShowNetworkModeConfirmDialogAsync(
                L("快速模式连接失败", "Fast Mode Connection Failed"),
                L(
                    "快速模式暂时无法连接。是否切换到兼容模式并重新连接？",
                    "Fast mode could not connect. Switch to Compatible mode and reconnect?"),
                L("切换兼容模式", "Use Compatible"),
                L("暂不切换", "Not Now"));
            if (confirmation != MessageBoxResult.Yes || _allowClose)
                return;

            _networkModeUiChanging = true;
            cmbNetworkMode.SetCurrentValue(IsEnabledProperty, false);
            NetworkModeChangeResult result = await ChangeNetworkModeAsync(
                ClientNetworkMode.Compatible,
                CancellationToken.None);
            SynchronizeNetworkModeSelection(result.Mode);
            if (result.Outcome == NetworkModeChangeOutcome.ConnectionFailed)
            {
                await ShowInfoDialogAsync(
                    L("兼容模式连接失败", "Compatible Mode Connection Failed"),
                    L(
                        "兼容模式也未能连接，已恢复快速模式。请检查网络后重新检测。",
                        "Compatible mode also failed, so Fast mode was restored. Check your network and try again."));
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Startup compatible-mode offer failed: " + ex.GetType().Name);
            SynchronizeNetworkModeSelection();
        }
        finally
        {
            _networkModeUiChanging = false;
            NetworkModeState current = GetNetworkModeState();
            cmbNetworkMode.SetCurrentValue(
                IsEnabledProperty,
                current.CanChange);
            Interlocked.Exchange(ref _startupCompatibleModeOfferState, 2);
        }
    }

    private void SynchronizeNetworkModeSelection(ClientNetworkMode? mode = null)
    {
        if (cmbNetworkMode == null)
            return;

        NetworkModeState state = GetNetworkModeState();
        ClientNetworkMode selectedMode = mode ?? state.Mode;
        _suppressNetworkModeSelection = true;
        try
        {
            cmbNetworkMode.SelectedIndex = selectedMode == ClientNetworkMode.Compatible ? 1 : 0;
        }
        finally
        {
            _suppressNetworkModeSelection = false;
        }
        cmbNetworkMode.SetCurrentValue(IsEnabledProperty, state.CanChange);
    }

    private async Task<MessageBoxResult> ShowNetworkModeConfirmDialogAsync(
        string title,
        string message,
        string yesText,
        string noText)
    {
        Task<MessageBoxResult> task = ShowConfirmDialogAsync(title, message, yesText, noText);
        btnDialogYes.Focus();
        Keyboard.Focus(btnDialogYes);
        return await task;
    }

    private async void GameplayModePresentation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_modeSelectionUiInitialized
            || !_settingsLoaded
            || _suppressGameplayModePresentation
            || _modSelectionPreparing)
            return;

        int requestedIndex = NormalizeGameplayModeIndex(cmbGameplayMode.SelectedIndex);
        if (requestedIndex == _confirmedGameplayModeIndex)
            return;

        if (requestedIndex == 1)
        {
            LauncherConfigurationAvailability availability = GetModSelectionAvailability();
            if (!availability.IsModSelectable)
            {
                RestoreConfirmedGameplayModeSelection();
                return;
            }

            if (!await PrepareAndShowModModeDialogAsync(availability))
                RestoreConfirmedGameplayModeSelection();
            return;
        }

        _confirmedGameplayModeIndex = 0;
        _settings.GameplayMode = GameplayModeNames.Official;
        SaveRuntimeSettings();
        UpdateLaunchConfigurationAvailability();
        ShowModModeDialog();
    }

    private void OfficialModeItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!itemOfficialMode.IsSelected || _modSelectionPreparing)
            return;

        e.Handled = true;
        ShowModModeDialog();
    }

    private void OfficialModeItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!itemOfficialMode.IsSelected
            || _modSelectionPreparing
            || (e.Key != Key.Enter && e.Key != Key.Space))
        {
            return;
        }

        e.Handled = true;
        ShowModModeDialog();
    }

    private async void ModModeItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!itemModMode.IsSelected || _modSelectionPreparing)
            return;

        LauncherConfigurationAvailability availability = GetModSelectionAvailability();
        if (!availability.IsModSelectable)
            return;

        e.Handled = true;
        await PrepareAndShowModModeDialogAsync(availability);
    }

    private async void ModModeItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!itemModMode.IsSelected
            || _modSelectionPreparing
            || (e.Key != Key.Enter && e.Key != Key.Space))
        {
            return;
        }

        LauncherConfigurationAvailability availability = GetModSelectionAvailability();
        if (!availability.IsModSelectable)
            return;

        e.Handled = true;
        await PrepareAndShowModModeDialogAsync(availability);
    }

    private LauncherConfigurationAvailability GetModSelectionAvailability()
        => _configurationAvailabilityService.Get(
            GetInteractionState(),
            GameplayModeNames.Mod,
            GetServerDescriptorObservationId());

    private async Task<bool> PrepareAndShowModModeDialogAsync(
        LauncherConfigurationAvailability availability)
    {
        if (_modSelectionPreparing)
            return false;

        _modSelectionPreparing = true;
        SetBusy(true, L("正在准备模组...", "Preparing MOD..."));
        try
        {
            await App.ModSelectionPreparation.PrepareAsync(
                _gameDir,
                availability,
                CancellationToken.None);

            _confirmedGameplayModeIndex = 1;
            _settings.GameplayMode = GameplayModeNames.Mod;
            SaveRuntimeSettings();
            ShowModModeDialog();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            await ShowFriendlyErrorDialogAsync(FriendlyErrorKind.HookFiles, ex);
            return false;
        }
        finally
        {
            _modSelectionPreparing = false;
            SetBusy(false);
            UpdateLaunchConfigurationAvailability();
        }
    }

    private void ShowModModeDialog()
    {
        double normalized = LauncherSettingsService.NormalizeModFovScale(_settings.ModFovScale);
        modFovSlider.Value = normalized;
        UpdateModFovValueText(normalized);
        modModeOverlay.Visibility = Visibility.Visible;
        modFovSlider.Focus();
        Keyboard.Focus(modFovSlider);
    }

    private async Task HandleFirstRunFovPromptAsync()
    {
        if (_settings.FovPromptHandled || _allowClose)
            return;

        _firstRunFovPromptTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BringLauncherToFront("show first-run FOV prompt");
        ShowModModeDialog();
        await _firstRunFovPromptTcs.Task;

        _settings.FovPromptHandled = true;
        SaveRuntimeSettings();
    }

    private void ModFovSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateModFovValueText(e.NewValue);

    private void UpdateModFovValueText(double value)
    {
        if (txtModFovValue == null)
            return;

        double normalized = LauncherSettingsService.NormalizeModFovScale(value);
        txtModFovValue.Text = normalized.ToString("0.00", CultureInfo.InvariantCulture) + "×";
        AutomationProperties.SetName(
            txtModFovValue,
            L($"当前视野范围倍率 {normalized:0.00}", $"Current field of view multiplier {normalized:0.00}"));
    }

    private void ModFovSaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ModFovScale = LauncherSettingsService.NormalizeModFovScale(modFovSlider.Value);
        SaveRuntimeSettings();
        CloseModModeDialog();
    }

    private void ModFovCancelButton_Click(object sender, RoutedEventArgs e)
        => CloseModModeDialog();

    private void ModModeOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CloseModModeDialog();
    }

    private void CloseModModeDialog()
    {
        modModeOverlay.Visibility = Visibility.Collapsed;
        TaskCompletionSource<bool>? firstRunPrompt = _firstRunFovPromptTcs;
        _firstRunFovPromptTcs = null;
        firstRunPrompt?.TrySetResult(true);
        ListBoxItem selectedItem = _confirmedGameplayModeIndex == 1
            ? itemModMode
            : itemOfficialMode;
        if (selectedItem.IsEnabled)
        {
            selectedItem.Focus();
            Keyboard.Focus(selectedItem);
        }
        else
        {
            cmbGameplayMode.Focus();
            Keyboard.Focus(cmbGameplayMode);
        }
    }

    private void RestoreConfirmedGameplayModeSelection()
    {
        _suppressGameplayModePresentation = true;
        try
        {
            cmbGameplayMode.SelectedIndex = _confirmedGameplayModeIndex;
        }
        finally
        {
            _suppressGameplayModePresentation = false;
        }
    }

    private static int NormalizeGameplayModeIndex(int index)
        => index == 1 ? 1 : 0;
}
