using SplinterCellCNLauncher.Services;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private async void OverwriteSavesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        await ConfirmAndOverwriteFullUnlockSavesAsync(firstRun: false);
    }

    private async Task ConfirmAndOverwriteFullUnlockSavesAsync(bool firstRun)
    {
        if (firstRun && _settings.SaveOverwritePromptHandled)
            return;

        try
        {
            if (firstRun)
            {
                _settings.SaveOverwritePromptHandled = true;
                _settingsService.Save(_settings);
                if (!_saveGameService.HasExistingSaves())
                {
                    LogService.Info("First-run save overwrite prompt handled: no existing saves found.");
                    return;
                }
            }

            if (_isGameStarting || _isGameRunning || IsAnyBlacklistGameProcessRunning())
            {
                await ShowInfoDialogAsync(
                    L("请先关闭游戏", "Close the Game First"),
                    L("请先关闭游戏，再覆盖存档。", "Close the game before overwriting saves."));
                return;
            }

            MessageBoxResult first = await ShowTimedConfirmDialogAsync(
                title: firstRun
                    ? L("检测到已有本地存档", "Existing Saves Detected")
                    : L("覆盖全解锁存档", "Overwrite with Full-Unlock Saves"),
                message: L(
                    "当前存档将被全解锁存档替换，并自动备份。是否继续？",
                    "Current saves will be backed up and replaced with full-unlock saves. Continue?"),
                yesText: L("继续", "Continue"),
                noText: L("取消", "Cancel"),
                seconds: 5);
            if (first != MessageBoxResult.Yes)
                return;

            MessageBoxResult second = await ShowTimedConfirmDialogAsync(
                title: L("确认覆盖存档", "Confirm Save Overwrite"),
                message: L(
                    "当前存档将被替换。备份会保留，是否确认覆盖？",
                    "Current saves will be replaced and the backup kept. Confirm overwrite?"),
                yesText: L("继续覆盖", "Overwrite"),
                noText: L("取消", "Cancel"),
                seconds: 5);
            if (second != MessageBoxResult.Yes)
                return;

            string backupDir = _saveGameService.BackupExistingSaves();
            _saveGameService.DeployBaseSavesOverwrite();
            LogService.Info($"Full-unlock saves deployed: source={(firstRun ? "first-run" : "settings")}, backup={backupDir}");
            await ShowInfoDialogAsync(
                L("存档已覆盖", "Saves Overwritten"),
                L("全解锁存档已写入。备份位置：\n", "Full-unlock saves deployed. Backup:\n") + backupDir);
        }
        catch (Exception ex)
        {
            LogService.Error($"Save overwrite failed: source={(firstRun ? "first-run" : "settings")}, error={ex}");
            await ShowInfoDialogAsync(
                L("存档覆盖失败", "Save Overwrite Failed"),
                L("请关闭游戏并检查存档目录权限，然后重试。",
                  "Close the game, check access to the save folder, and try again."));
        }
    }
}
