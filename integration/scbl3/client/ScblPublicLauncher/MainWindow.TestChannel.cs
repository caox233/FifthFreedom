using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    // Presentation-facing result only. The ApplicationLayer owns persistence,
    // restart arguments and the actual channel transition.
    private enum TestChannelUiCommandResult
    {
        ContinueStartup,
        StopStartup,
        Failed
    }

    // Implemented by the backend bridge after the ApplicationLayer has resolved
    // persisted intent and command outcomes. These declarations deliberately have
    // no presentation-side fallback: silently inventing channel state is unsafe.
    private partial bool IsTestChannelContinuationConfirmationRequired();
    private partial Task<TestChannelUiCommandResult> ApplyTestChannelStartupChoiceAsync(bool continueTestChannel);
    private partial Task<TestChannelUiCommandResult> RequestEnterTestChannelAsync();

    private async void TestChannelAwareWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (IsTestChannelContinuationConfirmationRequired())
            {
                MessageBoxResult choice = await ShowTestChannelConfirmDialogAsync(
                    L("继续使用测试通道？", "Continue Using the Test Channel?"),
                    L(
                        "继续测试，或返回正式通道并重启。",
                        "Continue testing, or return to stable and restart."),
                    L("继续测试", "Continue Testing"),
                    L("返回正式通道", "Return to Stable"));

                TestChannelUiCommandResult result = await ApplyTestChannelStartupChoiceAsync(
                    choice == MessageBoxResult.Yes);
                if (result == TestChannelUiCommandResult.Failed)
                {
                    await ShowTestChannelCommandFailureAsync(result, startupStopped: true);
                    return;
                }

                if (result != TestChannelUiCommandResult.ContinueStartup)
                    return;
            }

            Window_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            LogService.Error("Test channel startup adapter failed: " + ex);
            await ShowTestChannelCommandFailureAsync(
                TestChannelUiCommandResult.Failed,
                startupStopped: true);
        }
    }

    private async void TestChannelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        settingsMenu.IsOpen = false;

        if (App.ComponentUpdateChannel == ClientUpdateChannel.Test)
        {
            MessageBoxResult leaveChoice = await ShowTestChannelConfirmDialogAsync(
                L("返回正式通道？", "Return to the Stable Channel?"),
                L(
                    "启动器将切换回正式通道并重新启动。之后将保持正式通道，不再显示测试通道启动询问。",
                    "The launcher will switch back to the stable channel and restart. Future launches will remain on stable without showing the test-channel prompt."),
                L("返回并重启", "Return & Restart"),
                L("取消", "Cancel"));
            if (leaveChoice != MessageBoxResult.Yes)
                return;

            TestChannelUiCommandResult leaveResult = await ApplyTestChannelStartupChoiceAsync(continueTestChannel: false);
            if (leaveResult == TestChannelUiCommandResult.Failed)
                await ShowTestChannelCommandFailureAsync(leaveResult, startupStopped: false);
            return;
        }

        MessageBoxResult enterChoice = await ShowTestChannelConfirmDialogAsync(
            L("进入测试通道", "Enter the Test Channel"),
            L(
                "测试通道可提前体验组件更新，稳定性可能较低；安全校验仍会保持开启。\n\n确认后启动器将重启，以后每次启动都会询问是否继续。",
                "The test channel offers early component updates and may be less stable; security checks remain enabled.\n\nThe launcher will restart after confirmation and ask whether to continue on future launches."),
            L("进入并重启", "Enter & Restart"),
            L("取消", "Cancel"));
        if (enterChoice != MessageBoxResult.Yes)
            return;

        TestChannelUiCommandResult enterResult = await RequestEnterTestChannelAsync();
        if (enterResult == TestChannelUiCommandResult.Failed)
            await ShowTestChannelCommandFailureAsync(enterResult, startupStopped: false);
    }

    private Task<MessageBoxResult> ShowTestChannelCommandFailureAsync(
        TestChannelUiCommandResult result,
        bool startupStopped)
    {
        string message = startupStopped
                ? L(
                    "未能安全确认更新通道，本次启动已停止。请重新打开启动器后再试；持续失败时可在设置中导出诊断包。",
                    "The update channel could not be confirmed safely, so this launch has stopped. Reopen the launcher and try again. If it keeps failing, export diagnostics from Settings.")
                : L(
                    "未能安全切换更新通道，当前通道保持不变。请稍后重试；持续失败时可在设置中导出诊断包。",
                    "The update channel could not be switched safely and remains unchanged. Try again later. If it keeps failing, export diagnostics from Settings.");
        Task<MessageBoxResult> task = ShowInfoDialogAsync(
            L("通道切换未完成", "Channel Switch Was Not Completed"),
            message);
        FocusTestChannelDialogPrimaryAction();
        return task;
    }

    private Task<MessageBoxResult> ShowTestChannelConfirmDialogAsync(
        string title,
        string message,
        string yesText,
        string noText)
    {
        Task<MessageBoxResult> task = ShowConfirmDialogAsync(title, message, yesText, noText);
        FocusTestChannelDialogPrimaryAction();
        return task;
    }

    private void FocusTestChannelDialogPrimaryAction()
    {
        btnDialogYes.Focus();
        Keyboard.Focus(btnDialogYes);
    }
}
