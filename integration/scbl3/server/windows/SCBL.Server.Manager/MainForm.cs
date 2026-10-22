using System.Drawing;
using System.Diagnostics;

namespace Scbl.Server.Manager;

public sealed class MainForm : Form
{
    private readonly ServerPaths _paths;
    private readonly Label _state = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold) };
    private readonly Label _details = new() { AutoSize = true };
    private readonly Label _operation = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly ProgressBar _activity = new() { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false };
    private readonly TextBox _serverId = new();
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _publicHost = new();
    private readonly NumericUpDown _gamePort = Port(11010);
    private readonly NumericUpDown _updatePort = Port(18080);
    private readonly NumericUpDown _controlPort = Port(19080);
    private readonly CheckBox _ipv6 = new() { Text = "启用 IPv6 监听", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "Windows 开机时自动启动 SCBL 服务", AutoSize = true };
    private readonly CheckBox _ddns = new() { Text = "启用 DDNS-Go（配置后生效）", AutoSize = true };
    private readonly NumericUpDown _ddnsPort = Port(9876);
    private readonly FlowLayoutPanel _ddnsSettings = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
    private Label? _ddnsSettingsLabel;
    private readonly TextBox _logs = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly Button _install = Button("首次安装");
    private readonly Button _uninstall = Button("卸载服务端");
    private readonly Button _start = Button("启动");
    private readonly Button _stop = Button("停止");
    private readonly Button _restart = Button("重启");
    private readonly Button _save = Button("保存设置并重启服务");
    private readonly Label _clientState = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 11, FontStyle.Bold) };
    private readonly TextBox _clientDetails = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };
    private string? _lastMode;
    private bool _operationRunning;

    public MainForm(ServerPaths paths)
    {
        _paths = paths;
        Text = "SCBL 原生 Windows 服务端";
        MinimumSize = new Size(780, 620);
        Size = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        _mode.Items.AddRange(["stable", "test"]);
        _mode.SelectedIndexChanged += (_, _) => ApplyModePortOffset();
        Button openDdns = Button("打开配置页");
        openDdns.Click += (_, _) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{decimal.ToInt32(_ddnsPort.Value)}") { UseShellExecute = true });
        _ddnsSettings.Controls.Add(_ddnsPort);
        _ddnsSettings.Controls.Add(openDdns);
        _ddns.CheckedChanged += (_, _) => UpdateDdnsSettingsVisibility();
        Controls.Add(BuildTabs());
        Shown += async (_, _) => await RunUiAsync(async () => { LoadConfig(); await RefreshStatusAsync(); RefreshClientStatus(); });
    }

    private Control BuildTabs()
    {
        TabControl tabs = new() { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildStatusPage());
        tabs.TabPages.Add(BuildSettingsPage());
        tabs.TabPages.Add(BuildMaintenancePage());
        tabs.TabPages.Add(BuildClientPublicationPage());
        return tabs;
    }

    private TabPage BuildStatusPage()
    {
        TabPage page = new("状态");
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        panel.Controls.Add(_state);
        panel.Controls.Add(_details);
        panel.SetFlowBreak(_details, true);
        Button refresh = Button("刷新");
        refresh.Click += async (_, _) => await RunUiAsync(RefreshStatusAsync);
        _start.Click += async (_, _) => await RunUiAsync(StartOrInstallAsync);
        _stop.Click += async (_, _) => await RunUiAsync(async () => { await WindowsServiceInstaller.StopAsync(); await RefreshStatusAsync(); });
        _restart.Click += async (_, _) => await RunUiAsync(async () => { await RestartServiceAsync(); await RefreshStatusAsync(); });
        panel.Controls.Add(refresh);
        panel.Controls.Add(_start);
        panel.Controls.Add(_stop);
        panel.Controls.Add(_restart);
        panel.SetFlowBreak(_restart, true);
        panel.Controls.Add(_operation);
        layout.Controls.Add(panel, 0, 0);
        layout.Controls.Add(_activity, 0, 1);
        layout.Controls.Add(_logs, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        TabPage page = new("设置");
        TableLayoutPanel table = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16), ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, "服务器 ID", _serverId);
        AddRow(table, "服务器身份", _mode);
        AddRow(table, "服务器地址（域名或 IP）", _publicHost);
        AddRow(table, "游戏端口（TCP/UDP）", _gamePort);
        AddRow(table, "更新端口（TCP）", _updatePort);
        AddRow(table, "网络", _ipv6);
        AddRow(table, "动态域名", _ddns);
        _ddnsSettingsLabel = AddRow(table, "DDNS-Go 管理端口", _ddnsSettings);
        UpdateDdnsSettingsVisibility();
        _save.AutoSize = true;
        _save.Click += async (_, _) => await RunUiAsync(SaveAndRestartAsync);
        AddRow(table, "", _save);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildMaintenancePage()
    {
        TabPage page = new("安装与维护");
        FlowLayoutPanel panel = Flow();
        _install.Click += async (_, _) => await RunUiAsync(InstallAsync);
        panel.Controls.Add(_install);
        Button update = Button("安装更新 ZIP"); update.Click += async (_, _) => await RunUiAsync(ApplyUpdateAsync); panel.Controls.Add(update);
        Button backup = Button("立即备份"); backup.Click += async (_, _) => await RunUiAsync(() =>
        {
            return CreateBackupAndShowAsync();
        }); panel.Controls.Add(backup);
        Button external = Button("检查并更新外部组件"); external.Click += async (_, _) => await RunUiAsync(UpdateExternalComponentsAsync); panel.Controls.Add(external);
        Button rollback = Button("回滚上一运行版本"); rollback.Click += async (_, _) => await RunUiAsync(RollbackAsync); panel.Controls.Add(rollback);
        Button restore = Button("从备份恢复"); restore.Click += async (_, _) => await RunUiAsync(RestoreBackupAsync); panel.Controls.Add(restore);
        Button openData = Button("打开数据目录"); openData.Click += (_, _) => { _paths.EnsureLayout(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _paths.DataRoot) { UseShellExecute = true }); }; panel.Controls.Add(openData);
        _uninstall.Click += async (_, _) => await RunUiAsync(UninstallAsync);
        panel.Controls.Add(_uninstall);
        panel.SetFlowBreak(_uninstall, true);
        panel.Controls.Add(_autoStart);
        Button applyAutoStart = Button("应用开机启动设置");
        applyAutoStart.Click += async (_, _) => await RunUiAsync(ApplyAutoStartAsync);
        panel.Controls.Add(applyAutoStart);
        Label note = new()
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "首次安装请选择随安装包提供的 Windows full ZIP。更新只替换 ZIP 中声明的组件；失败会恢复上一个 Runtime。EasyTier 和 DDNS-Go 首次使用时下载，已有缓存不会重复下载。",
        };
        panel.SetFlowBreak(applyAutoStart, true);
        panel.Controls.Add(note);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildClientPublicationPage()
    {
        TabPage page = new("客户端发布");
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        FlowLayoutPanel actions = Flow();
        Button publish = Button("发布客户端 ZIP");
        publish.Click += async (_, _) => await RunUiAsync(PublishClientArchiveAsync);
        Button refresh = Button("刷新状态");
        refresh.Click += (_, _) => RefreshClientStatus();
        actions.Controls.Add(publish);
        actions.Controls.Add(refresh);
        Label note = new()
        {
            AutoSize = true,
            MaximumSize = new Size(780, 0),
            Text = "选择完整客户端 ZIP 或组件补丁 ZIP。完整包会自动生成六组件更新；补丁会应用到当前客户端快照。两种方式都会重新生成最新 /SCBL.zip，并在同一事务中更新组件清单和服务器配置。",
        };
        layout.Controls.Add(_clientState, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        actions.SetFlowBreak(refresh, true);
        actions.Controls.Add(note);
        layout.Controls.Add(_clientDetails, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private async Task PublishClientArchiveAsync()
    {
        using OpenFileDialog dialog = new() { Filter = "SCBL 客户端 ZIP (*.zip)|*.zip", Title = "选择完整客户端或补丁 ZIP" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ServerConfiguration config = ServerConfiguration.LoadOrCreate(_paths);
        DialogResult confirmation = MessageBox.Show(
            $"将向 {config.Mode} 服务端发布此客户端 ZIP。\r\n\r\n{dialog.FileName}\r\n\r\n程序会自动识别完整包或补丁，生成六组件更新与最新 SCBL.zip，并在校验全部通过后一次切换。是否继续？",
            "发布客户端 ZIP",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirmation != DialogResult.Yes) return;
        ReportOperation("正在校验客户端 ZIP、生成组件更新和最新完整包……");
        ClientArchivePublication result = await Task.Run(() => new ClientPublicationManager(_paths).PublishArchive(
            dialog.FileName,
            config.Mode,
            config,
            ServerSecrets.Load(_paths)));
        string lines = string.Join(Environment.NewLine, result.Components.OrderBy(item => item.Key).Select(item => $"{item.Key}: {item.Value}"));
        string url = config.BuildPublicUpdateBaseUrl().TrimEnd('/') + result.PublicPath;
        MessageBox.Show(
            $"客户端 {result.Version} 发布完成。\r\n输入类型：{(result.InputKind == "full" ? "完整包" : "补丁")}\r\n\r\n{lines}\r\n\r\n最新完整包：{result.Size:N0} bytes\r\nSHA256：{result.Sha256}\r\n\r\n固定下载地址：\r\n{url}",
            "客户端发布",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        RefreshClientStatus();
    }

    private void RefreshClientStatus()
    {
        try
        {
            ServerConfiguration config = ServerConfiguration.LoadForEditing(_paths);
            ClientPublicationStatus status = new ClientPublicationManager(_paths).Status(config.Mode);
            _clientState.Text = $"当前客户端通道：{status.Channel}";
            List<string> lines = status.Components.OrderBy(item => item.Key).Select(item => $"{item.Key}: {item.Value}").ToList();
            lines.Add("完整客户端：" + (status.FullPackageVersion ?? "尚未发布"));
            lines.Add("固定地址：" + config.BuildPublicUpdateBaseUrl().TrimEnd('/') + "/SCBL.zip");
            _clientDetails.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception error)
        {
            _clientState.Text = "客户端发布状态不可用";
            _clientDetails.Text = error.Message;
        }
    }

    private void LoadConfig()
    {
        _paths.EnsureLayout();
        ServerConfiguration config = ServerConfiguration.LoadForEditing(_paths);
        _serverId.Text = config.ServerId;
        _mode.SelectedItem = config.Mode;
        _publicHost.Text = config.PublicHost;
        _gamePort.Value = config.GamePort;
        _updatePort.Value = config.UpdatePort;
        _controlPort.Value = config.ControlPort;
        _ipv6.Checked = config.EnableIpv6;
        _autoStart.Checked = config.StartAutomatically;
        _ddns.Checked = config.DdnsEnabled;
        _ddnsPort.Value = config.DdnsManagementPort;
        _lastMode = config.Mode;
        UpdateDdnsSettingsVisibility();
    }

    private ServerConfiguration ReadConfig()
    {
        ServerConfiguration config = ServerConfiguration.LoadForEditing(_paths);
        config.ServerId = _serverId.Text.Trim();
        config.Mode = Convert.ToString(_mode.SelectedItem) ?? "stable";
        config.PublicHost = _publicHost.Text.Trim();
        config.GamePort = decimal.ToInt32(_gamePort.Value);
        config.UpdatePort = decimal.ToInt32(_updatePort.Value);
        config.ControlPort = decimal.ToInt32(_controlPort.Value);
        config.EnableIpv6 = _ipv6.Checked;
        config.StartAutomatically = _autoStart.Checked;
        config.DdnsEnabled = _ddns.Checked;
        config.DdnsManagementPort = decimal.ToInt32(_ddnsPort.Value);
        config.Validate(throwOnError: true);
        return config;
    }

    private async Task SaveAndRestartAsync()
    {
        ServerConfiguration config = ReadConfig();
        ServerConfiguration previous = ServerConfiguration.LoadForEditing(_paths);
        FileStateSnapshot configBefore = FileStateSnapshot.Capture(_paths.Config);
        ServiceState serviceBefore = await WindowsServiceInstaller.QueryStateAsync();
        bool wasRunning = serviceBefore is not (ServiceState.Missing or ServiceState.Stopped);
        try
        {
            config.Save(_paths);
            await FirewallManager.ApplyAsync(config);
            await WindowsServiceInstaller.SetStartModeAsync(config.StartAutomatically);
            if (wasRunning) await RestartServiceAsync();
        }
        catch (Exception original)
        {
            List<Exception> recoveryErrors = [];
            try { configBefore.Restore(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            try { await FirewallManager.ApplyAsync(previous); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            if (wasRunning)
            {
                try { await WindowsServiceInstaller.StartAsync(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            }
            if (recoveryErrors.Count != 0) throw new AggregateException("保存设置失败且恢复未完全成功。", [original, .. recoveryErrors]);
            throw;
        }
        MessageBox.Show("设置已保存。", "SCBL Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshStatusAsync();
    }

    private async Task ApplyAutoStartAsync()
    {
        ServerConfiguration config = ServerConfiguration.LoadForEditing(_paths);
        config.StartAutomatically = _autoStart.Checked;
        config.Save(_paths);
        await WindowsServiceInstaller.SetStartModeAsync(config.StartAutomatically);
        MessageBox.Show(
            config.StartAutomatically ? "已启用服务端开机自动启动。" : "已关闭服务端开机自动启动；仍可手动启动。",
            "SCBL Server",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task InstallAsync()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "payload", "SCBL-Windows-Server-full.zip");
        string packagePath = bundled;
        if (!File.Exists(packagePath))
        {
            using OpenFileDialog dialog = new() { Filter = "SCBL Windows 完整包 (*.zip)|*.zip", Title = "选择 SCBL Windows 完整包" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            packagePath = dialog.FileName;
        }
        IProgress<string> progress = new Progress<string>(ReportOperation);
        await FirstInstall.InstallAsync(_paths, ReadConfig(), packagePath, CancellationToken.None, progress);
        MessageBox.Show("安装完成，SCBL 服务已启动。", "SCBL Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshStatusAsync();
    }

    private async Task StartOrInstallAsync()
    {
        if (!await WindowsServiceInstaller.ExistsAsync())
        {
            DialogResult confirmation = MessageBox.Show(
                "SCBL 服务端尚未安装。是否使用随包完整包执行首次安装并启动？",
                "首次安装",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirmation != DialogResult.Yes) return;
            await InstallAsync();
            return;
        }
        await WindowsServiceInstaller.StartAsync();
        await RefreshStatusAsync();
    }

    private async Task ApplyUpdateAsync()
    {
        using OpenFileDialog dialog = new() { Filter = "SCBL Windows 更新包 (*.zip)|*.zip", Title = "选择服务端更新 ZIP" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string installedUpdater = Path.Combine(_paths.Bootstrap, "SCBL.Server.Update.exe");
        if (!File.Exists(installedUpdater)) throw new FileNotFoundException("未安装 Windows 更新助手，请先使用完整包修复安装。", installedUpdater);
        CleanupTemporaryUpdaters();
        string temporaryUpdater = Path.Combine(Path.GetTempPath(), $"SCBL.Server.Update.{Guid.NewGuid():N}.exe");
        File.Copy(installedUpdater, temporaryUpdater);
        System.Diagnostics.ProcessStartInfo start = new()
        {
            FileName = temporaryUpdater,
            UseShellExecute = true,
            Verb = "runas",
        };
        start.ArgumentList.Add("--package"); start.ArgumentList.Add(dialog.FileName);
        start.ArgumentList.Add("--program-root"); start.ArgumentList.Add(_paths.ProgramRoot);
        start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(_paths.DataRoot);
        start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        MessageBox.Show("管理界面将关闭并启动更新助手；更新结束后会重新打开管理界面。", "SCBL Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _ = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows 更新助手。");
        BeginInvoke(Close);
    }

    private async Task UninstallAsync()
    {
        if (!await WindowsServiceInstaller.ExistsAsync())
        {
            MessageBox.Show("SCBL 服务端尚未安装。", "卸载服务端", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DialogResult confirmation = MessageBox.Show(
            "将停止并删除 SCBL Windows 服务和防火墙规则。是否继续？",
            "卸载服务端",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes) return;

        DialogResult dataChoice = MessageBox.Show(
            "是否同时删除配置、数据库、日志、缓存和备份？\r\n\r\n“是”=彻底卸载；“否”=保留数据，方便以后重新安装；“取消”=不执行。",
            "选择卸载方式",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);
        if (dataChoice == DialogResult.Cancel) return;

        string installedUpdater = Path.Combine(_paths.Bootstrap, "SCBL.Server.Update.exe");
        if (!File.Exists(installedUpdater)) throw new FileNotFoundException("未安装卸载助手，请先使用完整包修复安装。", installedUpdater);
        CleanupTemporaryUpdaters();
        string temporaryUpdater = Path.Combine(Path.GetTempPath(), $"SCBL.Server.Update.{Guid.NewGuid():N}.exe");
        File.Copy(installedUpdater, temporaryUpdater);
        ProcessStartInfo start = new()
        {
            FileName = temporaryUpdater,
            UseShellExecute = true,
            Verb = "runas",
        };
        start.ArgumentList.Add("--uninstall"); start.ArgumentList.Add(dataChoice == DialogResult.Yes ? "remove-data" : "keep-data");
        start.ArgumentList.Add("--program-root"); start.ArgumentList.Add(_paths.ProgramRoot);
        start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(_paths.DataRoot);
        start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        MessageBox.Show("管理界面将关闭，卸载助手会在服务停止后完成清理。", "卸载服务端", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows 卸载助手。");
        BeginInvoke(Close);
    }

    private async Task RollbackAsync()
    {
        DialogResult confirmation = MessageBox.Show("将停止服务并切回上一个不可变 Runtime，是否继续？", "回滚", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes) return;
        PackageDeployment deployment = new(_paths, new ServerLog(_paths, "manager"));
        ServiceState serviceBefore = await WindowsServiceInstaller.QueryStateAsync();
        bool wasRunning = serviceBefore is not (ServiceState.Missing or ServiceState.Stopped);
        FileStateSnapshot currentBefore = FileStateSnapshot.Capture(_paths.CurrentReleasePointer);
        FileStateSnapshot previousBefore = FileStateSnapshot.Capture(_paths.PreviousReleasePointer);
        if (wasRunning) await WindowsServiceInstaller.StopAsync();
        _ = new BackupManager(_paths, new ServerLog(_paths, "manager")).Create();
        try
        {
            deployment.RollbackRuntime();
            if (wasRunning) await WindowsServiceInstaller.StartAsync();
        }
        catch (Exception original)
        {
            List<Exception> recoveryErrors = [];
            try { currentBefore.Restore(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            try { previousBefore.Restore(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            if (wasRunning)
            {
                try { await WindowsServiceInstaller.StartAsync(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            }
            if (recoveryErrors.Count != 0) throw new AggregateException("回滚切换失败且恢复原运行版本未成功。", [original, .. recoveryErrors]);
            throw;
        }
        await RefreshStatusAsync();
    }

    private async Task UpdateExternalComponentsAsync()
    {
        ServiceState state = await WindowsServiceInstaller.QueryStateAsync();
        bool restart = state is not (ServiceState.Missing or ServiceState.Stopped);
        FileStateSnapshot easyTierBefore = FileStateSnapshot.Capture(Path.Combine(_paths.EasyTier, "current.json"));
        FileStateSnapshot ddnsBefore = FileStateSnapshot.Capture(Path.Combine(_paths.DdnsGo, "current.json"));
        if (restart) await WindowsServiceInstaller.StopAsync();
        try
        {
            ServerConfiguration config = ServerConfiguration.LoadOrCreate(_paths);
            ExternalComponentManager external = new(_paths, new ServerLog(_paths, "manager"));
            IProgress<string> progress = new Progress<string>(ReportOperation);
            await external.EnsureEasyTierAsync(refreshLatest: true, progress: progress);
            if (config.DdnsEnabled) await external.EnsureDdnsGoAsync(refreshLatest: true, progress: progress);
            if (restart) await WindowsServiceInstaller.StartAsync();
        }
        catch (Exception original)
        {
            List<Exception> recoveryErrors = [];
            try { easyTierBefore.Restore(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            try { ddnsBefore.Restore(); } catch (Exception recovery) { recoveryErrors.Add(recovery); }
            if (restart)
            {
                try { await WindowsServiceInstaller.StartAsync(); }
                catch (Exception recovery) { recoveryErrors.Add(recovery); }
            }
            if (recoveryErrors.Count != 0) throw new AggregateException("外部组件更新失败且原服务未能恢复启动。", [original, .. recoveryErrors]);
            throw;
        }
        MessageBox.Show("外部组件检查完成。已有相同版本不会重复下载。", "SCBL Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshStatusAsync();
    }

    private async Task RestoreBackupAsync()
    {
        using OpenFileDialog dialog = new() { Filter = "SCBL 备份 (*.zip)|*.zip", Title = "选择 SCBL 备份", InitialDirectory = _paths.Backups };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        DialogResult confirmation = MessageBox.Show("将停止服务并恢复配置、数据库和运行指针，是否继续？", "恢复备份", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes) return;
        BackupManager backups = new(_paths, new ServerLog(_paths, "manager"));
        ServiceState serviceBefore = await WindowsServiceInstaller.QueryStateAsync();
        bool wasRunning = serviceBefore is not (ServiceState.Missing or ServiceState.Stopped);
        if (wasRunning) await WindowsServiceInstaller.StopAsync();
        string safety = backups.Create();
        try
        {
            backups.Restore(dialog.FileName);
            await FirewallManager.ApplyAsync(ServerConfiguration.LoadOrCreate(_paths));
            if (wasRunning) await WindowsServiceInstaller.StartAsync();
        }
        catch (Exception original)
        {
            try
            {
                backups.Restore(safety);
                await FirewallManager.ApplyAsync(ServerConfiguration.LoadOrCreate(_paths));
                if (wasRunning) await WindowsServiceInstaller.StartAsync();
            }
            catch (Exception recovery) { throw new AggregateException("备份恢复失败且安全快照恢复未成功。", original, recovery); }
            throw;
        }
        await RefreshStatusAsync();
    }

    private static async Task RestartServiceAsync()
    {
        await WindowsServiceInstaller.StopAsync();
        await WindowsServiceInstaller.StartAsync();
    }

    private async Task CreateBackupAndShowAsync()
    {
        ServiceState state = await WindowsServiceInstaller.QueryStateAsync();
        bool restart = state is not (ServiceState.Missing or ServiceState.Stopped);
        if (restart) await WindowsServiceInstaller.StopAsync();
        string? path = null;
        Exception? failure = null;
        try { path = new BackupManager(_paths, new ServerLog(_paths, "manager")).Create(); }
        catch (Exception error) { failure = error; }
        try
        {
            if (restart) await WindowsServiceInstaller.StartAsync();
        }
        catch (Exception recovery)
        {
            if (failure is not null) throw new AggregateException("备份失败且服务未能恢复启动。", failure, recovery);
            throw;
        }
        if (failure is not null) throw failure;
        MessageBox.Show(path ?? throw new InvalidOperationException("备份完成但没有返回文件路径。"), "备份");
    }

    private static void CleanupTemporaryUpdaters()
    {
        foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), "SCBL.Server.Update.*.exe", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5)) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task RefreshStatusAsync()
    {
        bool installed = await WindowsServiceInstaller.ExistsAsync();
        _install.Text = installed ? "使用完整包修复安装" : "首次安装";
        _uninstall.Enabled = installed;
        _start.Text = installed ? "启动" : "首次安装并启动";
        _stop.Enabled = installed;
        _restart.Enabled = installed;
        _save.Text = installed ? "保存设置并重启服务" : "保存设置";
        if (!installed)
        {
            _state.Text = "未安装";
            _details.Text = "请选择完整包完成首次安装。";
            return;
        }
        ServiceState serviceState = await WindowsServiceInstaller.QueryStateAsync();
        if (serviceState != ServiceState.Running)
        {
            _state.Text = serviceState switch
            {
                ServiceState.Stopped => "已停止",
                ServiceState.StartPending => "正在启动",
                ServiceState.StopPending => "正在停止",
                _ => "服务状态异常",
            };
            _details.Text = $"Windows 服务状态：{serviceState}";
            _logs.Clear();
            return;
        }
        try
        {
            PipeResponse response = await PipeClient.SendAsync("status");
            if (!response.Success || response.Status is null) throw new InvalidOperationException(response.Message);
            ServerStatus status = response.Status;
            _state.Text = status.Running ? "运行中" : "已停止";
            _details.Text = $"版本 {status.Version} · {status.Mode} · {status.PublicHost}:{status.GamePort}\r\n" +
                string.Join(" · ", status.Components.Select(component => $"{component.Name}:{(component.Running ? "正常" : "停止")}"));
            PipeResponse logs = await PipeClient.SendAsync("logs", "250");
            _logs.Text = string.Join(Environment.NewLine, logs.Lines ?? []);
            _logs.SelectionStart = _logs.TextLength;
            _logs.ScrollToCaret();
        }
        catch (Exception error)
        {
            _state.Text = "服务未响应";
            _details.Text = error.Message;
        }
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        if (_operationRunning) return;
        _operationRunning = true;
        UseWaitCursor = true;
        _activity.Visible = true;
        try { await action(); }
        catch (Exception error) { MessageBox.Show(error.Message, "SCBL Server", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _activity.Visible = false;
            UseWaitCursor = false;
            _operationRunning = false;
        }
    }

    private void ReportOperation(string message)
    {
        _operation.Text = message;
        _logs.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        _logs.SelectionStart = _logs.TextLength;
        _logs.ScrollToCaret();
    }

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
    private static Button Button(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(4) };
    private static NumericUpDown Port(int value) => new() { Minimum = 1, Maximum = 65535, Value = value, Width = 160 };
    private void UpdateDdnsSettingsVisibility()
    {
        bool visible = _ddns.Checked;
        _ddnsSettings.Visible = visible;
        if (_ddnsSettingsLabel is not null) _ddnsSettingsLabel.Visible = visible;
    }

    private void ApplyModePortOffset()
    {
        string? selected = Convert.ToString(_mode.SelectedItem);
        if (selected is not ("stable" or "test")) return;
        if (_lastMode is null)
        {
            _lastMode = selected;
            return;
        }
        if (string.Equals(_lastMode, selected, StringComparison.Ordinal)) return;
        int offset = selected == "test" ? 1 : -1;
        _gamePort.Value = Math.Clamp(_gamePort.Value + offset, _gamePort.Minimum, _gamePort.Maximum);
        _updatePort.Value = Math.Clamp(_updatePort.Value + offset, _updatePort.Minimum, _updatePort.Maximum);
        _lastMode = selected;
    }

    private static Label AddRow(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label rowLabel = new() { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 9, 8, 9) };
        table.Controls.Add(rowLabel, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 5, 3, 5);
        table.Controls.Add(control, 1, row);
        return rowLabel;
    }
}
