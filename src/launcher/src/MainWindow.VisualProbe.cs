using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SplinterCellCNLauncher;

public partial class MainWindow
{
    private const string VisualFixtureId = "fifth-freedom-visual-fixture-v1";
    private static readonly string[] VisualStates = ["main", "settings", "settings-public", "settings-gameplay", "discovery", "join", "create", "cancel"];
    private sealed record VisualScenario(string State, string Language, int Dpi, int Width, int Height);
    private sealed record VisualConfiguration(string FixtureId, string Phase, string SourceCommit, string OutputDirectory, VisualScenario[] Scenarios);
    private sealed record LayoutCheck(string Category, string Target, bool Passed, string Detail);
    private sealed record ElementBounds(string Name, double X, double Y, double Width, double Height);
    private sealed record VisualFrame(string Id, string State, string Language, int Dpi, int Width, int Height,
        string Surface, bool ProductionStateHook, string Screenshot, string Sha256, double ObservedDpi,
        ElementBounds[] Bounds, LayoutCheck[] Checks, string? ErrorType, Dictionary<string, string>? AdditionalScreenshots = null);

    internal static int RunVisualProbe(string configurationPath)
    {
        if (!App.PreviewMode) throw new InvalidOperationException("Visual probe requires isolated preview mode");
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        var config = JsonSerializer.Deserialize<VisualConfiguration>(File.ReadAllText(configurationPath), options)
                     ?? throw new InvalidOperationException("Missing visual configuration");
        if (config.FixtureId != VisualFixtureId || config.Scenarios.Length is < 1 or > 150 ||
            config.Phase is not ("baseline" or "modified" or "rollback" or "reapplied"))
            throw new InvalidOperationException("Unrecognized synthetic fixture");
        foreach (var scenario in config.Scenarios)
            if (!VisualStates.Contains(scenario.State) || scenario.Language is not ("zh-CN" or "en-US") ||
                scenario.Dpi is not (96 or 120 or 144 or 192) ||
                (scenario.Width, scenario.Height) is not ((1120, 680) or (900, 580)))
                throw new InvalidOperationException("Unsupported visual scenario");
        string output = Path.GetFullPath(config.OutputDirectory);
        Directory.CreateDirectory(output);
        var frames = new List<VisualFrame>();
        foreach (var scenario in config.Scenarios)
        {
            string id = $"{scenario.State}-{scenario.Language}-{scenario.Dpi}-{scenario.Width}x{scenario.Height}";
            var window = new MainWindow();
            try { frames.Add(window.CaptureVisualScenario(scenario, id, output)); }
            catch (Exception error)
            {
                frames.Add(new(id, scenario.State, scenario.Language, scenario.Dpi, scenario.Width, scenario.Height,
                    "unavailable", false, "", "", 0, [], [], error.GetType().Name));
                File.WriteAllText(Path.Combine(output, id + ".error.txt"), error.ToString());
            }
            finally { window.Close(); }
            if (frames.Count % 16 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        var report = new { Schema = 1, config.FixtureId, config.Phase, config.SourceCommit,
            Rendering = "actual WPF visual tree; SetRootDpi and RenderTargetBitmap; not OS/display DPI or game/GPU acceptance",
            NetworkStarted = false, GameStarted = false, PersistedSettingsRead = false,
            Frames = frames, Passed = frames.All(frame => frame.ErrorType == null && frame.Checks.All(check => check.Passed)) };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, options));
        return report.Passed ? 0 : 1;
    }

