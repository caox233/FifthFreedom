using SplinterCellCNLauncher.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _diagnosticPromptActive;
    private bool _diagnosticExportInProgress;

    private async void ExportDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;
        if (_diagnosticPromptActive || _diagnosticExportInProgress || _allowClose)
            return;
        _diagnosticPromptActive = true;
        try
        {
            var result = await ShowConfirmDialogAsync(
                title: L("导出诊断信息", "Export Diagnostics"),
                message: L(
                    "将生成诊断包并打开保存位置。密码和密钥不会包含在内。",
                    "A diagnostic package will be created and its location opened. Passwords and secrets are excluded."),
                yesText: L("导出诊断", "Export"),
                noText: L("取消", "Cancel"));
            if (result == MessageBoxResult.Yes)
                await ExportDiagnosticsAsync();
        }
        finally
        {
            _diagnosticPromptActive = false;
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (_diagnosticExportInProgress)
            return;

        _diagnosticExportInProgress = true;
        try
        {
            string zipPath = await _diagnosticExportService.ExportAsync(
                GetLauncherBaseDirectory(),
                LauncherVersion,
                _assignedIp,
                _gameDir,
                _isGameRunning || _isGameStarting);
            LogService.Info("Diagnostic bundle exported: " + zipPath);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zipPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogService.Info("Could not open diagnostic bundle location: " + ex.Message);
            }

            await ShowInfoDialogAsync(
                L("诊断信息已导出", "Diagnostics Exported"),
                L($"诊断包已生成：\n{zipPath}",
                  $"Diagnostic bundle created:\n{zipPath}"));
        }
        catch (Exception ex)
        {
            LogService.Error("Diagnostic export failed: " + ex);
            await ShowInfoDialogAsync(
                L("导出失败", "Export Failed"),
                L("未能生成诊断包，请重试。",
                  "The diagnostic package could not be created. Try again."));
        }
        finally
        {
            _diagnosticExportInProgress = false;
        }
    }
}
