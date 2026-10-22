using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        string username = txtUsername.Text.Trim();
        txtRecoveryUsername.Text = username;
        txtRecoveryCode.Text = username.Equals(_settings.RecoveryCodeUsername, StringComparison.OrdinalIgnoreCase)
            ? _settings.RecoveryCode
            : string.Empty;
        txtRecoveryNewPassword.Password = string.Empty;
        txtRecoveryConfirmPassword.Password = string.Empty;
        txtRecoveryMessage.Text = string.Empty;
        txtRecoveryTitle.Text = L("使用恢复码", "Use Recovery Code");
        txtRecoveryDescription.Text = L(
            "输入恢复码并设置新密码。完成后会生成新的恢复码。",
            "Enter your recovery code and a new password. A new recovery code will be issued.");
        txtRecoveryUsernameLabel.Text = L("账号", "Account");
        txtRecoveryCodeLabel.Text = L("恢复码", "Recovery code");
        txtRecoveryNewPasswordLabel.Text = L("新密码", "New password");
        txtRecoveryConfirmPasswordLabel.Text = L("确认新密码", "Confirm password");
        btnRecoveryCancel.Content = L("取消", "Cancel");
        btnRecoverySubmit.Content = L("重置密码", "Reset Password");
        passwordRecoveryOverlay.Visibility = Visibility.Visible;
        txtRecoveryUsername.Focus();
        txtRecoveryUsername.CaretIndex = txtRecoveryUsername.Text.Length;
    }

    private void RecoveryCancelButton_Click(object sender, RoutedEventArgs e)
    {
        passwordRecoveryOverlay.Visibility = Visibility.Collapsed;
        txtRecoveryNewPassword.Password = string.Empty;
        txtRecoveryConfirmPassword.Password = string.Empty;
        txtRecoveryMessage.Text = string.Empty;
    }

    private async void RecoverySubmitButton_Click(object sender, RoutedEventArgs e)
    {
        string username = txtRecoveryUsername.Text.Trim();
        string recoveryCode = txtRecoveryCode.Text.Trim();
        string newPassword = txtRecoveryNewPassword.Password;
        string confirmation = txtRecoveryConfirmPassword.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(recoveryCode))
        {
            txtRecoveryMessage.Text = L("请输入账号和恢复码。", "Enter the account and recovery code.");
            return;
        }

        if (newPassword.Length is < 6 or > 128)
        {
            txtRecoveryMessage.Text = L("新密码需为 6–128 个字符。", "The new password must be 6–128 characters.");
            return;
        }

        if (!newPassword.Equals(confirmation, StringComparison.Ordinal))
        {
            txtRecoveryMessage.Text = L("两次输入的密码不一致。", "The passwords do not match.");
            return;
        }

        btnRecoverySubmit.IsEnabled = false;
        btnRecoveryCancel.IsEnabled = false;
        txtRecoveryMessage.Foreground = System.Windows.Media.Brushes.LightGray;
        txtRecoveryMessage.Text = L("正在重置…", "Resetting…");
        try
        {
            PasswordResetResult result = await _authService.ResetPasswordPublicAsync(
                username,
                recoveryCode,
                newPassword,
                _assignedIp);
            if (result.Status != PasswordResetStatus.Success)
            {
                txtRecoveryMessage.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF6, 0xA7, 0xAF));
                txtRecoveryMessage.Text = result.Status switch
                {
                    PasswordResetStatus.InvalidRecoveryCode => L("账号或恢复码不正确。", "The account or recovery code is incorrect."),
                    PasswordResetStatus.ConnectionFailed => L("暂时无法连接服务器。", "The server is currently unavailable."),
                    _ => L("请检查账号、恢复码和新密码。", "Check the account, recovery code, and new password.")
                };
                return;
            }

            _settings.Username = username;
            _settings.Password = newPassword;
            passwordRecoveryOverlay.Visibility = Visibility.Collapsed;
            await StoreAndShowRecoveryCodeAsync(username, result.RecoveryCode, isReplacement: true);
            txtUsername.Text = username;
            txtPassword.Password = newPassword;
            txtPasswordVisible.Text = newPassword;
            txtPasswordPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LogService.Error(ex);
            passwordRecoveryOverlay.Visibility = Visibility.Visible;
            txtRecoveryMessage.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF6, 0xA7, 0xAF));
            txtRecoveryMessage.Text = L("密码已重置，但新恢复码保存失败。请联系维护人员。", "The password was reset, but the new recovery code could not be saved. Contact support.");
        }
        finally
        {
            btnRecoverySubmit.IsEnabled = true;
            btnRecoveryCancel.IsEnabled = true;
            txtRecoveryNewPassword.Password = string.Empty;
            txtRecoveryConfirmPassword.Password = string.Empty;
        }
    }

    private async Task StoreAndShowRecoveryCodeAsync(string username, string recoveryCode, bool isReplacement)
    {
        _settings.RecoveryCodeUsername = username;
        _settings.RecoveryCode = recoveryCode;
        _settingsService.Save(_settings);

        try { Clipboard.SetText(recoveryCode); } catch { }

        string title = isReplacement
            ? L("保存新恢复码", "Save New Recovery Code")
            : L("保存恢复码", "Save Recovery Code");
        string message = L(
            $"新恢复码：\n{recoveryCode}\n\n已复制到剪贴板。请截图另存；旧恢复码已失效。",
            $"New recovery code:\n{recoveryCode}\n\nCopied to the clipboard. Save a screenshot; the old code is no longer valid.");
        if (!isReplacement)
        {
            message = L(
                $"恢复码：\n{recoveryCode}\n\n已复制到剪贴板。请截图另存，忘记密码时需要它。",
                $"Recovery code:\n{recoveryCode}\n\nCopied to the clipboard. Save a screenshot; you will need it if you forget your password.");
        }
        await ShowInfoDialogAsync(title, message, L("我已保存", "Saved"));
    }
}