    private VisualFrame CaptureVisualScenario(VisualScenario scenario, string id, string output)
    {
        _settings.Language = scenario.Language;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(scenario.Language);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        ApplyLocalization();
        txtUsername.Text = "VisualFixture";
        txtServer.Text = "192.0.2.10:50051";
        txtPassword.Clear();
        var hook = GetType().GetMethod("ApplyVisualProbeState", BindingFlags.Instance | BindingFlags.NonPublic);
        if (hook != null) hook.Invoke(this, [scenario.State]);
        else
        {
            if (scenario.State.StartsWith("settings", StringComparison.Ordinal)) ShowToolsPreview("home");
            if (scenario.State == "create") { cmbConnectionMode.SelectedIndex = 1; _isHostModeCached = true; UpdateConnectionModeUi(); }
            if (scenario.State == "join") { cmbConnectionMode.SelectedIndex = 0; _isHostModeCached = false; UpdateConnectionModeUi(); }
            // The old product has an actual modal picker, captured below. No
            // discovery sockets, account database or LAN runtime is started.
            if (scenario.State == "discovery") return CaptureLegacyHostPicker(scenario, id, output);
        }
        txtPassword.Clear();
        txtSavePaths.Text = "Official: C:\\FifthFreedom-VisualFixture\\Official\nMOD: C:\\FifthFreedom-VisualFixture\\MOD";
        if (scenario.State == "settings-public")
            foreach (string name in new[] { "txtServerEndpoint", "txtTestServerEndpoint" })
                if (FindName(name) is TextBox field)
                    field.Text = "visual-fixture-" + new string('a', 48) + ".example.invalid:11010";
        if (scenario.State == "settings-gameplay" && hook != null)
        {
            fovSlider.Value = _settings.FovScale < 1.25 ? _settings.FovScale + .05 : _settings.FovScale - .05;
            cmbGameplayMode.SelectedValue = _settings.GameplayMode == "MOD" ? "Official" : "MOD";
        }
        var surface = (FrameworkElement)Content;
        // Keep the logical Window owner: its implicit styles and resources are
        // part of the production UI. Before Show/EnsureHandle the content has no
        // visual parent, so SetRootDpi remains valid without detaching Content.
        var frame = CaptureVisualSurface(surface, scenario, id, output, "launcher-content", hook != null);
        if (hook != null)
            frame = frame with { Checks = frame.Checks.Concat(CheckVisualKeyboardBoundary(surface, scenario, hook)).ToArray() };
        return frame;
    }

