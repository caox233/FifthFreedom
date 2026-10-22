using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private bool _remoteAnnouncementCheckedThisSession;
    private CancellationTokenSource? _announcementRefreshCts;
    private LauncherAnnouncement? _activeTickerAnnouncement;
    private AnimationClock? _announcementScrollClock;
    private bool _announcementRefreshLoopStarted;
    private string _renderedAnnouncementText = string.Empty;
    private string _renderedAnnouncementStyle = string.Empty;

    private async Task CheckRemoteAnnouncementsAfterNetworkAsync()
    {
        if (!_networkReady || _allowClose)
            return;

        await RefreshActiveTickerAnnouncementAsync();

        // Startup announcements remain explicit one-time dialogs. The normal active
        // announcement is a non-interactive ticker and is refreshed periodically.
        if (!_remoteAnnouncementCheckedThisSession)
        {
            _remoteAnnouncementCheckedThisSession = true;
            try
            {
                var startup = await _announcementService.GetStartupAnnouncementAsync();
                if (startup != null)
                    await ShowStartupAnnouncementAsync(startup);
            }
            catch (Exception ex)
            {
                LogService.Info("Startup announcement skipped: " + ex.Message);
            }
        }

        StartAnnouncementRefreshLoop();
    }

    private async Task RefreshActiveTickerAnnouncementAsync()
    {
        try
        {
            LauncherAnnouncement? active = await _announcementService.GetActiveAnnouncementAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _activeTickerAnnouncement = active;
                RefreshAnnouncementVisual();
            });
        }
        catch (Exception ex)
        {
            LogService.Info("Ticker announcement refresh skipped: " + ex.Message);
        }
    }

    private void StartAnnouncementRefreshLoop()
    {
        if (_announcementRefreshLoopStarted)
            return;

        _announcementRefreshLoopStarted = true;
        _announcementRefreshCts = new CancellationTokenSource();
        CancellationToken token = _announcementRefreshCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _allowClose)
                        break;
                    if (_networkReady)
                        await RefreshActiveTickerAnnouncementAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Info("Ticker announcement refresh loop skipped one cycle: " + ex.Message);
                }
            }
        }, token);
    }

    private void InitializeAnnouncementTicker()
    {
        // The ticker used to be advanced by a DispatcherTimer every 30 ms. Any
        // short dispatcher spike (for example while a manual network check
        // publishes several status snapshots) therefore looked like a pause.
        // A WPF animation clock is rendered by the composition pipeline and
        // stays smooth while the UI dispatcher is briefly busy.
    }

    private void RefreshAnnouncementVisual()
    {
        if (txtAppSubtitle == null)
            return;

        if (_activeTickerAnnouncement == null)
        {
            txtAppSubtitle.Text = L("OK兄弟们，干起来♂", "OK agents, let's move ♂");
            txtAppSubtitle.Foreground = (Brush)FindResource("TextSubBrush");
        }
        else
        {
            string title = IsEnglish && !string.IsNullOrWhiteSpace(_activeTickerAnnouncement.TitleEn)
                ? _activeTickerAnnouncement.TitleEn
                : _activeTickerAnnouncement.Title;
            string body = IsEnglish && !string.IsNullOrWhiteSpace(_activeTickerAnnouncement.BodyEn)
                ? _activeTickerAnnouncement.BodyEn
                : _activeTickerAnnouncement.Body;
            string combined = $"{title}：{body}";
            txtAppSubtitle.Text = Regex.Replace(combined, @"\s+", " ").Trim();
            txtAppSubtitle.Foreground = _activeTickerAnnouncement.Level.ToLowerInvariant() switch
            {
                "error" => Brushes.IndianRed,
                "warning" => Brushes.Goldenrod,
                "success" => Brushes.LimeGreen,
                _ => (Brush)FindResource("TextSubBrush")
            };
        }

        string styleKey = _activeTickerAnnouncement?.Level?.ToLowerInvariant() ?? "default";
        if (string.Equals(_renderedAnnouncementText, txtAppSubtitle.Text, StringComparison.Ordinal)
            && string.Equals(_renderedAnnouncementStyle, styleKey, StringComparison.Ordinal))
        {
            // Network checks refresh the same remote announcement. Do not jump
            // it back to the beginning unless its visible content really changed.
            return;
        }

        _renderedAnnouncementText = txtAppSubtitle.Text;
        _renderedAnnouncementStyle = styleKey;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestartAnnouncementScroll));
    }

    private void RestartAnnouncementScroll()
    {
        if (txtAppSubtitle == null || bdAnnouncementClip == null)
            return;

        if (string.IsNullOrWhiteSpace(txtAppSubtitle.Text))
        {
            _announcementScrollClock?.Controller?.Stop();
            _announcementScrollClock = null;
            announcementTransform.BeginAnimation(TranslateTransform.XProperty, null);
            announcementTransform.X = 0;
            return;
        }

        _announcementScrollClock?.Controller?.Stop();
        _announcementScrollClock = null;
        announcementTransform.BeginAnimation(TranslateTransform.XProperty, null);
        announcementTransform.X = 0;

        txtAppSubtitle.Measure(new Size(double.PositiveInfinity, Math.Max(1, bdAnnouncementClip.ActualHeight)));
        double viewportWidth = bdAnnouncementClip.ActualWidth;
        double textWidth = txtAppSubtitle.DesiredSize.Width;
        if (_activeTickerAnnouncement == null || viewportWidth <= 1 || textWidth <= 1)
            return;

        // Every active announcement is a ticker, including short messages that
        // fit inside the viewport. Start at the normal left edge, move fully out,
        // then re-enter from the right without a long empty hold.
        const double pixelsPerSecond = 32;
        double cycleSeconds = (viewportWidth + textWidth) / pixelsPerSecond;
        var animation = new DoubleAnimation
        {
            From = viewportWidth,
            To = -textWidth,
            Duration = TimeSpan.FromSeconds(cycleSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };

        _announcementScrollClock = (AnimationClock)animation.CreateClock(true);
        announcementTransform.ApplyAnimationClock(
            TranslateTransform.XProperty,
            _announcementScrollClock,
            HandoffBehavior.SnapshotAndReplace);
        _announcementScrollClock.Controller?.Seek(
            TimeSpan.FromSeconds(viewportWidth / pixelsPerSecond),
            TimeSeekOrigin.BeginTime);

        if (bdAnnouncementClip.IsMouseOver)
            _announcementScrollClock.Controller?.Pause();
    }

    private void AnnouncementClip_MouseEnter(object sender, MouseEventArgs e)
        => _announcementScrollClock?.Controller?.Pause();

    private void AnnouncementClip_MouseLeave(object sender, MouseEventArgs e)
        => _announcementScrollClock?.Controller?.Resume();

    private void AnnouncementClip_SizeChanged(object sender, SizeChangedEventArgs e)
        => RestartAnnouncementScroll();

    private async Task ShowStartupAnnouncementAsync(LauncherAnnouncement announcement)
    {
        if (announcement.ShowOnce)
        {
            if (string.Equals(_settings.DismissedStartupAnnouncementId, announcement.Id, StringComparison.OrdinalIgnoreCase))
                return;
        }

        string title = IsEnglish && !string.IsNullOrWhiteSpace(announcement.TitleEn) ? announcement.TitleEn : announcement.Title;
        string body = IsEnglish && !string.IsNullOrWhiteSpace(announcement.BodyEn) ? announcement.BodyEn : announcement.Body;
        var result = await ShowConfirmDialogAsync(
            title,
            body,
            L("不再提示", "Don't show again"),
            L("关闭", "Close"));

        if (result == MessageBoxResult.Yes)
        {
            _settings.DismissedStartupAnnouncementId = announcement.Id;
            _settingsService.Save(_settings);
        }
    }
}
