using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class UpdaterProgressWindow : Form
{
    private readonly Label _title;
    private readonly Label _status;
    private readonly Label _elapsed;
    private readonly UpdateActivityBar _progress;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private bool _canClose;

    public UpdaterProgressWindow()
    {
        Name = "UpdaterProgressWindow";
        Text = "SCBL 更新 / Update";
        AccessibleName = "SCBL 更新进度 / SCBL update progress";
        ClientSize = new Size(520, 232);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(78, 190, 120);
        ForeColor = Color.White;
        Padding = new Padding(1);

        var surface = new Panel
        {
            Name = "UpdateSurface",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 14, 18)
        };
        var accent = new Panel
        {
            Name = "UpdateAccent",
            Bounds = new Rectangle(0, 0, 518, 4),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(78, 190, 120)
        };
        var brand = new Label
        {
            Name = "UpdateBrand",
            AutoSize = false,
            Bounds = new Rectangle(32, 24, 454, 20),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 211, 155),
            Text = "SCBL CONNECT  ·  UPDATE"
        };
        _title = new Label
        {
            Name = "UpdateTitle",
            AutoSize = false,
            Bounds = new Rectangle(30, 54, 456, 39),
            Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold),
            ForeColor = Color.FromArgb(242, 246, 249),
            Text = "正在更新 / Updating"
        };
        _status = new Label
        {
            Name = "UpdateStatus",
            AutoSize = false,
            Bounds = new Rectangle(32, 101, 454, 25),
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = Color.FromArgb(178, 190, 199),
            Text = "正在安全安装更新，请勿关闭此窗口"
        };
        _progress = new UpdateActivityBar
        {
            Name = "UpdateActivity",
            Bounds = new Rectangle(32, 139, 454, 8),
            AccessibleName = "更新正在进行 / Update in progress"
        };
        _elapsed = new Label
        {
            Name = "UpdateElapsed",
            AutoSize = false,
            Bounds = new Rectangle(32, 160, 288, 24),
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(117, 132, 143),
            Text = "已用时 0 秒 / 0s elapsed"
        };
        _closeButton = new Button
        {
            Name = "UpdateCloseButton",
            Bounds = new Rectangle(374, 172, 112, 36),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(48, 55, 62),
            Text = "关闭 / Close",
            Visible = false,
            AccessibleName = "关闭更新窗口 / Close update window"
        };
        _closeButton.FlatAppearance.BorderColor = Color.FromArgb(88, 99, 108);
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 70, 78);
        _closeButton.Click += (_, _) =>
        {
            _canClose = true;
            Close();
        };

        surface.Controls.AddRange(new Control[]
        {
            accent,
            brand,
            _title,
            _status,
            _progress,
            _elapsed,
            _closeButton
        });
        Controls.Add(surface);

        _elapsedTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _elapsedTimer.Tick += (_, _) =>
        {
            int seconds = Math.Max(1, (int)(DateTime.UtcNow - _startedAt).TotalSeconds);
            _elapsed.Text = $"已用时 {seconds} 秒 / {seconds}s elapsed";
        };
        _elapsedTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int DropShadow = 0x00020000;
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= DropShadow;
            return parameters;
        }
    }

    public void CompleteAndClose()
    {
        _elapsedTimer.Stop();
        _progress.Stop();
        _canClose = true;
        Close();
    }

    public void ShowFailure()
    {
        _elapsedTimer.Stop();
        BackColor = Color.FromArgb(218, 83, 83);
        if (Controls.Find("UpdateAccent", true).FirstOrDefault() is Panel accent)
            accent.BackColor = Color.FromArgb(218, 83, 83);
        _title.Text = "更新未完成 / Update not completed";
        _status.Text = "原版本已保留或恢复。详细信息已写入更新日志。";
        _elapsed.Text = "请关闭窗口后重新打开 SCBL；如仍失败，请导出诊断。";
        _progress.ShowFailure();
        _closeButton.Visible = true;
        _canClose = true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_canClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _elapsedTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class UpdateActivityBar : Control
{
    private readonly System.Windows.Forms.Timer _animationTimer;
    private int _offset;
    private bool _failed;

    public UpdateActivityBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        TabStop = false;
        _animationTimer = new System.Windows.Forms.Timer { Interval = 28 };
        _animationTimer.Tick += (_, _) =>
        {
            _offset = (_offset + 7) % Math.Max(1, Width + 150);
            Invalidate();
        };
        _animationTimer.Start();
    }

    public void Stop() => _animationTimer.Stop();

    public void ShowFailure()
    {
        _failed = true;
        Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new SolidBrush(Color.FromArgb(35, 43, 50));
        e.Graphics.FillRectangle(track, ClientRectangle);
        if (_failed)
        {
            using var failure = new SolidBrush(Color.FromArgb(218, 83, 83));
            e.Graphics.FillRectangle(failure, ClientRectangle);
            return;
        }

        int segmentWidth = Math.Min(150, Math.Max(72, Width / 3));
        int x = _offset - segmentWidth;
        using var progress = new LinearGradientBrush(
            new Rectangle(x, 0, segmentWidth, Math.Max(1, Height)),
            Color.FromArgb(42, 162, 101),
            Color.FromArgb(119, 224, 158),
            LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(progress, x, 0, segmentWidth, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _animationTimer.Dispose();
        base.Dispose(disposing);
    }
}
