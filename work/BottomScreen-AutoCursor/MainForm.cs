using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace BottomScreen;

internal sealed class Settings
{
    public double PanelInches { get; set; } = 31.5;
    public int TargetInches { get; set; } = 24;
    public bool Nearest { get; set; }
    public bool CenterFourThree { get; set; }
    public bool ShowFps { get; set; } = true;
    public bool AllowAutoHdr { get; set; }
    public Dictionary<string, long> GameHdrRestartAfterUtc { get; set; } = new();
    public string Monitor { get; set; } = "";

    internal static Settings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
            settings.GameHdrRestartAfterUtc ??= new();
            return settings;
        }
        catch { return new(); }
    }
}

internal sealed class MainForm : Form
{
    private readonly ComboBox windows = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox screens = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox sourceMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown diagonal = new() { Minimum = 27, Maximum = 100, DecimalPlaces = 1, Increment = 0.5m, Value = 31.5m, Width = 100 };
    private readonly RadioButton size24 = new() { Text = "24 寸", Checked = true, AutoSize = true };
    private readonly RadioButton size27 = new() { Text = "27 寸", AutoSize = true };
    private readonly CheckBox nearest = new() { Text = "锐利像素（最近邻）；关闭时使用平滑缩放", AutoSize = true };
    private readonly CheckBox showFps = new() { Text = "顶部黑边显示提交帧率", AutoSize = true, Checked = true };
    private readonly CheckBox allowAutoHdr = new() { Text = "允许所选游戏使用自动 HDR", AutoSize = true };
    private readonly Label hdrInfo = new() { AutoSize = true, ForeColor = Color.FromArgb(93, 95, 96), MaximumSize = new Size(770, 0) };
    private readonly Button applyHdr = new() { Text = "应用设置", Height = 34 };
    private readonly Func<WindowChoice, SourceProcess> resolveSource;
    private string? hdrStatus;
    private bool hdrError;
    private readonly Button start = new() { Text = "开始显示", AutoSize = false, Width = 130, Height = 40, BackColor = Color.FromArgb(25, 27, 29), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button stop = new() { Text = "停止", Width = 90, Height = 40, Enabled = false };
    private readonly Label outputInfo = new() { AutoSize = true, ForeColor = Color.FromArgb(75, 77, 80) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(770, 0), Text = "就绪 · 支持窗口 / 全屏窗口化，请选择游戏窗口。" };
    private readonly Label telemetry = new() { AutoSize = true, ForeColor = Color.FromArgb(75, 77, 80), Text = "源画面：—    捕获帧率：—" };
    private readonly ScreenPreview preview = new() { Dock = DockStyle.Fill, Height = 190 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly Stopwatch stats = new();
    private CaptureEngine? engine;
    private OverlayForm? overlay;
    private SystemCursorGuard? cursorGuard;
    private nint source;
    private long lastFrames;
    private long lastFrameProgress;
    private long previousFrames;
    private bool restoring;
    private bool hotkey;
    private Screen[] availableScreens = Array.Empty<Screen>();
    private Settings settings = new();
    private readonly string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public MainForm(Func<WindowChoice, SourceProcess>? resolveSource = null)
    {
        this.resolveSource = resolveSource ?? (choice => SourceProcess.ForWindow(choice.Handle));
        Text = "BottomScreen";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = Color.FromArgb(249, 251, 252);
        ForeColor = Color.FromArgb(19, 20, 22);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, Math.Min(1000, (Screen.PrimaryScreen?.WorkingArea.Height ?? 1080) - 80));
        MinimumSize = new Size(760, 700);
        StartPosition = FormStartPosition.CenterScreen;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 18, 28, 18), ColumnCount = 1, RowCount = 13, AutoScroll = true };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label { Text = "BottomScreen", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        content.Controls.Add(new Label { Text = "完整游戏画面，放进底部居中的小屏区域。", AutoSize = true, ForeColor = Color.FromArgb(93, 95, 96), Margin = new Padding(0, 0, 0, 18) });
        var form = new CenteredRowsPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, BackColor = Color.White, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 16) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        var refresh = new Button { Text = "刷新", Height = 34 };
        refresh.Click += (_, _) => RefreshWindows();
        AddRow(form, 0, "游戏窗口", windows, refresh);
        AddRow(form, 1, "输出显示器", screens);
        sourceMode.Items.AddRange(new object[] { "完整画面（普通窗口 / 全屏窗口已拉伸）", "居中4:3去黑边（仅用于左右有黑边的画面）" });
        sourceMode.SelectedIndex = 0;
        AddRow(form, 2, "画面来源", sourceMode);
        var dims = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1 };
        diagonal.Anchor = AnchorStyles.Left;
        diagonal.Margin = new Padding(0, 0, 8, 0);
        dims.Controls.Add(diagonal, 0, 0);
        dims.Controls.Add(new Label { Text = "寸（实体面板对角线）", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 1, 0);
        AddRow(form, 3, "屏幕尺寸", dims);
        var sizes = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        size24.Margin = new Padding(0, 0, 30, 0); size27.Margin = Padding.Empty;
        sizes.Controls.AddRange(new Control[] { size24, size27 });
        AddRow(form, 4, "模拟尺寸", sizes);
        AddRow(form, 5, "自动 HDR", allowAutoHdr, applyHdr);
        content.Controls.Add(form);
        content.Controls.Add(new Label { Text = "显示区域预览", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) });
        content.Controls.Add(preview);
        content.Controls.Add(outputInfo);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 16, 0, 8) };
        nearest.Margin = new Padding(0, 0, 18, 0); showFps.Margin = new Padding(0);
        options.Controls.AddRange(new Control[] { nearest, showFps }); content.Controls.Add(options);
        hdrInfo.Margin = new Padding(0, 0, 0, 4); content.Controls.Add(hdrInfo);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 10) };
        start.Margin = new Padding(0, 0, 12, 0); start.FlatAppearance.BorderSize = 0;
        actions.Controls.AddRange(new Control[] { start, stop });
        if (Program.PerformanceLoggingEnabled)
        {
            var demo = new Button { Text = "打开测试画面", Width = 145, Height = 40 };
            demo.Click += (_, _) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Arguments = "--demo" });
            var logs = new Button { Text = "打开性能日志", Width = 145, Height = 40 };
            logs.Click += (_, _) =>
            {
                try { Directory.CreateDirectory(PerformanceLog.DirectoryPath); Process.Start(new ProcessStartInfo(PerformanceLog.DirectoryPath) { UseShellExecute = true }); }
                catch (Exception e) { status.Text = "无法打开日志目录：" + e.Message; }
            };
            actions.Controls.AddRange(new Control[] { demo, logs });
        }
        content.Controls.Add(actions);
        status.Margin = new Padding(0, 0, 0, 6); content.Controls.Add(status);
        content.Controls.Add(telemetry);
        content.Controls.Add(new Label
        {
            Text = "开始后自动保留小屏内真实光标，临时隐藏原位置的系统光标。\n光标样式随原窗口变化，无需鼠标5切换；点击和游戏输入保持原样。\n切出游戏、移出源区域或停止时恢复系统光标；游戏自行隐藏光标时不强制显示。\nCtrl + Alt + F10：停止并返回设置。支持窗口 / 全屏窗口化。\n左右有黑边才选“居中4:3去黑边”；正常16:9画面请选“完整画面”。",
            AutoSize = true, MaximumSize = new Size(770, 0), ForeColor = Color.FromArgb(93, 95, 96), Margin = new Padding(0, 14, 0, 0)
        });
        Controls.Add(content);
        LoadSettings();
        UpdateHdrInfo();
        RefreshScreens(); RefreshWindows();
        screens.SelectedIndexChanged += (_, _) => UpdatePreview();
        diagonal.ValueChanged += (_, _) => UpdatePreview();
        size24.CheckedChanged += (_, _) => UpdatePreview();
        start.Click += (_, _) => StartCapture(); stop.Click += (_, _) => StopCapture("已停止。");
        allowAutoHdr.CheckedChanged += (_, _) => ChangeHdrPreference();
        applyHdr.Click += (_, _) => ApplyGameHdr();
        windows.SelectedIndexChanged += (_, _) => { hdrStatus = null; hdrError = false; UpdateHdrInfo(); };
        timer.Tick += (_, _) => Watch(); timer.Start();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        FormClosing += (_, _) => { StopCapture("", false); SaveSettings(); SystemEvents.DisplaySettingsChanged -= DisplayChanged; timer.Dispose(); Native.UnregisterHotKey(Handle, 1); };
        Shown += (_, _) =>
        {
            form.PerformLayout();
            hotkey = Native.RegisterHotKey(Handle, 1, 0x4003, 0x79);
            SetRunning(false);
            if (!hotkey) status.Text = "停止快捷键被其他程序占用。请关闭占用 Ctrl+Alt+F10 的程序后重新打开。";
        };
    }

    private void AddRow(TableLayoutPanel panel, int row, string label, Control control, Control? action = null)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 8, 10) }, 0, row);
        control.Dock = DockStyle.None;
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 0, 8, 10);
        panel.Controls.Add(control, 1, row);
        if (action != null)
        {
            action.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            action.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(action, 2, row);
        }
        else panel.SetColumnSpan(control, 2);
    }

    private void RefreshWindows()
    {
        nint selected = (windows.SelectedItem as WindowChoice)?.Handle ?? 0;
        windows.Items.Clear();
        foreach (var window in Native.Windows()) windows.Items.Add(window);
        int index = windows.Items.Cast<WindowChoice>().ToList().FindIndex(x => x.Handle == selected);
        if (windows.Items.Count > 0) windows.SelectedIndex = index >= 0 ? index : 0;
    }

    private void RefreshScreens()
    {
        string selected = screens.SelectedIndex >= 0 && screens.SelectedIndex < availableScreens.Length ? availableScreens[screens.SelectedIndex].DeviceName : settings.Monitor;
        availableScreens = Screen.AllScreens;
        screens.Items.Clear();
        foreach (var screen in availableScreens)
            screens.Items.Add($"{screen.DeviceName}  ·  {screen.Bounds.Width}×{screen.Bounds.Height}  ·  {Native.RefreshRate(screen)} Hz{(screen.Primary ? "  主屏" : "")}");
        int index = Array.FindIndex(availableScreens, x => x.DeviceName == selected);
        if (availableScreens.Length > 0) screens.SelectedIndex = index >= 0 ? index : Array.FindIndex(availableScreens, x => x.Primary);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (screens.SelectedIndex < 0 || screens.SelectedIndex >= availableScreens.Length) return;
        var screen = availableScreens[screens.SelectedIndex];
        try
        {
            var rect = Geometry.Target(screen.Bounds.Size, (double)diagonal.Value, size24.Checked ? 24 : 27);
            preview.Ratio = (double)diagonal.Value; preview.Inches = size24.Checked ? 24 : 27; preview.Invalidate();
            outputInfo.Text = $"输出 {screen.Bounds.Width}×{screen.Bounds.Height} · 区域 {rect.Width}×{rect.Height} · 横向居中 / 底部对齐";
        }
        catch (Exception e) { outputInfo.Text = e.Message; }
    }

    private void StartCapture()
    {
        if (engine != null || !hotkey) return;
        if (windows.SelectedItem is not WindowChoice choice) { status.Text = "没有可捕获窗口。请先打开游戏，然后点击刷新。"; return; }
        if (!Native.IsWindow(choice.Handle) || Native.IsIconic(choice.Handle)) { status.Text = "窗口已关闭或最小化。请先还原游戏窗口，再刷新列表。"; return; }
        if (screens.SelectedIndex < 0) return;
        if (!ApplyGameHdr()) return;
        var screen = availableScreens[screens.SelectedIndex];
        try
        {
            var rect = Geometry.Target(screen.Bounds.Size, (double)diagonal.Value, size24.Checked ? 24 : 27);
            overlay = new OverlayForm(screen.Bounds);
            _ = overlay.Handle;
            source = choice.Handle;
            engine = new CaptureEngine(source, overlay.Handle, screen.Bounds.Size, rect, nearest.Checked,
                sourceMode.SelectedIndex == 1, Native.RefreshRate(screen), showFps.Checked);
            engine.Failed += message => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => StopCapture(message)); };
            cursorGuard = new SystemCursorGuard();
            lastFrames = previousFrames = 0; lastFrameProgress = Environment.TickCount64;
            stats.Restart();
            SetRunning(true); SaveSettings();
            overlay.Show(); engine.Start();
            WindowState = FormWindowState.Minimized;
            Native.SetForegroundWindow(source);
            status.Text = Program.PerformanceLoggingEnabled ? "显示中 · 正在后台记录性能日志 · 按 Ctrl+Alt+F10 停止。" : "显示中 · 按 Ctrl+Alt+F10 停止。";
        }
        catch (Exception e) { Program.Log(e.ToString()); StopCapture("启动失败：" + e.Message); }
    }

    private void SetRunning(bool running)
    {
        start.Enabled = !running && hotkey; stop.Enabled = running;
        start.BackColor = start.Enabled ? Color.FromArgb(25, 27, 29) : Color.FromArgb(226, 229, 231);
        start.ForeColor = start.Enabled ? Color.White : Color.FromArgb(75, 77, 80);
        windows.Enabled = screens.Enabled = sourceMode.Enabled = diagonal.Enabled = size24.Enabled = size27.Enabled = nearest.Enabled = showFps.Enabled = allowAutoHdr.Enabled = !running;
        applyHdr.Enabled = !running;
    }

    private void ChangeHdrPreference()
    {
        hdrError = !SaveSettings();
        hdrStatus = hdrError ? "无法保存设置，请将程序放在可写目录后重试。" : null;
        UpdateHdrInfo();
    }

    private void UpdateHdrInfo()
    {
        hdrInfo.ForeColor = hdrError ? Color.FromArgb(170, 40, 40) : Color.FromArgb(93, 95, 96);
        hdrInfo.Text = hdrStatus ?? "自动 HDR：作用于所选游戏；点击“应用设置”后如有更改，请重启游戏。";
    }

    private bool ApplyGameHdr()
    {
        if (engine != null) return false;
        try
        {
            if (windows.SelectedItem is not WindowChoice choice)
                throw new InvalidOperationException("请先选择被捕获的游戏窗口。");
            var process = resolveSource(choice);
            if (!SaveSettings()) throw new IOException("无法保存设置，尚未修改游戏的自动 HDR 配置。");
            var preference = GameHdrController.Apply(process, allowAutoHdr.Checked, settings);
            if (preference.Error != null) throw new IOException(preference.Error);
            if (!SaveSettings()) throw new IOException("游戏配置已写入，但无法保存重启标记；请手动重启游戏。");
            bool needsRestart = GameHdrController.NeedsRestart(process, settings);
            string state = allowAutoHdr.Checked ? "跟随 Windows 全局设置" : "已禁用";
            hdrStatus = $"{Path.GetFileName(process.Executable)}：自动 HDR {state}。" +
                (needsRestart ? "请手动重启游戏，然后刷新窗口列表。" : "配置已匹配。");
            hdrError = false; UpdateHdrInfo();
            if (needsRestart) status.Text = hdrStatus;
            return !needsRestart;
        }
        catch (Exception e)
        {
            hdrStatus = e.Message; hdrError = true; UpdateHdrInfo();
            status.Text = "无法应用游戏自动 HDR 设置：" + e.Message;
            return false;
        }
    }

    private void StopCapture(string message, bool restore = true)
    {
        if (restoring) return;
        restoring = true;
        bool wasRunning = engine != null || overlay != null;
        try
        {
            cursorGuard?.Dispose(); cursorGuard = null;
            overlay?.Hide();
            var previous = engine; engine = null;
            try { if (previous != null) { previous.StopReason = string.IsNullOrEmpty(message) ? "window closed" : message; previous.Dispose(); } } catch (Exception e) { Program.Log(e.ToString()); }
            overlay?.Dispose(); overlay = null;
            stats.Reset(); SetRunning(false);
            if (message.Length > 0) status.Text = message;
            if (previous?.PerformanceLogError is string logError) status.Text += " 性能日志写入失败：" + logError;
            else if (previous?.PerformanceLogPath != null) status.Text += " 性能日志已保存，可点击“打开性能日志”。";
            if (restore && wasRunning) { WindowState = FormWindowState.Normal; Show(); Activate(); }
        }
        finally { restoring = false; }
    }

    private void Watch()
    {
        if (engine == null || overlay == null) return;
        if (!Native.IsWindow(source)) { StopCapture("游戏窗口已关闭。"); return; }
        if (Native.IsIconic(source)) { StopCapture("游戏已最小化，显示已停止。"); return; }
        bool foreground = Native.GetForegroundWindow() == source;
        if (foreground != overlay.Visible) { if (foreground) overlay.Show(); else overlay.Hide(); }
        long count = engine.FrameCount;
        var cursorState = engine.CursorState;
        try { cursorGuard?.Pulse(foreground && count > 0 && cursorState.Rendered ? source : 0, cursorState.Source); }
        catch (Exception e) { StopCapture(e.Message); return; }
        if (count != previousFrames) { previousFrames = count; lastFrameProgress = Environment.TickCount64; }
        if (foreground && count == 0 && Environment.TickCount64 - lastFrameProgress > 8000)
        {
            StopCapture("连续8秒没有捕获到新画面。请确认使用窗口或全屏窗口化、检查捕获权限，或用测试画面验证。"); return;
        }
        if (stats.ElapsedMilliseconds < 1000) return;
        double fps = (count - lastFrames) * 1000.0 / stats.ElapsedMilliseconds;
        var size = engine.SourceSize;
        var client = engine.ClientSize;
        telemetry.Text = $"源区域：{size.Width}×{size.Height} / 窗口：{client.Width}×{client.Height}    捕获：{fps:F0} 帧/秒（非游戏FPS）";
        lastFrames = count; stats.Restart();
    }

    private void DisplayChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            if (engine != null) StopCapture("显示模式已变化，已安全停止。请确认输出模式后重新开始。");
            RefreshScreens();
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == 1) { StopCapture("已通过快捷键停止。"); return; }
        base.WndProc(ref m);
    }

    private void LoadSettings()
    {
        settings = Settings.Load(settingsPath);
        diagonal.Value = Math.Clamp((decimal)settings.PanelInches, diagonal.Minimum, diagonal.Maximum);
        size24.Checked = settings.TargetInches != 27; size27.Checked = settings.TargetInches == 27;
        nearest.Checked = settings.Nearest;
        sourceMode.SelectedIndex = settings.CenterFourThree ? 1 : 0;
        showFps.Checked = settings.ShowFps;
        allowAutoHdr.Checked = settings.AllowAutoHdr;
    }
    private bool SaveSettings()
    {
        settings.PanelInches = (double)diagonal.Value; settings.TargetInches = size24.Checked ? 24 : 27;
        settings.Nearest = nearest.Checked;
        settings.CenterFourThree = sourceMode.SelectedIndex == 1;
        settings.ShowFps = showFps.Checked;
        settings.AllowAutoHdr = allowAutoHdr.Checked;
        if (screens.SelectedIndex >= 0) settings.Monitor = availableScreens[screens.SelectedIndex].DeviceName;
        try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); return true; }
        catch { return false; }
    }
}

