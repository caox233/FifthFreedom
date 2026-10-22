using SplinterCellCNLauncher.Services;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    internal const int MaximumDialogMessageCharacters = 12000;
    internal const int DefaultDialogTitleCharacters = 80;
    internal const int UpdateNoticeDialogTitleCharacters = 160;
    private const string FusionFixPreserveConflictMarker =
        "FusionFix was not installed because an existing MOD file must be preserved.";

    private async Task ShowNetworkFailureDialogAsync(NetworkReadyResult result)
    {
        FriendlyErrorKind kind = result.FailureStage switch
        {
            NetworkFailureStage.Network => FriendlyErrorKind.Tunnel,
            NetworkFailureStage.Tunnel => FriendlyErrorKind.Tunnel,
            NetworkFailureStage.Server => FriendlyErrorKind.Server,
            _ => FriendlyErrorKind.General
        };
        string stage = result.FailureStage switch
        {
            NetworkFailureStage.Network => L("网络创建阶段", "Network preparation"),
            NetworkFailureStage.Tunnel => L("隧道连接阶段", "Tunnel connection"),
            NetworkFailureStage.Server => L("服务器连接阶段", "Server connection"),
            _ => L("网络检测阶段", "Network check")
        };
        string configuredEndpoint = GetConfiguredPublicEndpoint();
        string advice = result.FailureStage switch
        {
            NetworkFailureStage.Network => L("1. 以管理员身份运行启动器；\n2. 检查杀毒软件是否拦截 EasyTier/SCBLEasyTier；\n3. 如反复失败，请在服务端脚本执行修复防火墙和转发规则。",
                "1. Run the launcher as administrator;\n2. Check whether antivirus blocks EasyTier/SCBLEasyTier;\n3. If it keeps failing, run server firewall/forwarding repair."),
            NetworkFailureStage.Tunnel => L($"1. 检查本机网络是否正常；\n2. 确认 {configuredEndpoint} 可以访问；\n3. 允许 easytier-core.exe 通过防火墙/杀毒软件。",
                $"1. Check your internet connection;\n2. Confirm {configuredEndpoint} is reachable;\n3. Allow easytier-core.exe through firewall/antivirus."),
            NetworkFailureStage.Server => L("1. 等待几秒后重新检测；\n2. 确认服务端 scbl-dedicated.service / scbl-update.service 正常；\n3. 在服务端脚本中执行检查服务状态和修复防火墙。",
                "1. Wait a few seconds and check again;\n2. Confirm scbl-dedicated.service / scbl-update.service are running;\n3. Use the server script to check status and repair firewall."),
            _ => L("请稍后重试；如果反复失败，请把日志发给维护人员。", "Try again later. If it keeps failing, send the logs to the maintainer.")
        };
        await ShowFriendlyErrorDialogAsync(kind, $"{L("失败过程", "Failed stage")}：{stage}\n\n{L("处理建议", "Suggestion")}：\n{advice}\n\n{result.Message}");
    }

    private FriendlyErrorKind ClassifyLaunchError(Exception ex)
    {
        string message = ex.ToString();
        if (message.Contains("公网隧道", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Public tunnel", StringComparison.OrdinalIgnoreCase)
            || message.Contains("assigned", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SCBLEasyTier", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Tunnel;
        if (message.Contains("50051", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gRPC", StringComparison.OrdinalIgnoreCase)
            || message.Contains("无法连接服务器", StringComparison.OrdinalIgnoreCase)
            || message.Contains("连接服务器超时", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Server;
        if (message.Contains("游戏目录", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Blacklist", StringComparison.OrdinalIgnoreCase) && message.Contains("not", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.GamePath;
        if (message.Contains("uplay_r1_loader", StringComparison.OrdinalIgnoreCase)
            || message.Contains("scbl.toml", StringComparison.OrdinalIgnoreCase)
            || message.Contains("写入", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.HookFiles;
        if (message.Contains("密码", StringComparison.OrdinalIgnoreCase)
            || message.Contains("账号", StringComparison.OrdinalIgnoreCase)
            || message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("account", StringComparison.OrdinalIgnoreCase))
            return FriendlyErrorKind.Account;
        return FriendlyErrorKind.General;
    }

    private Task ShowFriendlyErrorDialogAsync(FriendlyErrorKind kind, Exception ex, string? extraDetails = null)
        => ShowFriendlyErrorDialogAsync(
            kind,
            ex + (string.IsNullOrWhiteSpace(extraDetails) ? "" : "\n" + extraDetails),
            kind == FriendlyErrorKind.HookFiles
                && ex.Message.Contains(FusionFixPreserveConflictMarker, StringComparison.Ordinal));

    private async Task ShowFriendlyErrorDialogAsync(
        FriendlyErrorKind kind,
        string technicalDetails,
        bool isFusionFixPreserveConflict = false)
    {
        LogService.Error($"Friendly error [{kind}]: {technicalDetails}");
        (string title, string message) = BuildFriendlyErrorMessage(kind, isFusionFixPreserveConflict);
        await ShowInfoDialogAsync(title, message);
    }

    private (string Title, string Message) BuildFriendlyErrorMessage(
        FriendlyErrorKind kind,
        bool isFusionFixPreserveConflict = false)
    {
        if (kind == FriendlyErrorKind.HookFiles && isFusionFixPreserveConflict)
        {
            return (
                L("检测到已有 MOD 文件", "Existing MOD Files Detected"),
                L(
                    "检测到已有 MOD 文件，为避免覆盖未安装 FusionFix。请保留现有 MOD，或先自行备份并移除冲突文件后重试。",
                    "Existing MOD files were detected, so FusionFix was not installed to avoid overwriting them. Keep the existing MOD, or back up and remove the conflicting files yourself before trying again."));
        }

        return kind switch
        {
            FriendlyErrorKind.Tunnel => (
                L("联机网络连接失败", "Online Connection Failed"),
                L("请检查本机网络和防火墙，然后重新检测。\n\n仍然失败时，可在设置中导出诊断信息。",
                    "Check your connection and firewall, then run Check Network again.\n\nIf it still fails, export diagnostics from Settings.")),
            FriendlyErrorKind.Server => (
                L("服务器连接失败", "Server Connection Failed"),
                L("服务器暂时不可用，请稍后重新检测。",
                    "The server is temporarily unavailable. Check again shortly.")),
            FriendlyErrorKind.GamePath => (
                L("游戏目录不正确", "Invalid Game Folder"),
                L("未找到游戏文件。请选择：\nTom Clancy's Splinter Cell Blacklist\\src\\SYSTEM",
                    "Game files were not found. Select:\nTom Clancy's Splinter Cell Blacklist\\src\\SYSTEM")),
            FriendlyErrorKind.HookFiles => (
                L("无法更新游戏文件", "Unable to Update Game Files"),
                L("请关闭游戏，检查杀毒软件是否拦截 SCBL，然后重试。",
                    "Close the game, check whether antivirus is blocking SCBL, and try again.")),
            FriendlyErrorKind.Account => (
                L("登录失败", "Login Failed"),
                L("已有账号请检查密码；新账号会自动注册。",
                    "Check the password for an existing account. New accounts register automatically.")),
            _ => (
                L("操作失败", "Operation Failed"),
                L("请重试。仍然失败时，可在设置中导出诊断信息。",
                    "Try again. If it still fails, export diagnostics from Settings."))
        };
    }

    private Task<MessageBoxResult> ShowInfoDialogAsync(string title, string message)
        => ShowDialogAsync(title, message, L("确定", "OK"), null);

    private Task<MessageBoxResult> ShowInfoDialogAsync(string title, string message, string okText)
        => ShowDialogAsync(title, message, okText, null);

    private Task<MessageBoxResult> ShowUpdateNoticeDialogAsync(
        string title,
        string message,
        string okText)
        => ShowDialogAsync(
            title,
            message,
            okText,
            null,
            titleMaxChars: UpdateNoticeDialogTitleCharacters);

    private Task<MessageBoxResult> ShowConfirmDialogAsync(string title, string message, string yesText, string noText)
        => ShowDialogAsync(title, message, yesText, noText);

    internal static (string Text, bool IsEnabled) BuildTimedConfirmButtonState(string text, int remainingSeconds)
        => remainingSeconds <= 0
            ? (text, true)
            : ($"{text} ({remainingSeconds})", false);

    private async Task<MessageBoxResult> ShowTimedConfirmDialogAsync(string title, string message, string yesText, string noText, int seconds)
    {
        (string initialText, bool initialIsEnabled) = BuildTimedConfirmButtonState(yesText, seconds);
        Task<MessageBoxResult> task = ShowDialogAsync(title, message, initialText, noText, initialIsEnabled);
        for (int remainingSeconds = seconds - 1; remainingSeconds >= 0; remainingSeconds--)
        {
            await Task.Delay(1000);
            if (task.IsCompleted || dialogOverlay.Visibility != Visibility.Visible)
                break;

            (string text, bool isEnabled) = BuildTimedConfirmButtonState(yesText, remainingSeconds);
            btnDialogYes.Content = text;
            btnDialogYes.IsEnabled = isEnabled;
        }
        return await task;
    }

    private string CompactDialogText(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string normalized = value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        LogService.Warning(
            $"Dialog text was truncated for UI display: originalLength={normalized.Length}, maxChars={maxChars}.");
        return normalized[..Math.Max(0, maxChars)] + L("\n……", "\n...");
    }

    private Task<MessageBoxResult> ShowDialogAsync(
        string title,
        string message,
        string yesText,
        string? noText,
        bool isYesEnabled = true,
        int titleMaxChars = DefaultDialogTitleCharacters)
    {
        _dialogTcs?.TrySetResult(MessageBoxResult.None);
        _dialogTcs = new TaskCompletionSource<MessageBoxResult>();
        txtDialogTitle.Text = CompactDialogText(title, titleMaxChars);
        txtDialogMessage.Text = CompactDialogText(message, MaximumDialogMessageCharacters);
        btnDialogYes.Content = yesText;
        btnDialogNo.Content = noText ?? "";
        btnDialogNo.Visibility = string.IsNullOrWhiteSpace(noText) ? Visibility.Collapsed : Visibility.Visible;
        btnDialogYes.IsEnabled = isYesEnabled;
        dialogOverlay.Visibility = Visibility.Visible;
        return _dialogTcs.Task;
    }

    private void DialogYesButton_Click(object sender, RoutedEventArgs e)
    {
        dialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(MessageBoxResult.Yes);
        _dialogTcs = null;
    }

    private void DialogNoButton_Click(object sender, RoutedEventArgs e)
    {
        dialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(MessageBoxResult.No);
        _dialogTcs = null;
    }
}
