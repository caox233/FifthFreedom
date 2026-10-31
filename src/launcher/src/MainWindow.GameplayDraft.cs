using System.Globalization;
using System.IO;
using System.Windows;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private GameplaySettingsDraft? _gameplayDraft;
    private bool _gameplayControlsReady;
    private bool _gameplayDraftActive => _gameplayDraft?.IsActive == true;

    private bool CanEditGameplayDraft => _gameplayControlsReady && !_closingBackgroundTasks && !_networkEnvironmentClosing
        && !_networkEnvironmentChanging && !_isBusy && !_toolsOperation && !_publicSettingsSaving
        && !_isGameStarting && !_isGameRunning && !_gameLaunchService.SessionId.HasValue;

    private void BeginGameplayDraft()
    {
        if (_gameplayDraftActive) return;
        _gameplayDraft = new GameplaySettingsDraft(_settings);
        RestoreGameplayControls(_gameplayDraft.Mode, _gameplayDraft.Fov);
        SetGameplayDraftMessage("");
        RenderGameplaySummary();
    }

    private void CancelGameplayDraft()
    {
        _gameplayDraft?.Cancel();
        _gameplayDraft = null;
        RestoreGameplayControls(GameplayRuntime.Normalize(_settings.GameplayMode), GameplayRuntime.NormalizeFov(_settings.FovScale));
        SetGameplayDraftMessage("");
        RenderGameplaySummary();
    }

    private void RestoreGameplayControls(string mode, double fov)
    {
        bool previous = _restoringInteractiveSetting;
        _restoringInteractiveSetting = true;
        try
        {
            if (cmbGameplayMode != null) cmbGameplayMode.SelectedValue = mode;
            if (fovSlider != null) fovSlider.Value = fov;
            if (txtFovValue != null) txtFovValue.Text = fov.ToString("0.00", CultureInfo.InvariantCulture) + "×";
        }
        finally { _restoringInteractiveSetting = previous; }
    }

    private void SetGameplayDraftMessage(string message)
    {
        if (txtGameplaySettingsMessage == null) return;
        txtGameplaySettingsMessage.Text = message;
        txtGameplaySettingsMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HandleGameplayDraftChanged()
    {
        if (_restoringInteractiveSetting || !_gameplayControlsReady) return;
        if (!CanEditGameplayDraft)
        {
            RestoreGameplayControls(_gameplayDraftActive ? _gameplayDraft!.Mode : GameplayRuntime.Normalize(_settings.GameplayMode),
                _gameplayDraftActive ? _gameplayDraft!.Fov : GameplayRuntime.NormalizeFov(_settings.FovScale));
            RenderGameplaySummary();
            return;
        }
        // The direct main-screen control has already changed. Capture the saved
        // values as the cancel target without overwriting this first edit.
        _gameplayDraft ??= new GameplaySettingsDraft(_settings);
        _gameplayDraft.Update(cmbGameplayMode.SelectedValue as string, fovSlider.Value);
        SetGameplayDraftMessage("");
        RenderGameplaySummary();
    }

    private void RenderGameplaySummary()
    {
        bool dirty = _gameplayDraftActive && _gameplayDraft!.IsDirty;
        if (_gameplayControlsReady)
            cmbGameplayMode.IsEnabled = fovSlider.IsEnabled = btnFovReset.IsEnabled = CanEditGameplayDraft;
        if (gameplayDraftActions != null) gameplayDraftActions.Visibility = dirty ? Visibility.Visible : Visibility.Hidden;
        if (btnGameplaySave != null)
        {
            btnGameplaySave.Content = L("保存", "Save");
            btnGameplaySave.IsEnabled = dirty && CanEditGameplayDraft;
        }
        if (btnGameplayCancel != null)
        {
            btnGameplayCancel.Content = L("取消", "Cancel");
            btnGameplayCancel.IsEnabled = dirty && CanEditGameplayDraft;
        }
        UpdateLaunchButtonAvailability();
    }

    private void SaveGameplayDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!_gameplayDraftActive || !CanEditGameplayDraft || toolsOverlay.Visibility == Visibility.Visible
            || localDiscoveryOverlay.Visibility == Visibility.Visible || _dialogTcs != null) return;
        try
        {
            _gameplayDraft!.Update(cmbGameplayMode.SelectedValue as string, fovSlider.Value);
            if (App.PreviewMode) _gameplayDraft.CommitPreview(_settings);
            else _gameplayDraft.Commit(_settings, _settingsService.Save);
            _gameplayDraft = null;
            RestoreGameplayControls(GameplayRuntime.Normalize(_settings.GameplayMode), GameplayRuntime.NormalizeFov(_settings.FovScale));
            SetGameplayDraftMessage("");
            cmbGameplayMode.Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetGameplayDraftMessage(L("保存失败，原设置未改变。请检查写入权限后重试。", "Save failed; previous settings kept. Check write access and retry."));
            if (!App.PreviewMode) LogService.Error("Gameplay draft save failed: " + error.GetType().Name);
        }
        catch (InvalidOperationException)
        {
            SetGameplayDraftMessage(L("原设置已变化，请取消草稿后重试。", "Saved settings changed. Cancel this draft and retry."));
        }
        RenderGameplaySummary();
    }

    private void CancelGameplayDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditGameplayDraft) return;
        CancelGameplayDraft();
        cmbGameplayMode.Focus();
    }
}