    private VisualFrame CaptureLegacyHostPicker(VisualScenario scenario, string id, string output)
    {
        _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        VisualFrame? result = null;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.Owner == this);
            if (dialog == null) return;
            timer.Stop();
            try
            {
                var content = (FrameworkElement)dialog.Content;
                dialog.Content = null;
                var surface = new Grid { Background = (Brush)FindResource("WindowBrush") };
                content.HorizontalAlignment = HorizontalAlignment.Center;
                content.VerticalAlignment = VerticalAlignment.Center;
                surface.Children.Add(content);
                result = CaptureVisualSurface(surface, scenario, id, output, "legacy-production-host-picker", false);
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        };
        timer.Start();
        try
        {
            ShowHostSelectionDialog([
                new DiscoveredHost { HostName = "Visual fixture host A", HostIp = "192.0.2.10" },
                new DiscoveredHost { HostName = "合成房主 B", HostIp = "192.0.2.11" }
            ], "当前发现的房主", "");
        }
        finally { timer.Stop(); }
        if (failure != null) throw failure;
        return result ?? throw new InvalidOperationException("Legacy host picker did not render");
    }

    private static IEnumerable<FrameworkElement> VisualElements(DependencyObject parent)
    {
        if (parent is FrameworkElement element) yield return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in VisualElements(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static bool Presented(FrameworkElement element, FrameworkElement root)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity <= 0)) return false;
            if (ReferenceEquals(current, root)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool OwnsHit(DependencyObject? hit, FrameworkElement target)
    {
        while (hit != null)
        {
            if (ReferenceEquals(hit, target)) return true;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return false;
    }

    private static DependencyObject? VisualHit(FrameworkElement surface, Point point)
    {
        DependencyObject? found = null;
        VisualTreeHelper.HitTest(surface,
            target => target is UIElement element && (element.Visibility != Visibility.Visible || !element.IsHitTestVisible || element.Opacity <= 0)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            hit => { found = hit.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        return found;
    }

    private bool IsDirectGameplayScenario(string state) => state == "settings-gameplay" && FindName("gameplayDraftActions") != null;

    private static bool PositiveAreaOverlap(Rect first, Rect second)
    {
        // WPF IntersectsWith also returns true for a shared row edge. Reject
        // positive area, allowing only floating-point noise from Viewbox scaling.
        Rect overlap = Rect.Intersect(first, second);
        return !overlap.IsEmpty && overlap.Width > .000001 && overlap.Height > .000001;
    }

    private string[] VisualActionNames(string state) => state is "main" or "cancel" || IsDirectGameplayScenario(state)
        ? ["btnLaunch", "btnSettings", "btnLocalConnection", "btnPublicConfiguration", "itemGameplayMod", "itemGameplayOfficial", "fovSlider", "btnFovReset", "btnGameplaySave", "btnGameplayCancel"]
        : state.StartsWith("settings", StringComparison.Ordinal) ? ["btnToolsClose"]
        : ["btnLocalConfirm", "btnLocalRescan", "btnLocalManual", "btnLocalCreate", "btnLocalCancel"];

    private LayoutCheck[] CheckVisualKeyboardBoundary(FrameworkElement surface, VisualScenario scenario, MethodInfo hook)
    {
        // This is a real shown WPF window with an HWND and real focus routing.
        // Its display DPI is the runner's system DPI, separate from the root-DPI
        // bitmap matrix. Synthetic state hooks suppress all real connections.
        var checks = new List<LayoutCheck>();
        void Check(string target, bool passed, string detail = "") => checks.Add(new("keyboard", target, passed, detail));
        Content = surface;
        Width = scenario.Width; Height = scenario.Height;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0;
        Show(); Activate();
        hook.Invoke(this, [scenario.State]);
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void CheckActualInputHit(string name)
        {
            if (FindName(name) is not FrameworkElement element || !element.IsVisible || !element.IsEnabled || !element.IsHitTestVisible) return;
            var rect = element.TransformToAncestor(surface).TransformBounds(new Rect(new Point(), element.RenderSize));
            checks.Add(new("input-hit-test", name,
                OwnsHit(surface.InputHitTest(new Point(rect.X+rect.Width/2, rect.Y+rect.Height/2)) as DependencyObject, element),
                $"actual shown HWND; content DPI={VisualTreeHelper.GetDpi(surface).PixelsPerInchX}; HWND device DPI={(PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 0)*96}"));
        }
        if (IsDirectGameplayScenario(scenario.State))
        {
            double savedFov = _settings.FovScale;
            string savedMode = _settings.GameplayMode;
            double nextFov = savedFov < 1.25 ? savedFov + .05 : savedFov - .05;
            string nextMode = savedMode == "MOD" ? "Official" : "MOD";
            fovSlider.Value = nextFov; cmbGameplayMode.SelectedValue = nextMode;
            surface.UpdateLayout();
            foreach (string name in VisualActionNames(scenario.State)) CheckActualInputHit(name);
            Check("direct-edit-keeps-overlays-closed", toolsOverlay.Visibility != Visibility.Visible);
            Check("gameplay-edits-remain-draft", _settings.FovScale == savedFov && _settings.GameplayMode == savedMode &&
                GetSelectedFovScale() == savedFov && GetSelectedGameplayMode() == savedMode);
            var save = FindName("btnGameplaySave") as Button;
            var cancel = FindName("btnGameplayCancel") as Button;
            Check("dirty-draft-blocks-launch", !btnLaunch.IsEnabled);
            Check("gameplay-save-enabled-for-dirty-draft", save?.IsEnabled == true);
            var directSource = PresentationSource.FromVisual(this);
            if (directSource == null) { Check("escape-hwnd-source", false); return checks.ToArray(); }
            cmbGameplayMode.Focus();
            cmbGameplayMode.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, directSource, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check("escape-cancels-direct-gameplay-draft", _settings.FovScale == savedFov && _settings.GameplayMode == savedMode &&
                Math.Abs(fovSlider.Value-savedFov) < .00001 && Equals(cmbGameplayMode.SelectedValue, savedMode));
            Check("escape-keeps-main-focus", OwnsHit(Keyboard.FocusedElement as DependencyObject, cmbGameplayMode));
            fovSlider.Value = nextFov; cmbGameplayMode.SelectedValue = nextMode;
            save?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("gameplay-preview-save-commits-memory", Math.Abs(_settings.FovScale-nextFov) < .00001 && _settings.GameplayMode == nextMode,
                "production main-screen Save uses CommitPreview; real disk persistence is covered separately");
            fovSlider.Value = savedFov; cmbGameplayMode.SelectedValue = savedMode;
            cancel?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("direct-cancel-restores-last-preview-save", Math.Abs(fovSlider.Value-nextFov) < .00001 &&
                Equals(cmbGameplayMode.SelectedValue, nextMode) && Math.Abs(_settings.FovScale-nextFov) < .00001 && _settings.GameplayMode == nextMode);
            Check("cancel-keeps-main-screen", toolsOverlay.Visibility != Visibility.Visible &&
                FindName("gameplayDraftActions") is UIElement actions && actions.Visibility == Visibility.Hidden);
            var targets = new[] { cmbGameplayMode as Control, fovSlider, btnFovReset, save, cancel };
            fovSlider.Value = savedFov;
            surface.UpdateLayout();
            bool allReachable = true;
            foreach (var target in targets)
            {
                if (target == null) { allReachable = false; continue; }
                target.Focus();
                bool focused = OwnsHit(Keyboard.FocusedElement as DependencyObject, target);
                Check("direct-focus-" + target.Name, focused);
                allReachable &= focused;
            }
            // Keyboard navigation through a ListBox is not an inverse function:
            // selection determines its entry item. Follow real focus transitions
            // from each end of the main screen and require every edit/action in
            // both directions instead of assuming Next then Previous is identity.
            foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
            {
                var entry = direction == FocusNavigationDirection.Next ? btnSettings : btnCheckNetwork;
                entry.Focus();
                bool entryFocused = OwnsHit(Keyboard.FocusedElement as DependencyObject, entry);
                Check("direct-" + direction + "-entry-focus", entryFocused, entry.Name);
                allReachable &= entryFocused;
                var visited = new HashSet<string>();
                var trace = new List<string>();
                for (int step = 0; step < 100; step++)
                {
                    var focus = Keyboard.FocusedElement as DependencyObject;
                    trace.Add(focus is FrameworkElement element && !string.IsNullOrEmpty(element.Name)
                        ? element.Name : focus?.GetType().Name ?? "none");
                    foreach (var target in targets)
                        if (target != null && OwnsHit(focus, target)) visited.Add(target.Name);
                    if (visited.Count == targets.Length) break;
                    if (focus is not UIElement current || !current.MoveFocus(new TraversalRequest(direction))) break;
                    Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
                string route = string.Join(" -> ", trace);
                foreach (var target in targets)
                {
                    bool reached = target != null && visited.Contains(target.Name);
                    Check("direct-" + direction + "-reaches-" + target?.Name, reached, route);
                    allReachable &= reached;
                }
            }
            Check("direct-controls-keyboard-reachable", allReachable, "actual shown HWND; every direct control reached by Tab and Shift+Tab");
            cancel?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return checks.ToArray();
        }
        foreach (string name in VisualActionNames(scenario.State)) CheckActualInputHit(name);
        if (scenario.State is "main" or "cancel") return checks.ToArray();
        bool settings = scenario.State.StartsWith("settings", StringComparison.Ordinal);
        var overlay = FindName(settings ? "toolsOverlay" : "localDiscoveryOverlay") as FrameworkElement;
        var initial = FindName(settings ? "btnToolsClose" : "btnLocalCancel") as Control;
        if (overlay == null || initial == null)
        {
            Check("active-overlay", false);
            return checks.ToArray();
        }
        initial.Focus();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        bool Contained() => OwnsHit(Keyboard.FocusedElement as DependencyObject, overlay);
        Check("initial-focus-contained", Contained(), "actual HWND focus at runner display DPI");
        int focusable = VisualElements(overlay).Count(element => element.Focusable && element.IsEnabled && element.IsVisible);
        foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
        {
            bool contained = true;
            for (int index = 0; index < focusable + 2; index++)
            {
                (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(direction));
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                contained &= Contained();
            }
            Check(direction == FocusNavigationDirection.Next ? "tab-cycle" : "shift-tab-cycle", contained);
        }
        btnLaunch.Focus();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("background-programmatic-focus-blocked", Contained());
        btnSettings.Focus();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("enabled-background-focus-blocked", btnSettings.IsEnabled && Contained());
        if (settings && FindName("toolsCard") is FrameworkElement card)
        {
            var scroll = VisualElements(card).OfType<ScrollViewer>().FirstOrDefault();
            scroll?.ScrollToEnd(); surface.UpdateLayout();
            foreach (string name in new[] { "btnServerSettingsSave", "btnServerSettingsReset", "btnGameplaySave", "btnGameplayCancel" })
                CheckActualInputHit(name);
        }
        if (scenario.State == "settings-public")
        {
            foreach (string name in new[] { "txtServerEndpoint", "txtTestServerEndpoint" })
            {
                var field = FindName(name) as TextBox;
                if (field == null) { Check(name + "-long-address", false); continue; }
                string address = "visual-fixture-" + new string('a', 48) + ".example.invalid:11010";
                field.Text = address; field.Focus(); field.CaretIndex = address.Length;
                field.ScrollToHorizontalOffset(double.MaxValue); field.UpdateLayout();
                Rect caret = field.GetRectFromCharacterIndex(address.Length - 1, true);
                Check(name + "-long-address", field.Text == address && field.TextWrapping == TextWrapping.NoWrap &&
                    field.HorizontalOffset > 0 && !caret.IsEmpty && caret.Right <= field.ActualWidth + 1,
                    "full synthetic address preserved; horizontal scrolling reaches final character");
            }
        }
        double originalFov = _settings.FovScale;
        string originalMode = _settings.GameplayMode;
        if (scenario.State == "settings-gameplay")
        {
            fovSlider.Value = originalFov < 1.25 ? originalFov + .05 : originalFov - .05;
            cmbGameplayMode.SelectedValue = originalMode == "MOD" ? "Official" : "MOD";
            Check("gameplay-edits-remain-draft", _settings.FovScale == originalFov && _settings.GameplayMode == originalMode);
        }
        initial.Focus();
        var source = PresentationSource.FromVisual(this);
        if (source == null) { Check("escape-hwnd-source", false); return checks.ToArray(); }
        initial.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var returnTarget = FindName(settings ? "btnSettings" : "btnLocalConnection") as FrameworkElement;
        Check("escape-closes-active-overlay", overlay.Visibility == Visibility.Collapsed);
        Check("escape-restores-entry-focus", returnTarget != null && OwnsHit(Keyboard.FocusedElement as DependencyObject, returnTarget));
        if (scenario.State == "settings-gameplay")
        {
            hook.Invoke(this, [scenario.State]);
            Check("escape-cancels-gameplay-draft", _settings.FovScale == originalFov && _settings.GameplayMode == originalMode &&
                Math.Abs(fovSlider.Value - originalFov) < .00001 && Equals(cmbGameplayMode.SelectedValue, originalMode));
            double nextFov = originalFov < 1.25 ? originalFov + .05 : originalFov - .05;
            string nextMode = originalMode == "MOD" ? "Official" : "MOD";
            fovSlider.Value = nextFov; cmbGameplayMode.SelectedValue = nextMode;
            var save = FindName("btnGameplaySave") as Button;
            Check("gameplay-save-enabled-for-dirty-draft", save?.IsEnabled == true);
            save?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("gameplay-preview-save-commits-memory", Math.Abs(_settings.FovScale-nextFov) < .00001 && _settings.GameplayMode == nextMode,
                "production button uses CommitPreview; disk persistence is covered separately");
            fovSlider.Value = originalFov; cmbGameplayMode.SelectedValue = originalMode;
            initial.Focus();
            initial.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            hook.Invoke(this, [scenario.State]);
            Check("later-cancel-restores-last-preview-save", Math.Abs(fovSlider.Value-nextFov) < .00001 &&
                Equals(cmbGameplayMode.SelectedValue, nextMode) && Math.Abs(_settings.FovScale-nextFov) < .00001 && _settings.GameplayMode == nextMode);
        }
        return checks.ToArray();
    }

    private VisualFrame CaptureVisualSurface(FrameworkElement surface, VisualScenario scenario, string id,
        string output, string kind, bool productionHook)
    {
        double scale = scenario.Dpi / 96.0;
        VisualTreeHelper.SetRootDpi(surface, new DpiScale(scale, scale));
        var size = new Size(scenario.Width, scenario.Height);
        surface.Measure(size);
        surface.Arrange(new Rect(new Point(), size));
        surface.UpdateLayout();
        // Drain deferred text/template rendering at the requested root DPI before
        // reading geometry or drawing the bitmap. Do not repaint PNGs afterwards.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        surface.UpdateLayout();
        var elements = VisualElements(surface).Where(element => Presented(element, surface)).ToArray();
        var named = elements.Where(element => !string.IsNullOrEmpty(element.Name)).GroupBy(element => element.Name)
                            .ToDictionary(group => group.Key, group => group.First());
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(surface)
            .TransformBounds(new Rect(new Point(), element.RenderSize));
        var checks = new List<LayoutCheck>();
        void Check(string category, string target, bool passed, string detail = "") => checks.Add(new(category, target, passed, detail));
        string RectText(Rect rectangle) => string.Create(CultureInfo.InvariantCulture,
            $"x={rectangle.X:F2},y={rectangle.Y:F2},w={rectangle.Width:F2},h={rectangle.Height:F2}");
        var dpi = VisualTreeHelper.GetDpi(surface);
        Check("dpi", "root", Math.Abs(dpi.PixelsPerInchX - scenario.Dpi) < .01, $"observed={dpi.PixelsPerInchX}");
        Check("geometry-control", "shared-edge-is-not-overlap",
            !PositiveAreaOverlap(new Rect(0, 0, 44, 44), new Rect(0, 44, 44, 44)));
        Check("geometry-control", "positive-area-is-overlap",
            PositiveAreaOverlap(new Rect(0, 0, 44, 44), new Rect(0, 43.9999, 44, 44)),
            "even 0.0001 DIP of overlap is rejected; only floating-point edge noise is tolerated");
        if (kind == "launcher-content")
        {
            Check("resource-scope", "logical-window-owner", ReferenceEquals(LogicalTreeHelper.GetParent(surface), this));
            var foreground = TryFindResource("TextMainBrush") as SolidColorBrush;
            foreach (string name in new[] { "txtAppTitle", "txtSectionTitle", "txtToolsTitle", "txtLocalTitle" })
                if (named.TryGetValue(name, out var element) && element is TextBlock title)
                    Check("resource-scope", name, foreground != null && title.Foreground is SolidColorBrush actual && actual.Color == foreground.Color,
                        "production implicit title foreground retained");
        }
        Check("privacy", "password-fields", elements.OfType<PasswordBox>().All(password => password.Password.Length == 0));
        if (productionHook)
        {
            bool local = scenario.State is not ("main" or "settings-public");
            Check("mode-presentation", "mode-selector", FindName("cmbNetworkEnvironment") is System.Windows.Controls.Primitives.Selector mode &&
                Equals(mode.SelectedValue, local ? "Lan" : "Public"), "synthetic scenario selects its actual production mode");
            string expectedPresence = local ? L("在线玩家 / 联机质量", "Players / quality")
                : L("公网连接尚未就绪", "Public connection not ready");
            Check("mode-presentation", "btnPlayersSummary", FindName("btnPlayersSummary") is Button presence &&
                Equals(presence.Content, expectedPresence), local
                    ? "Local immediately displays its neutral presence caption without stale Public state"
                    : "Public displays its unavailable connection caption without LAN presence");
        }
        bool directGameplay = IsDirectGameplayScenario(scenario.State);
        if (scenario.State is "main" or "cancel" or "settings-gameplay")
        {
            var mainPanel = FindName("heroPanel") as FrameworkElement;
            Check("state", "main-direct-gameplay-controls", new[] { "cmbGameplayMode", "fovSlider", "btnFovReset" }.All(name =>
                FindName(name) is FrameworkElement control && mainPanel?.IsAncestorOf(control) == true && Presented(control, surface)),
                "MOD/Official and FOV are directly visible on the SCBL main screen");
            Check("state", "main-game-options-entry-absent", FindName("btnOpenGameOptions") == null && FindName("networkModeDescription") == null,
                "no extra Game options entry or explanatory block replaces direct controls");
        }
        string[] required = scenario.State.StartsWith("settings", StringComparison.Ordinal) && !directGameplay
            ? ["toolsOverlay", "toolsCard", "btnToolsClose"]
            : scenario.State is "discovery" or "join" or "create" && productionHook
                ? ["localDiscoveryOverlay", "localDiscoveryPanel", "btnLocalCancel"]
                : ["heroPanel", "authPanel", "btnLaunch", "btnSettings", "txtUsername", "txtPassword"];
        if (kind == "legacy-production-host-picker") required = [];
        foreach (string name in required)
        {
            Check("state", name, named.ContainsKey(name), "required surface is visible");
            if (!named.TryGetValue(name, out var element)) continue;
            var rect = Bounds(element);
            Check("geometry", name, rect.Width > 0 && rect.Height > 0 && rect.Left >= -.5 && rect.Top >= -.5 &&
                rect.Right <= size.Width + .5 && rect.Bottom <= size.Height + .5, RectText(rect));
        }
        if (productionHook && scenario.State is "settings-public" or "settings-gameplay")
        {
            string[] pageControls = scenario.State == "settings-public"
                ? ["serverSettingsPanel", "txtServerEndpoint", "txtTestServerEndpoint", "btnServerSettingsSave", "btnServerSettingsReset"]
                : directGameplay ? ["heroPanel", "cmbGameplayMode", "fovSlider", "btnGameplaySave", "btnGameplayCancel"]
                : ["gameplaySettingsPanel", "cmbGameplayMode", "fovSlider", "btnGameplaySave", "btnGameplayCancel"];
            foreach (string name in pageControls) Check("state", name, named.ContainsKey(name), "actual settings page control is present");
        }
        bool main = scenario.State is "main" or "cancel" || directGameplay;
        if (main && named.TryGetValue("heroPanel", out var hero) && named.TryGetValue("authPanel", out var auth))
        {
            var left = Bounds(hero); var right = Bounds(auth);
            Check("overlap", "heroPanel/authPanel", left.Right + 12 <= right.Left, $"gap={right.Left-left.Right:F2}");
        }
        var activeActions = VisualActionNames(scenario.State);
        var actionRects = new List<(string Name, Rect Bounds)>();
        foreach (string name in activeActions)
        {
            if (!named.TryGetValue(name, out var element)) continue;
            var rect = Bounds(element);
            Check("hit-area", name, rect.Width >= 32 && rect.Height >= 32, RectText(rect));
            Check("geometry", name, rect.Left >= -.5 && rect.Top >= -.5 && rect.Right <= size.Width + .5 && rect.Bottom <= size.Height + .5, RectText(rect));
            if (element.IsEnabled && element.IsHitTestVisible)
                Check("visual-hit-test", name, OwnsHit(VisualHit(surface, new Point(rect.X+rect.Width/2, rect.Y+rect.Height/2)), element));
            actionRects.Add((name, rect));
        }
        for (int a = 0; a < actionRects.Count; a++)
            for (int b = a + 1; b < actionRects.Count; b++)
            {
                Rect intersection = Rect.Intersect(actionRects[a].Bounds, actionRects[b].Bounds);
                Check("overlap", actionRects[a].Name + "/" + actionRects[b].Name,
                    !PositiveAreaOverlap(actionRects[a].Bounds, actionRects[b].Bounds),
                    intersection.IsEmpty ? "empty" : string.Create(CultureInfo.InvariantCulture,
                        $"intersection={intersection.Width:F9}x{intersection.Height:F9}"));
            }
        // Check the measured production TextBlocks, including labels introduced
        // by control templates. Explicit ellipsis is permitted and recorded by
        // the production template; other clipped text is a layout failure.
        int textIndex = 0;
        foreach (var text in elements.OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(text.Text) || text.ActualWidth <= 1 || text.ActualHeight <= 1) continue;
            if (text.TextTrimming != TextTrimming.None) continue;
            var measured = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                text.Foreground, dpi.PixelsPerDip);
            if (text.TextWrapping != TextWrapping.NoWrap) measured.MaxTextWidth = Math.Max(1, text.ActualWidth);
            string name = string.IsNullOrEmpty(text.Name) ? $"text-{textIndex++}" : text.Name;
            Check("text-clipping", name, measured.Width <= text.ActualWidth + 2 && measured.Height <= text.ActualHeight + 2,
                string.Create(CultureInfo.InvariantCulture, $"needed={measured.Width:F2}x{measured.Height:F2};actual={text.ActualWidth:F2}x{text.ActualHeight:F2}"));
        }
        var bounds = named.Select(pair => { var rect = Bounds(pair.Value); return new ElementBounds(pair.Key, rect.X, rect.Y, rect.Width, rect.Height); }).ToArray();
        string SaveScreenshot(string name)
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width*scale), (int)Math.Ceiling(size.Height*scale),
                scenario.Dpi, scenario.Dpi, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            string path = Path.Combine(output, name);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        string fileName = id + ".png";
        string hash = SaveScreenshot(fileName);
        var additional = new Dictionary<string, string>();
        if (productionHook && scenario.State.StartsWith("settings", StringComparison.Ordinal) && named.TryGetValue("toolsCard", out var card))
        {
            var scroll = VisualElements(card).OfType<ScrollViewer>().FirstOrDefault();
            if (scroll != null)
            {
                double initialVertical = scroll.VerticalOffset, initialHorizontal = scroll.HorizontalOffset;
                scroll.ScrollToEnd(); surface.UpdateLayout();
                foreach (string name in new[] { "btnServerSettingsSave", "btnServerSettingsReset", "btnGameplaySave", "btnGameplayCancel" })
                {
                    if (!named.TryGetValue(name, out var element)) continue;
                    var rect = Bounds(element);
                    var viewport = Bounds(scroll);
                    Check("scroll-accessibility", name, rect.Top >= viewport.Top - 1 && rect.Bottom <= viewport.Bottom + 1 &&
                        rect.Left >= viewport.Left - 1 && rect.Right <= viewport.Right + 1, RectText(rect));
                    Check("hit-area", name, rect.Width >= 32 && rect.Height >= 32, RectText(rect));
                    if (element.IsEnabled)
                        Check("visual-hit-test", name, OwnsHit(VisualHit(surface, new Point(rect.X+rect.Width/2, rect.Y+rect.Height/2)), element));
                }
                additional[id + "-scrolled-bottom.png"] = SaveScreenshot(id + "-scrolled-bottom.png");
                // Supplementary screenshots must not change the starting state
                // of the independent real-window input/focus checks.
                scroll.ScrollToVerticalOffset(initialVertical);
                scroll.ScrollToHorizontalOffset(initialHorizontal);
                surface.UpdateLayout();
                Check("scroll-restoration", "toolsScrollViewer", Math.Abs(scroll.VerticalOffset-initialVertical) < .5 &&
                    Math.Abs(scroll.HorizontalOffset-initialHorizontal) < .5, "supplementary capture restores its initial viewport");
            }
        }
        return new(id, scenario.State, scenario.Language, scenario.Dpi, scenario.Width, scenario.Height, kind,
            productionHook, fileName, hash, dpi.PixelsPerInchX, bounds, checks.ToArray(), null, additional);
    }
}
