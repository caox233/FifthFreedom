using System.Xml.Linq;

namespace SCBL.Client.Tests;

public sealed class LauncherModeSelectionPresentationTests
{
    [Fact]
    public void ModeGroupsAreThreeColumnsWithTwoVerticalChoicesEach()
    {
        XDocument document = LoadMainWindow();
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement panel = FindNamed(document, x, "launchOptionsPanel");
        Assert.Equal("360", (string?)panel.Attribute("Width"));
        Assert.Equal("88", (string?)panel.Attribute("Height"));
        Assert.Equal("0,18,0,10", (string?)panel.Attribute("Margin"));
        Assert.Equal("ModeSelectionPanel_Loaded", (string?)panel.Attribute("Loaded"));

        XElement columns = Assert.Single(panel.Elements(presentation + "Grid.ColumnDefinitions"));
        Assert.Equal(
            ["112", "12", "112", "12", "112"],
            columns.Elements(presentation + "ColumnDefinition")
                .Select(column => (string?)column.Attribute("Width")));

        AssertChoiceGroup(document, x, "cmbNetworkMode", "0", new[] { "快速", "兼容" });
        AssertChoiceGroup(document, x, "cmbGameplayMode", "2", new[] { "官方", "模组" });
        AssertChoiceGroup(document, x, "cmbGameExecutable", "4", new[] { "DX9", "DX11" });

        XElement modeStyle = document
            .Descendants(presentation + "Style")
            .Single(style => (string?)style.Attribute(x + "Key") == "ModeListBox");
        Dictionary<string, string?> modeSetters = modeStyle
            .Elements(presentation + "Setter")
            .Where(setter => setter.Attribute("Value") is not null)
            .ToDictionary(
                setter => (string)setter.Attribute("Property")!,
                setter => (string?)setter.Attribute("Value"),
                StringComparer.Ordinal);
        Assert.Equal("58", modeSetters["Height"]);

        XElement itemStyle = modeStyle
            .Elements(presentation + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "ItemContainerStyle")
            .Descendants(presentation + "Style")
            .Single();
        XElement minHeight = itemStyle
            .Elements(presentation + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "MinHeight");
        Assert.Equal("28", (string?)minHeight.Attribute("Value"));

        XElement itemsPanel = modeStyle
            .Elements(presentation + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "ItemsPanel");
        XElement grid = Assert.Single(itemsPanel.Descendants(presentation + "Grid"));
        Assert.Equal(2, grid.Descendants(presentation + "RowDefinition").Count());

        Assert.All(
            panel.Elements(presentation + "Border"),
            separator => Assert.Equal("58", (string?)separator.Attribute("Height")));
        foreach (string name in new[] { "txtNetworkModeLabel", "txtGameplayModeLabel", "txtRendererLabel" })
            Assert.Equal("0,0,0,5", (string?)FindNamed(document, x, name).Attribute("Margin"));
    }

    [Fact]
    public void ModeAndPlayerLabelsUseCompactUserFacingLayout()
    {
        XDocument document = LoadMainWindow();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("在线玩家", (string?)FindNamed(document, x, "txtPlayersTitle").Attribute("Text"));
        Assert.DoesNotContain(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(x + "Name"),
                "txtPeerHeaderIp",
                StringComparison.Ordinal));

