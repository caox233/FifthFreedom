using System.Globalization;
using System.IO;
using System.Windows;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private GameplaySettingsDraft? _gameplayDraft;
    private bool _gameplayDraftActive => _gameplayDraft?.IsActive == true;

    private bool CanEditGameplayDraft => !_closingBackgroundTasks && !_networkEnvironmentClosing
        && !_networkEnvironmentChanging && !_isBusy && !_toolsOperation && !_publicSettingsSaving
        && !_isGameStarting && !_isGameRunning && !_gameLaunchService.SessionId.HasValue;

    private void BeginGameplayDraft()
    {
        if (_gameplayDraftActive) return;
        _gameplayDraft = new GameplaySettingsDraft(_settings);
        RestoreGameplayControls(_gameplayDraft.Mode, _gameplayDraft.Fov);
        txtGameplaySettingsMessage.Text = "";
        RenderGameplaySummary();
    }

    private void CancelGameplayDraft()
    {
        _gameplayDraft?.Cancel();
        _gameplayDraft = null;
        RestoreGameplayControls(GameplayRuntime.Normalize(_settings.GameplayMode), GameplayRuntime.NormalizeFov(_settings.FovScale));
        if (txtGameplaySettingsMessage != null) txtGameplaySettingsMessage.Text = "";
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

    private void HandleGameplayDraftChanged()
    {
        if (_restoringInteractiveSetting || !_gameplayDraftActive) return;
        _gameplayDraft!.Update(cmbGameplayMode.SelectedValue as string, fovSlider.Value);
        txtGameplaySettingsMessage.Text = L("更改尚未保存。", "Changes have not been saved.");
        RenderGameplaySummary();
    }

    private void RenderGameplaySummary()
    {
        if (txtGameplaySummary != null)
        {
            string mode = GameplayRuntime.Normalize(_settings.GameplayMode) == "MOD" ? "MOD" : L("官方", "Official");
            txtGameplaySummary.Text = mode + " · FOV " + GameplayRuntime.NormalizeFov(_settings.FovScale).ToString("0.00", CultureInfo.InvariantCulture) + "×";
        }
        if (btnGameplaySettingsPage != null) btnGameplaySettingsPage.Content = L("MOD 与 FOV", "MOD / FOV");
        if (btnGameplaySave != null)
        {
            btnGameplaySave.Content = L("确认并保存", "Confirm and save");
            btnGameplaySave.IsEnabled = _gameplayDraftActive && _gameplayDraft!.IsDirty && CanEditGameplayDraft;
        }
        if (btnGameplayCancel != null) btnGameplayCancel.Content = L("取消", "Cancel");
    }

    private void GameplaySettingsPage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditGameplayDraft || _dialogTcs != null) return;
        ShowTools("gameplay");
        if (_toolsPage == "gameplay" && toolsOverlay.Visibility == Visibility.Visible) BeginGameplayDraft();
    }

    private void SaveGameplayDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!_gameplayDraftActive || !CanEditGameplayDraft || _toolsPage != "gameplay"
            || toolsOverlay.Visibility != Visibility.Visible) return;
        try
        {
            _gameplayDraft!.Update(cmbGameplayMode.SelectedValue as string, fovSlider.Value);
            if (App.PreviewMode) _gameplayDraft.CommitPreview(_settings);
            else _gameplayDraft.Commit(_settings, _settingsService.Save);
            // Keep the page editable after saving, with the new committed values
            // as its cancel target. Launch/presence reads still use _settings.
            _gameplayDraft = new GameplaySettingsDraft(_settings);
            RestoreGameplayControls(_gameplayDraft.Mode, _gameplayDraft.Fov);
            txtGameplaySettingsMessage.Text = L("已保存。", "Saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            txtGameplaySettingsMessage.Text = L("保存失败，原设置未改变。请检查目录写入权限后重试，或取消本次更改。",
                "Save failed; the previous settings are unchanged. Check folder write permission and retry, or cancel these changes.");
            if (!App.PreviewMode) LogService.Error("Gameplay draft save failed: " + error.GetType().Name);
        }
        catch (InvalidOperationException)
        {
            txtGameplaySettingsMessage.Text = L("已保存的设置发生变化。请取消本次草稿后重新编辑。",
                "The saved settings changed. Cancel this draft and reopen it before editing.");
        }
        RenderGameplaySummary();
    }

    private void CancelGameplayDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsOperation) return;
        CancelGameplayDraft();
        ShowTools("home");
    }
}
