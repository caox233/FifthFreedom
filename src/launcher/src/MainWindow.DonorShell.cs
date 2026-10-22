using System.Windows;

namespace SplinterCellCNLauncher;

// Presentation-only adapters for the SCBL 3.0 menu. Existing LAN operations own
// authentication, launch preparation, recovery, saves, diagnostics and lifecycle.
public partial class MainWindow
{
    private void OpenShellSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_closingBackgroundTasks || _isBusy || _toolsOperation || _dialogTcs != null)
            return;

        settingsMenu.PlacementTarget = btnShellMenu;
        settingsMenu.IsOpen = true;
    }

    private void ShellSettingsMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Also gate keyboard/right-click opening, which bypasses the button Click.
        if (_closingBackgroundTasks || _isBusy || _toolsOperation || _dialogTcs != null)
        {
            settingsMenu.IsOpen = false;
            return;
        }

        // A ContextMenu lives in a separate visual tree. Refresh its presentation
        // from existing localized controls instead of rebinding account state.
        miGuide.Header = btnGuide.ToolTip;
        miLanguageToggle.Header = "中文 / English";
        miMusicToggle.Header = btnMusicToggle.ToolTip;
        miLanSettings.Header = L("LAN / OB 设置", "LAN / OB settings");
        miRestoreSaves.Header = btnSavesPage.Content;
        miPlayers.Header = btnPlayersPage.Content;
        miExportDiagnostics.Header = btnExportDiagnostics.Content;
        miAdvancedDiagnostics.Header = btnAdvancedDiagnosticsPage.Content;
    }

    private void ExportDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
        => ExportDiagnostics_Click(sender, e);
}