        string modeSource = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));
        Assert.Contains("网络模式", modeSource, StringComparison.Ordinal);
        Assert.Contains("游戏模式", modeSource, StringComparison.Ordinal);
        Assert.Contains("渲染模式", modeSource, StringComparison.Ordinal);

        AssertModeLabel(document, x, "txtNetworkModeLabel", "cmbNetworkMode", "网络模式");
        AssertModeLabel(document, x, "txtGameplayModeLabel", "cmbGameplayMode", "游戏模式");
        AssertModeLabel(document, x, "txtRendererLabel", "cmbGameExecutable", "渲染模式");
        Assert.Contains("L(\"网络模式\", \"NETWORK MODE\")", modeSource, StringComparison.Ordinal);
        Assert.Contains("L(\"游戏模式\", \"GAME MODE\")", modeSource, StringComparison.Ordinal);
        Assert.Contains("L(\"渲染模式\", \"RENDER MODE\")", modeSource, StringComparison.Ordinal);
        Assert.Contains("L(\"渲染模式\", \"Render mode\")", modeSource, StringComparison.Ordinal);

        string peersSource = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.Peers.cs"));
        Assert.DoesNotContain("ipText", peersSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ComponentUpdateProgressUsesSimpleOverallPercentagePresentation()
    {
        XDocument document = LoadMainWindow();
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement panel = FindNamed(document, x, "updateProgressPanel");
        Assert.Equal("Hidden", (string?)panel.Attribute("Visibility"));
        Assert.Equal("Bottom", (string?)panel.Attribute("VerticalAlignment"));
        Assert.Equal("0", (string?)panel.Attribute("Grid.Column"));
        Assert.NotEqual(
            FindNamed(document, x, "heroPanel"),
            panel.Parent);
        XElement progressBar = FindNamed(document, x, "updateProgressBar");
        Assert.Equal("0", (string?)progressBar.Attribute("Minimum"));
        Assert.Equal("100", (string?)progressBar.Attribute("Maximum"));

        string adapter = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ClientUpdates.cs"));
        Assert.Contains("txtUpdateProgress.Text = L(\"更新中\", \"Updating\");", adapter, StringComparison.Ordinal);
        Assert.Contains("updateProgressBar.Value = progress.Percentage;", adapter, StringComparison.Ordinal);
        string normalizedAdapter = adapter.Replace("\r\n", "\n", StringComparison.Ordinal);
        int startupFlowStart = normalizedAdapter.IndexOf(
            "private async Task<bool> EnsureIndependentComponentUpdatesAsync",
            StringComparison.Ordinal);
        Assert.True(startupFlowStart >= 0);
        int prepareStart = normalizedAdapter.IndexOf(
            ".PrepareAsync(cancellationToken);",
            startupFlowStart,
            StringComparison.Ordinal);
        Assert.True(prepareStart > startupFlowStart);
        string startupCheck = normalizedAdapter[startupFlowStart..prepareStart];
        Assert.Contains("SetClientUpdateProgressAllowed(false);", startupCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("SetClientUpdateProgressAllowed(true);", startupCheck, StringComparison.Ordinal);
        Assert.Contains("SetBusy(true", startupCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("SetUpdatingState(true);", startupCheck, StringComparison.Ordinal);
        int pendingCheck = normalizedAdapter.IndexOf(
            "if (!prepared.HasPendingUpdates)",
            prepareStart,
            StringComparison.Ordinal);
        int actualUpdatingStart = normalizedAdapter.IndexOf(
            "SetUpdatingState(true);",
            pendingCheck,
            StringComparison.Ordinal);
        int actualUpdateProgressStart = normalizedAdapter.IndexOf(
            "SetClientUpdateProgressAllowed(true);",
            pendingCheck,
            StringComparison.Ordinal);
        int applyConfirmedStart = normalizedAdapter.IndexOf(
            ".ApplyConfirmedAsync(prepared, cancellationToken);",
            actualUpdateProgressStart,
            StringComparison.Ordinal);
        Assert.True(pendingCheck > prepareStart);
        Assert.True(actualUpdatingStart > pendingCheck);
        Assert.True(actualUpdateProgressStart > actualUpdatingStart);
        Assert.True(applyConfirmedStart > actualUpdateProgressStart);
        int progressHandlerStart = normalizedAdapter.IndexOf(
            "private void ClientUpdateCoordinator_ProgressChanged",
            StringComparison.Ordinal);
        Assert.True(progressHandlerStart >= 0);
        int progressAllowedStart = normalizedAdapter.IndexOf(
            "private void SetClientUpdateProgressAllowed",
            progressHandlerStart,
            StringComparison.Ordinal);
        Assert.True(progressAllowedStart > progressHandlerStart);
        string progressHandler = normalizedAdapter[progressHandlerStart..progressAllowedStart];
        Assert.DoesNotContain("ClientUpdateProgressStage.Completed", progressHandler, StringComparison.Ordinal);
        Assert.Contains("ResetClientUpdateProgressPresentation();", adapter, StringComparison.Ordinal);
        Assert.Contains("SetClientUpdateProgressAllowed(false)", adapter, StringComparison.Ordinal);
        Assert.Contains(
            "if (!prepared.HasPendingUpdates)\n                {\n                    SetClientUpdateProgressAllowed(false);",
            normalizedAdapter,
            StringComparison.Ordinal);
        Assert.Contains(
            "case ClientUpdateHostCommandKind.Continue:\n                SetClientUpdateProgressAllowed(false);",
            normalizedAdapter,
            StringComparison.Ordinal);
        Assert.Contains("Visibility.Hidden", adapter, StringComparison.Ordinal);
        Assert.Contains("updateProgressBar.Value = 0d;", adapter, StringComparison.Ordinal);
        Assert.Contains("txtUpdateProgress.Text = string.Empty;", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("progress.IsIndeterminate", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("GetClientUpdateProgressText", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Getting update information", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Verifying update information", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Checking files", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading update", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Verifying downloaded files", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Applying update…", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedSnapshotCommitsAtomicDetailsAndRejectsAsyncServerPathSamples()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.Presentation.cs"));
        string normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        int snapshotStart = normalizedSource.IndexOf(
            "private void ApplyNetworkStatusSnapshot",
            StringComparison.Ordinal);
        Assert.True(snapshotStart >= 0);
        int clearStart = normalizedSource.IndexOf(
            "private void ClearServerPathPresentation",
            snapshotStart,
            StringComparison.Ordinal);
        Assert.True(clearStart > snapshotStart);
        int setStatusStart = normalizedSource.IndexOf(
            "private void SetServerStatus",
            clearStart,
            StringComparison.Ordinal);
        Assert.True(setStatusStart > clearStart);

        string snapshotHandler = normalizedSource[snapshotStart..clearStart];
        Assert.Contains("if (snapshot.Phase == NetworkPhase.Connected)", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("NetworkStatusSnapshot.HasCompleteConnectionDetails(", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("snapshot.UnderlayAddressFamily,", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("snapshot.RuntimeObservationId)", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("_lastConnectionTransport = snapshot.TransportMode.Trim();", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("_lastConnectionAddressFamily = snapshot.UnderlayAddressFamily.Trim();", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains(
            "SetServerStatus(YellowBrush, \"\", ServerStatusKind.ServerConnecting);\n                return;",
            snapshotHandler,
            StringComparison.Ordinal);
        Assert.Contains("SetServerStatus(GreenBrush, \"\", ServerStatusKind.Normal);", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("case NetworkPhase.Reconnecting:", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("ServerStatusKind.TunnelReconnecting", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("case NetworkPhase.NetworkFailed:", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("case NetworkPhase.TunnelFailed:", snapshotHandler, StringComparison.Ordinal);
        Assert.Contains("case NetworkPhase.ServerFailed:", snapshotHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("_isGameRunning", snapshotHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("GetInteractionState", snapshotHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("cmbGameplayMode", snapshotHandler, StringComparison.Ordinal);
        Assert.True(
            snapshotHandler.IndexOf("_lastConnectionAddressFamily = snapshot.UnderlayAddressFamily.Trim();", StringComparison.Ordinal)
            < snapshotHandler.IndexOf("SetServerStatus(GreenBrush, \"\", ServerStatusKind.Normal);", StringComparison.Ordinal));
        Assert.Contains("else\n        {\n            ClearServerPathPresentation();\n        }", snapshotHandler, StringComparison.Ordinal);

        string clearMethod = normalizedSource[clearStart..setStatusStart];
        Assert.Contains("_lastConnectionTransport = \"\";", clearMethod, StringComparison.Ordinal);
        Assert.Contains("_lastConnectionAddressFamily = \"\";", clearMethod, StringComparison.Ordinal);
        Assert.Contains("_serverUsesTcpFallback = false;", clearMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshServerPathMetadataAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyServerPathDisplaySample", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitServerPathDisplay", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkModeAvailabilityUsesPlatformStateAndPresentsManualGameActiveRejection()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));

        Assert.Contains(
            "cmbNetworkMode.SetCurrentValue(IsEnabledProperty, current.CanChange);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmbNetworkMode.SetCurrentValue(IsEnabledProperty, state.CanChange);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ShowNetworkModeUnavailableAsync(state)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cmbGameplayMode.IsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("NetworkModeChangeOutcome.RejectedGameActive", source, StringComparison.Ordinal);
        Assert.Contains("if (state.IsGameActive)", source, StringComparison.Ordinal);
        Assert.Contains("游戏运行期间无法切换网络模式", source, StringComparison.Ordinal);
        Assert.Contains("Network mode cannot be changed while the game is running", source, StringComparison.Ordinal);
        Assert.Contains("L(\"正在切换网络模式\", \"Changing Network Mode\")", source, StringComparison.Ordinal);
        Assert.Contains("L(\"请稍候。\", \"Please wait.\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferredChoicesRenderOnTopWithStableSelectionIndices()
    {
        XDocument document = LoadMainWindow();
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("0", (string?)FindNamed(document, x, "itemFastNetworkMode").Attribute("Grid.Row"));
        Assert.Equal("0", (string?)FindNamed(document, x, "itemOfficialMode").Attribute("Grid.Row"));
        Assert.Equal("1", (string?)FindNamed(document, x, "itemModMode").Attribute("Grid.Row"));

        XElement renderer = FindNamed(document, x, "cmbGameExecutable");
        XElement dx9 = renderer.Elements(presentation + "ListBoxItem")
            .Single(item => (string?)item.Attribute("Content") == "DX9");
        XElement dx11 = renderer.Elements(presentation + "ListBoxItem")
            .Single(item => (string?)item.Attribute("Content") == "DX11");

        Assert.Equal("1", (string?)dx9.Attribute("Grid.Row"));
        Assert.Equal("0", (string?)dx11.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)renderer.Attribute("SelectedIndex"));
    }

    [Fact]
    public void ModePresentationUsesApprovedBilingualPlayerText()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));

        Assert.Contains("如遇启动器连接失败、游戏网络不稳定等网络问题，可尝试兼容模式。", source, StringComparison.Ordinal);
        Assert.Contains("If the Launcher cannot connect or game networking is unstable, try Compatible mode.", source, StringComparison.Ordinal);
        Assert.Contains("官方模式使用联机核心，不加载玩法或诊断模组。", source, StringComparison.Ordinal);
        Assert.Contains("Official mode uses the online core without gameplay or diagnostic mods.", source, StringComparison.Ordinal);
        Assert.Contains("模组模式加载玩法 DLL，并默认启用已完成的玩法功能。", source, StringComparison.Ordinal);
        Assert.Contains("MOD loads the gameplay DLL and enables all completed gameplay features.", source, StringComparison.Ordinal);
        Assert.Contains("L(\"官方\", \"Official\")", source, StringComparison.Ordinal);
        Assert.Contains("L(\"模组\", \"MOD\")", source, StringComparison.Ordinal);
        Assert.Contains("!availability.IsModSelectable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("App.ComponentUpdateChannel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModEnabled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HookDllService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", source, StringComparison.Ordinal);
        Assert.DoesNotContain("测试通道开放", source, StringComparison.Ordinal);
        Assert.DoesNotContain("test channel", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("开发中", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Coming soon", source, StringComparison.Ordinal);

        string guide = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.Guide.cs"));
        Assert.Contains("官方与模组使用隔离存档", guide, StringComparison.Ordinal);
        Assert.Contains("Official and MOD use isolated saves", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("测试通道开放", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("test channel", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("开发中", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("coming soon", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("破坏者范围", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TickrateDiagnostics", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialAndModExposeTheSharedAccessibleFovDialog()
    {
        XDocument document = LoadMainWindow();
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement modItem = FindNamed(document, x, "itemModMode");
        XElement officialItem = FindNamed(document, x, "itemOfficialMode");
        Assert.Equal(
            "OfficialModeItem_PreviewMouseLeftButtonDown",
            (string?)officialItem.Attribute("PreviewMouseLeftButtonDown"));
        Assert.Equal(
            "OfficialModeItem_PreviewKeyDown",
            (string?)officialItem.Attribute("PreviewKeyDown"));
        Assert.Equal(
            "ModModeItem_PreviewMouseLeftButtonDown",
            (string?)modItem.Attribute("PreviewMouseLeftButtonDown"));
        Assert.Equal(
            "ModModeItem_PreviewKeyDown",
            (string?)modItem.Attribute("PreviewKeyDown"));

        XElement overlay = FindNamed(document, x, "modModeOverlay");
        Assert.Equal("Collapsed", (string?)overlay.Attribute("Visibility"));
        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.DirectionalNavigation"));
        Assert.Equal("ModModeOverlay_PreviewKeyDown", (string?)overlay.Attribute("PreviewKeyDown"));
        Assert.False(string.IsNullOrWhiteSpace((string?)overlay.Attribute("AutomationProperties.Name")));

        XElement slider = FindNamed(document, x, "modFovSlider");
        Assert.Equal("1.0", (string?)slider.Attribute("Minimum"));
        Assert.Equal("1.3", (string?)slider.Attribute("Maximum"));
        Assert.Equal("1.2", (string?)slider.Attribute("Value"));
        Assert.Equal("0.05", (string?)slider.Attribute("SmallChange"));
        Assert.Equal("0.05", (string?)slider.Attribute("TickFrequency"));
        Assert.Equal("True", (string?)slider.Attribute("IsSnapToTickEnabled"));
        Assert.Equal("ModFovSlider_ValueChanged", (string?)slider.Attribute("ValueChanged"));
        Assert.False(string.IsNullOrWhiteSpace((string?)slider.Attribute("AutomationProperties.Name")));
        Assert.False(string.IsNullOrWhiteSpace((string?)slider.Attribute("AutomationProperties.HelpText")));

        XElement thumbStyle = document
            .Descendants(presentation + "Style")
            .Single(style => (string?)style.Attribute(x + "Key") == "ModFovSliderThumb");
        XElement thumbCircle = Assert.Single(thumbStyle.Descendants(presentation + "Ellipse"));
        Assert.Equal("ThumbCircle", (string?)thumbCircle.Attribute(x + "Name"));
        Dictionary<string, string?> thumbSetters = thumbStyle
            .Elements(presentation + "Setter")
            .Where(setter => setter.Attribute("Value") is not null)
            .ToDictionary(
                setter => (string)setter.Attribute("Property")!,
                setter => (string?)setter.Attribute("Value"),
                StringComparer.Ordinal);
        Assert.Equal("20", thumbSetters["Width"]);
        Assert.Equal("20", thumbSetters["Height"]);

        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));
        Assert.Contains("App.ModSelectionPreparation.PrepareAsync(", source, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split("App.ModSelectionPreparation.PrepareAsync(", StringSplitOptions.None).Length - 1);
        Assert.Contains("PrepareAndShowModModeDialogAsync(availability)", source, StringComparison.Ordinal);
        Assert.Contains("ShowModModeDialog();", source, StringComparison.Ordinal);
        Assert.Contains("Game Field of View", source, StringComparison.Ordinal);
        Assert.Contains("Official is the default mode", source, StringComparison.Ordinal);
        Assert.Contains("_settings.ModFovScale", source, StringComparison.Ordinal);
        Assert.Contains("LauncherSettingsService.NormalizeModFovScale", source, StringComparison.Ordinal);
        Assert.Contains("SaveRuntimeSettings();", source, StringComparison.Ordinal);
        Assert.Contains("modFovSlider.Focus();", source, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Focus(modFovSlider);", source, StringComparison.Ordinal);
        Assert.Contains("e.Key != Key.Enter && e.Key != Key.Space", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FusionFix", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SplinterCellBlacklist.FusionFix.ini", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HookDllService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", source, StringComparison.Ordinal);
        Assert.DoesNotContain("scbl_gameplay", source, StringComparison.Ordinal);

        string window = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml.cs"));
        string loadedHandler = SliceMethod(window, "private async void Window_Loaded", "private async Task StartNetworkLifecycleAsync");
        string launchHandler = SliceMethod(window, "private async void LaunchButton_Click", "private async Task RunLaunchFlowAsync");
        Assert.DoesNotContain("HandleFirstRunFovPromptAsync", loadedHandler, StringComparison.Ordinal);
        Assert.Contains("await HandleFirstRunFovPromptAsync();", launchHandler, StringComparison.Ordinal);
        Assert.True(
            launchHandler.IndexOf("await HandleFirstRunFovPromptAsync();", StringComparison.Ordinal)
            < launchHandler.IndexOf("EnsureLatestComponentsBeforeLaunchAsync", StringComparison.Ordinal));
        Assert.Contains("if (_settings.FovPromptHandled || _allowClose)", source, StringComparison.Ordinal);
        Assert.Contains("ShowModModeDialog();", source, StringComparison.Ordinal);
        Assert.Contains("_settings.FovPromptHandled = true;", source, StringComparison.Ordinal);
        Assert.Contains("firstRunPrompt?.TrySetResult(true);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedExistingModUsesDedicatedPathFreePlayerMessage()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.Dialogs.cs"));

        Assert.Contains(
            "FusionFix was not installed because an existing MOD file must be preserved.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ex.Message.Contains(FusionFixPreserveConflictMarker, StringComparison.Ordinal)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("检测到已有 MOD 文件", source, StringComparison.Ordinal);
        Assert.Contains("为避免覆盖未安装 FusionFix", source, StringComparison.Ordinal);
        Assert.Contains("请保留现有 MOD，或先自行备份并移除冲突文件后重试", source, StringComparison.Ordinal);
        Assert.Contains("Existing MOD Files Detected", source, StringComparison.Ordinal);
        Assert.Contains("FusionFix was not installed to avoid overwriting them", source, StringComparison.Ordinal);
        Assert.Contains("back up and remove the conflicting files yourself before trying again", source, StringComparison.Ordinal);
        Assert.Contains(
            "请关闭游戏，检查杀毒软件是否拦截 SCBL，然后重试。",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Close the game, check whether antivirus is blocking SCBL, and try again.",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ConflictRelativePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("conflict=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporaryOfficialProjectionDoesNotOverwritePersistedModPreference()
    {
        string presentation = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.Presentation.cs"));
        string normalizedPresentation = presentation.Replace("\r\n", "\n", StringComparison.Ordinal);
        int availabilityStart = normalizedPresentation.IndexOf(
            "private void UpdateLaunchConfigurationAvailability()",
            StringComparison.Ordinal);
        int interactionStart = normalizedPresentation.IndexOf(
            "private LauncherInteractionState GetInteractionState()",
            availabilityStart,
            StringComparison.Ordinal);
        Assert.True(availabilityStart >= 0);
        Assert.True(interactionStart > availabilityStart);
        string availability = normalizedPresentation[availabilityStart..interactionStart];

        Assert.Contains("GetPreferredGameplayMode(),", availability, StringComparison.Ordinal);
        Assert.Contains(
            "availability.EffectiveGameplayMode == GameplayMode.Mod\n            ? 1\n            : 0",
            availability,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmbGameplayMode.SelectedIndex = effectiveGameplayModeIndex;",
            availability,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.GameplayMode =", availability, StringComparison.Ordinal);
        Assert.DoesNotContain("_confirmedGameplayModeIndex =", availability, StringComparison.Ordinal);
        Assert.DoesNotContain("ModSelectionPreparation", availability, StringComparison.Ordinal);
        Assert.Contains(
            "GameplayModeNames.ParseOrOfficial(_settings.GameplayMode) == GameplayMode.Mod",
            presentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmbGameplayMode.SelectedIndex = GetPreferredGameplayModeIndex();",
            presentation,
            StringComparison.Ordinal);

        string runtimeSettings = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ServerRuntime.cs"));
        Assert.Contains("private void SaveRuntimeSettings()", runtimeSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("GameplayMode", runtimeSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSelectedGameplayMode()", runtimeSettings, StringComparison.Ordinal);

        string modeSelection = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));
        Assert.Contains(
            "_confirmedGameplayModeIndex = GetPreferredGameplayModeIndex();",
            modeSelection,
            StringComparison.Ordinal);
        Assert.Contains(
            "_settings.GameplayMode = GameplayModeNames.Official;",
            modeSelection,
            StringComparison.Ordinal);
        Assert.Contains(
            "_settings.GameplayMode = GameplayModeNames.Mod;",
            modeSelection,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            modeSelection.Split("_settings.GameplayMode =", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RuntimeValidationAwaitsFinalProcessSpecificResultBeforeWarning()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.GameProcessMonitor.cs"));
        string normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        int validationStart = normalizedSource.IndexOf(
            "if (finalProcessConfirmed && !runtimeValidated)",
            StringComparison.Ordinal);
        int timeoutStart = normalizedSource.IndexOf(
            "if (!finalProcessConfirmed && DateTime.UtcNow >= deadline)",
            validationStart,
            StringComparison.Ordinal);
        Assert.True(validationStart >= 0);
        Assert.True(timeoutStart > validationStart);
        string validationBlock = normalizedSource[validationStart..timeoutStart];

        Assert.Contains(
            "int validatedProcessId = activeCandidatePid > 0\n                            ? activeCandidatePid\n                            : initialProcessId;",
            validationBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "result = await _gameRuntimeValidationService.ValidateAsync(\n                                validatedProcessId,\n                                activeGamePath,\n                                expectedHookPath,\n                                token);",
            validationBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException) when (token.IsCancellationRequested)\n                        {\n                            return;\n                        }",
            validationBlock,
            StringComparison.Ordinal);
        Assert.Contains("if (!result.IsValid)", validationBlock, StringComparison.Ordinal);
        Assert.Contains("Runtime validation warning: \" + result.Warning", validationBlock, StringComparison.Ordinal);
        Assert.Contains("联机功能可能未生效", validationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_gameRuntimeValidationService.Validate(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("actualProcessName", validationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", validationBlock, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split("_gameRuntimeValidationService.ValidateAsync(", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void NetworkModeUiOnlyConsumesPlatformStateAndCommand()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));

        Assert.Contains("GetNetworkModeState()", source, StringComparison.Ordinal);
        Assert.Contains("ChangeNetworkModeAsync(requestedMode, CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.Contains("快速模式连接失败", source, StringComparison.Ordinal);
        Assert.Contains("切换兼容模式", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            source.Split(
                "MessageBoxResult confirmation = await ShowNetworkModeConfirmDialogAsync(",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("Task<MessageBoxResult> task = ShowConfirmDialogAsync", source, StringComparison.Ordinal);
        Assert.Contains("btnDialogYes.Focus();", source, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Focus(btnDialogYes);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_networkOrchestrator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_settingsService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureReadyAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EasyTier", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientNetworkModeNames", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkModeSwitchUsesTargetedManualRestartInsteadOfFullRepair()
    {
        string compositionRoot = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml.cs"));
        string coordinator = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "Application",
            "NetworkModeCoordinator.cs"));

        Assert.Contains("NetworkEnsureMode.Manual", compositionRoot, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NetworkEnsureMode.Repair,\n                \"network mode changed\"",
            compositionRoot.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("_stageMode(previous)", coordinator, StringComparison.Ordinal);
        Assert.Contains("await _rebuildNetwork(cancellationToken)", coordinator, StringComparison.Ordinal);

        int startupLifecycle = compositionRoot.IndexOf(
            "private async Task StartNetworkLifecycleAsync",
            StringComparison.Ordinal);
        int compatibleOffer = compositionRoot.IndexOf(
            "await OfferCompatibleModeAfterFastStartupFailureAsync(failure)",
            startupLifecycle,
            StringComparison.Ordinal);
        int watchdogStart = compositionRoot.IndexOf(
            "_networkSessionCoordinator.StartWatchdog()",
            startupLifecycle,
            StringComparison.Ordinal);
        Assert.True(startupLifecycle >= 0);
        Assert.True(compatibleOffer > startupLifecycle);
        Assert.True(watchdogStart > compatibleOffer);
    }

    [Fact]
    public void ModeSelectorsKeepKeyboardAndAccessibilityMetadata()
    {
        XDocument document = LoadMainWindow();
        XNamespace presentation = document.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement modeStyle = document
            .Descendants(presentation + "Style")
            .Single(style => (string?)style.Attribute(x + "Key") == "ModeListBox");
        Dictionary<string, string?> setters = modeStyle
            .Elements(presentation + "Setter")
            .Where(setter => setter.Attribute("Value") is not null)
            .ToDictionary(
                setter => (string)setter.Attribute("Property")!,
                setter => (string?)setter.Attribute("Value"),
                StringComparer.Ordinal);

        Assert.Equal("Cycle", setters["KeyboardNavigation.DirectionalNavigation"]);
        Assert.Equal("Disabled", setters["ScrollViewer.HorizontalScrollBarVisibility"]);
        Assert.Equal("Disabled", setters["ScrollViewer.VerticalScrollBarVisibility"]);

        foreach (string name in new[] { "cmbNetworkMode", "cmbGameplayMode", "cmbGameExecutable" })
        {
            XElement selector = FindNamed(document, x, name);
            Assert.False(string.IsNullOrWhiteSpace((string?)selector.Attribute("AutomationProperties.Name")));
        }

        string source = File.ReadAllText(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.ModeSelection.cs"));
        Assert.Contains("AutomationProperties.SetHelpText", source, StringComparison.Ordinal);
        Assert.Contains("miLanguageToggle.Click += ModeSelectionLanguageToggle_Click", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherScalingStillUsesBoundedUniformViewbox()
    {
        XDocument document = LoadMainWindow();
        XNamespace presentation = document.Root!.Name.Namespace;
        XElement viewbox = document.Descendants(presentation + "Viewbox").First();

        Assert.Equal("Uniform", (string?)viewbox.Attribute("Stretch"));
        Assert.Equal("DownOnly", (string?)viewbox.Attribute("StretchDirection"));
        Assert.Equal("0", (string?)document.Root!.Attribute("MinWidth"));
        Assert.Equal("0", (string?)document.Root.Attribute("MinHeight"));
    }

    private static void AssertChoiceGroup(
        XDocument document,
        XNamespace x,
        string selectorName,
        string expectedColumn,
        IReadOnlyCollection<string> expectedChoices)
    {
        XNamespace presentation = document.Root!.Name.Namespace;
        XElement selector = FindNamed(document, x, selectorName);
        XElement group = selector.Parent
            ?? throw new InvalidDataException($"{selectorName} has no group parent.");

        Assert.Equal(expectedColumn, (string?)group.Attribute("Grid.Column") ?? "0");
        Assert.Equal(2, selector.Elements(presentation + "ListBoxItem").Count());
        Assert.Equal(
            expectedChoices.OrderBy(value => value, StringComparer.Ordinal),
            selector.Elements(presentation + "ListBoxItem")
                .Select(item => (string?)item.Attribute("Content") ?? string.Empty)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static XElement FindNamed(XDocument document, XNamespace x, string name)
        => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);

    private static void AssertModeLabel(
        XDocument document,
        XNamespace x,
        string labelName,
        string selectorName,
        string expectedText)
    {
        Assert.Equal(expectedText, (string?)FindNamed(document, x, labelName).Attribute("Text"));
        Assert.Equal(
            expectedText,
            (string?)FindNamed(document, x, selectorName).Attribute("AutomationProperties.Name"));
    }

    private static XDocument LoadMainWindow()
        => XDocument.Load(FindRepositoryFile(
            "client",
            "ScblPublicLauncher",
            "MainWindow.xaml"), LoadOptions.PreserveWhitespace);

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing method boundary: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Unable to locate repository file.", Path.Combine(relativeParts));
    }
}
