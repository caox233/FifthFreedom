using SplinterCellCNLauncher.Services;
using System.Windows;
using System.Windows.Controls;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private void GuideButton_Click(object sender, RoutedEventArgs e) => ShowGuide(markCompletedOnClose: false);

    private void BuildGuideSteps()
    {
        _guideSteps = new List<GuideStep>
        {
            new() { Target = txtUsername, TitleZh = "联机账号", TitleEn = "Online Account", MessageZh = "输入账号和密码。\n新账号会自动注册；已有账号请使用原密码。", MessageEn = "Enter your account and password.\nNew accounts register automatically; existing accounts use their current password." },
            new() { Target = bdStatusPanel, TitleZh = "连接状态", TitleEn = "Connection", MessageZh = "绿灯可启动游戏；黄灯正在连接；红灯表示连接失败。\n大厅显示服务器延迟，组队或进入房间后显示房主延迟。", MessageEn = "Green means ready, yellow means connecting, and red means the connection failed.\nLatency is measured to the server in the lobby and to the host after joining a party or room." },
            new() { Target = btnCheckNetwork, TitleZh = "检测网络", TitleEn = "Check Network", MessageZh = "重新检查联机状态。", MessageEn = "Check the online connection again." },
            new() { Target = btnPlayers, TitleZh = "在线玩家", TitleEn = "Online Players", MessageZh = "显示已发现的玩家。点击可查看玩家名称、在线状态和延迟。", MessageEn = "Shows discovered players. Click to view player names, online status, and latency." },
            new() { Target = launchOptionsPanel, TitleZh = "启动选项", TitleEn = "Launch Options", MessageZh = "选择 DX9/DX11 和官方/模组模式。官方与模组使用隔离存档，模组会加载玩法 DLL；游戏启动后无法切换。", MessageEn = "Choose DX9/DX11 and Official/MOD mode. Official and MOD use isolated saves, and MOD loads the gameplay DLL; options are locked after launch." },
            new() { Target = btnLaunch, TitleZh = "启动游戏", TitleEn = "Launch Game", MessageZh = "绿灯后点击启动。\n游戏运行后可在这里结束游戏。", MessageEn = "Launch when the status is green.\nUse the same button to end the game." },
            new() { Target = btnSettings, TitleZh = "设置", TitleEn = "Settings", MessageZh = "切换语言和声音、修改服务器、管理存档、修复网络或导出诊断包。", MessageEn = "Change language, sound, server, saves, network repair, or export a diagnostic package." }
        };
    }

    private void ShowGuide(bool markCompletedOnClose)
    {
        BuildGuideSteps();
        if (_guideSteps.Count == 0)
            return;
        guideOverlay.Tag = markCompletedOnClose;
        _guideIndex = 0;
        guideOverlay.Visibility = Visibility.Visible;
        RefreshGuideStep();
    }

    private void RefreshGuideStep()
    {
        if (_guideSteps.Count == 0 || guideOverlay.Visibility != Visibility.Visible)
            return;
        _guideIndex = Math.Max(0, Math.Min(_guideIndex, _guideSteps.Count - 1));
        GuideStep step = _guideSteps[_guideIndex];
        txtGuideStep.Text = $"{_guideIndex + 1} / {_guideSteps.Count}";
        txtGuideTitle.Text = IsEnglish ? step.TitleEn : step.TitleZh;
        txtGuideMessage.Text = IsEnglish ? step.MessageEn : step.MessageZh;
        btnGuideSkip.Content = L("跳过", "Skip");
        btnGuidePrev.Content = L("上一步", "Back");
        btnGuideNext.Content = _guideIndex >= _guideSteps.Count - 1 ? L("完成", "Done") : L("下一步", "Next");
        btnGuidePrev.IsEnabled = _guideIndex > 0;
        PositionGuideVisuals(step.Target);
    }

    private void PositionGuideVisuals(FrameworkElement target)
    {
        try
        {
            target.UpdateLayout();
            rootGrid.UpdateLayout();
            double windowWidth = rootGrid.ActualWidth > 0 ? rootGrid.ActualWidth : ActualWidth;
            double windowHeight = rootGrid.ActualHeight > 0 ? rootGrid.ActualHeight : ActualHeight;
            Point topLeft = target.TranslatePoint(new Point(0, 0), rootGrid);
            const double pad = 8;
            double highlightLeft = Clamp(topLeft.X - pad, 10, Math.Max(10, windowWidth - 20));
            double highlightTop = Clamp(topLeft.Y - pad, 10, Math.Max(10, windowHeight - 20));
            double highlightWidth = Math.Min(Math.Max(40, target.ActualWidth + pad * 2), Math.Max(40, windowWidth - highlightLeft - 10));
            double highlightHeight = Math.Min(Math.Max(28, target.ActualHeight + pad * 2), Math.Max(28, windowHeight - highlightTop - 10));
            Canvas.SetLeft(guideHighlight, highlightLeft);
            Canvas.SetTop(guideHighlight, highlightTop);
            guideHighlight.Width = highlightWidth;
            guideHighlight.Height = highlightHeight;

            double cardWidth = Math.Min(360, Math.Max(280, windowWidth - 28));
            guideCard.Width = cardWidth;
            guideCard.Measure(new Size(cardWidth, double.PositiveInfinity));
            double cardHeight = Math.Min(guideCard.DesiredSize.Height > 0 ? guideCard.DesiredSize.Height : 220, Math.Max(190, windowHeight - 32));
            double left = Clamp(highlightLeft + highlightWidth / 2 - cardWidth / 2, 14, Math.Max(14, windowWidth - cardWidth - 14));
            double belowTop = highlightTop + highlightHeight + 10;
            double aboveTop = highlightTop - cardHeight - 10;
            bool below = belowTop + cardHeight <= windowHeight - 14;
            double top = below ? belowTop : aboveTop >= 14 ? aboveTop : Clamp(highlightTop + highlightHeight / 2 - cardHeight / 2, 14, Math.Max(14, windowHeight - cardHeight - 14));
            Canvas.SetLeft(guideCard, left);
            Canvas.SetTop(guideCard, top);
            guideArrow.Text = below ? "▲" : "▼";
            Canvas.SetLeft(guideArrow, Clamp(highlightLeft + highlightWidth / 2 - 8, left + 10, Math.Max(left + 10, left + cardWidth - 26)));
            Canvas.SetTop(guideArrow, below ? top - 19 : top + cardHeight - 2);
        }
        catch (Exception ex)
        {
            LogService.Error($"PositionGuideVisuals failed: {ex.Message}");
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min) return min;
        if (value < min) return min;
        return value > max ? max : value;
    }

    private void GuideNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guideIndex >= _guideSteps.Count - 1) { CloseGuide(); return; }
        _guideIndex++;
        RefreshGuideStep();
    }

    private void GuidePrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guideIndex <= 0) return;
        _guideIndex--;
        RefreshGuideStep();
    }

    private void GuideSkipButton_Click(object sender, RoutedEventArgs e) => CloseGuide();

    private void CloseGuide()
    {
        bool markCompleted = guideOverlay.Tag is bool b && b;
        guideOverlay.Visibility = Visibility.Collapsed;
        if (markCompleted && !_settings.GuideCompleted)
        {
            _settings.GuideCompleted = true;
            _settingsService.Save(_settings);
        }
    }
}
