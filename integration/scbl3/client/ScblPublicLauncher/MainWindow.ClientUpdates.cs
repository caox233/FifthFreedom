using SplinterCellCNLauncher.ApplicationLayer;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _clientUpdateProgressAllowed;

    private async Task<bool> EnsureIndependentComponentUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        using IDisposable? updateLease = _clientUpdateCoordinator.TryBeginUpdateOperation();
        if (updateLease is null)
        {
            LogService.Info("Component update skipped because an update-channel transition owns the operation gate.");
            return false;
        }

        while (!_allowClose)
        {
            try
            {
                // Startup update reconciliation owns the launcher until it has
                // confirmed that all required components are current. Keep the
                // game action visibly disabled throughout checks and downloads,
                // matching the former mandatory-update flow.
                SetClientUpdateProgressAllowed(false);
                SetBusy(true, L("正在检查组件更新...", "Checking component updates..."));
                PreparedClientUpdatePrompt prepared = await _clientUpdateCoordinator
                    .PrepareAsync(cancellationToken);
                if (!prepared.HasPendingUpdates)
                {
                    SetClientUpdateProgressAllowed(false);
                    SetUpdatingState(false);
                    SetBusy(false);
                    return true;
                }

                UpdateAnnouncementSnapshot announcement = prepared.Announcement
                    ?? throw new UpdateAnnouncementUnavailableException(
                        "The prepared update did not include a server announcement snapshot.");
                SetClientUpdateProgressAllowed(false);
                SetBusy(false);
                await ShowUpdateNoticeDialogAsync(
                    IsEnglish ? announcement.TitleEn : announcement.TitleZh,
                    IsEnglish ? announcement.BodyEn : announcement.BodyZh,
                    L("立即更新", "Update Now"));
                cancellationToken.ThrowIfCancellationRequested();

                SetUpdatingState(true);
                SetClientUpdateProgressAllowed(true);
                SetBusy(true, L("正在下载并验证组件更新...", "Downloading and verifying component updates..."));
                ClientUpdateHostCommand command = await _clientUpdateCoordinator
                    .ApplyConfirmedAsync(prepared, cancellationToken);
                return ExecuteClientUpdateHostCommand(command);
            }
            catch (UpdateAnnouncementUnavailableException)
            {
                SetClientUpdateProgressAllowed(false);
                LogService.Warning("Server update announcement unavailable or invalid; component update remains blocked.");
                SetUpdatingState(false);
                SetBusy(false);
                MessageBoxResult retry = await ShowConfirmDialogAsync(
                    L("无法获取更新公告", "Unable to Load Update Notice"),
                    L("暂时无法获取本次更新公告。更新尚未开始，请稍后重试。",
                      "The notice for this update is currently unavailable. The update has not started; try again shortly."),
                    L("重新检查", "Try Again"),
                    L("退出", "Exit"));
                if (retry == MessageBoxResult.Yes)
                    continue;

                _allowClose = true;
                Application.Current.Shutdown();
                return false;
            }
            catch (CryptographicException ex)
            {
                SetClientUpdateProgressAllowed(false);
                LogService.Error("Component security verification failed: " + ex);
                SetUpdatingState(false);
                SetBusy(false);
                MessageBoxResult retry = await ShowConfirmDialogAsync(
                    L("组件安全验证失败", "Component Security Verification Failed"),
                    L("更新已安全停止，未安装任何未通过安全验证的组件。\n\n请先重新检查；如果问题持续，请在启动器设置中导出诊断信息并联系管理员。请勿关闭安全软件或绕过安全校验。",
                      "The update was stopped safely. No component that failed security verification was installed.\n\nTry the check again. If the problem continues, export diagnostics from Launcher Settings and contact an administrator. Do not disable security software or bypass verification."),
                    L("重新检查", "Try Again"),
                    L("退出", "Exit"));
                if (retry == MessageBoxResult.Yes)
                    continue;

                _allowClose = true;
                Application.Current.Shutdown();
                return false;
            }
            catch (ClientComponentManifestChangedException)
            {
                SetClientUpdateProgressAllowed(false);
                LogService.Info("Component manifest changed after update confirmation; preparing a fresh prompt.");
                SetUpdatingState(false);
                SetBusy(false);
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetClientUpdateProgressAllowed(false);
                SetUpdatingState(false);
                SetBusy(false);
                return false;
            }
            catch (Exception ex)
            {
                SetClientUpdateProgressAllowed(false);
                LogService.Error("Component update check failed: " + ex);
                SetUpdatingState(false);
                SetBusy(false);
                MessageBoxResult retry = await ShowConfirmDialogAsync(
                    L("无法检查组件更新", "Unable to Check Components"),
                    L("无法获取组件清单，请检查网络后重试。",
                      "The component manifest is unavailable. Check the network and try again."),
                    L("重新检查", "Try Again"),
                    L("退出", "Exit"));
                if (retry == MessageBoxResult.Yes)
                    continue;

                _allowClose = true;
                Application.Current.Shutdown();
                return false;
            }
        }

        SetClientUpdateProgressAllowed(false);
        return false;
    }

    private bool ExecuteClientUpdateHostCommand(ClientUpdateHostCommand command)
    {
        switch (command.Kind)
        {
            case ClientUpdateHostCommandKind.Continue:
                SetClientUpdateProgressAllowed(false);
                SetUpdatingState(false);
                SetBusy(false);
                return true;

            case ClientUpdateHostCommandKind.RestartLauncher:
                SetClientUpdateProgressAllowed(false);
                LogService.Info(
                    $"Component activation restart requested: components={string.Join(',', command.Components)}");
                SetUpdatingState(true);
                SetBusy(true, L("正在应用更新...", "Applying update..."));
                if (!_localUpdateService.ScheduleLauncherRestartAfterExit(Environment.ProcessId))
                {
                    throw new InvalidOperationException(
                        L("无法启动组件更新重启程序。", "Unable to start the component update restart helper."));
                }
                _allowClose = true;
                Application.Current.Shutdown();
                return false;

            case ClientUpdateHostCommandKind.ReplaceLauncher:
                SetClientUpdateProgressAllowed(false);
                LocalClientUpdateService.UpdatePackageInfo package = command.UpdatePackage
                    ?? throw new InvalidOperationException("Launcher replacement command did not include a verified package.");
                SetUpdatingState(true);
                SetBusy(true, L("正在安装启动器更新...", "Installing Launcher update..."));
                _localUpdateService.StartUpdater(package, Environment.ProcessId);
                _allowClose = true;
                Application.Current.Shutdown();
                return false;

            default:
                throw new InvalidOperationException($"Unknown client update host command: {command.Kind}");
        }
    }

    private void ClientUpdateCoordinator_ProgressChanged(
        object? sender,
        ClientUpdateProgress progress)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_clientUpdateProgressAllowed)
            {
                ResetClientUpdateProgressPresentation();
                return;
            }

            updateProgressPanel.Visibility = Visibility.Visible;
            updateProgressBar.IsIndeterminate = false;
            updateProgressBar.Value = progress.Percentage;
            txtUpdateProgress.Text = L("更新中", "Updating");
        });
    }

    private void SetClientUpdateProgressAllowed(bool allowed)
    {
        _clientUpdateProgressAllowed = allowed;
        if (!allowed && updateProgressPanel != null)
            ResetClientUpdateProgressPresentation();
    }

    private void ResetClientUpdateProgressPresentation()
    {
        updateProgressPanel.Visibility = Visibility.Hidden;
        updateProgressBar.IsIndeterminate = false;
        updateProgressBar.Value = 0d;
        txtUpdateProgress.Text = string.Empty;
    }

    private void StartComponentUpdateMonitor()
    {
        _componentUpdateMonitor.Start();
    }

    private async Task HandleBackgroundComponentUpdatesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureIndependentComponentUpdatesAsync(cancellationToken);
    }

    private async Task<bool> EnsureLatestComponentsBeforeLaunchAsync()
    {
        return await EnsureIndependentComponentUpdatesAsync();
    }

    private void NotifyGameExitedForComponentUpdates()
    {
        _componentUpdateMonitor.NotifyDeferredWorkCanResume();
    }

}