internal sealed class CenteredRowsPanel : TableLayoutPanel
{
    private bool aligning;
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (aligning) return;
        aligning = true;
        try
        {
            base.OnLayout(e);
            // Center actual native control bounds AFTER the table layout engine.
            // ComboBox HWND creation can change its height at high DPI.
            var heights = GetRowHeights();
            int top = Padding.Top;
            for (int row = 0; row < heights.Length; row++)
            {
                foreach (Control child in Controls)
                {
                    if (GetRow(child) != row) continue;
                    int available = heights[row] - child.Margin.Vertical;
                    int y = top + child.Margin.Top + (available - child.Height) / 2;
                    if (child.Top != y) child.Top = y;
                }
                top += heights[row];
            }
        }
        finally { aligning = false; }
    }
}

internal sealed class ScreenPreview : Control
{
    public double Ratio { get; set; } = 31.5;
    public int Inches { get; set; } = 24;
    public ScreenPreview() { DoubleBuffered = true; BackColor = Color.White; MinimumSize = new Size(300, 180); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int h = Math.Min(Height - 14, 180); int w = h * 16 / 9;
        var panel = new Rectangle((Width - w) / 2, 4, w, h);
        e.Graphics.FillRectangle(Brushes.Black, panel);
        var target = Geometry.Target(panel.Size, Ratio, Inches); target.Offset(panel.Location);
        using var fill = new SolidBrush(Color.FromArgb(27, 108, 128));
        e.Graphics.FillRectangle(fill, target);
        TextRenderer.DrawText(e.Graphics, $"{Inches} 寸 · 16:9\n4:3 画面拉伸铺满", Font, target, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
