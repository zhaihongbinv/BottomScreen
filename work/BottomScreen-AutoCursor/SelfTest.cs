using System.Drawing.Imaging;
using System.Text.Json;
using System.Diagnostics;

namespace BottomScreen;

internal sealed class PatternForm : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private int ticks;
    public bool Pillarboxed { get; set; }
    public PatternForm()
    {
        Text = "BottomScreen 测试画面 · 4:3";
        ClientSize = new Size(640, 480);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 50);
        DoubleBuffered = true;
        timer.Tick += (_, _) => { ticks++; Invalidate(); };
        timer.Start(); FormClosed += (_, _) => timer.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var area = Geometry.SourceCrop(new Rectangle(Point.Empty, ClientSize), Pillarboxed);
        g.TranslateTransform(area.X, area.Y);
        int w = area.Width, h = area.Height;
        g.FillRectangle(Brushes.Red, 0, 0, w / 2, h / 2);
        g.FillRectangle(Brushes.Lime, w / 2, 0, w - w / 2, h / 2);
        g.FillRectangle(Brushes.Blue, 0, h / 2, w / 2, h - h / 2);
        g.FillRectangle(Brushes.White, w / 2, h / 2, w - w / 2, h - h / 2);
        using var pen = new Pen(Color.Black, 3);
        g.DrawEllipse(pen, w / 2 - 75, h / 2 - 75, 150, 150);
        g.DrawLine(pen, 0, h / 2, w, h / 2); g.DrawLine(pen, w / 2, 0, w / 2, h);
        g.FillRectangle(Brushes.Black, 8, 8, 250, 44);
        using var font = new Font("Microsoft YaHei UI", 12);
        TextRenderer.DrawText(g, $"{w}×{h} · 帧 {ticks}", font, new Point(16, 16), Color.White);
        g.FillRectangle(Brushes.Black, (ticks * 5) % Math.Max(1, w - 20), h / 2 - 5, 20, 10);
    }
}

internal sealed class SelfTestForm : Form
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendDpiMessage(nint window, uint message, nint dpi, ref Native.Rect suggestedBounds);
    private readonly string directory;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly List<string> checks = new();
    private readonly DateTime start = DateTime.UtcNow;
    private PatternForm? pattern;
    private OverlayForm? output;
    private CaptureEngine? capture;
    private int stage;
    private long stageFrames;
    private Rectangle target;
    private bool complete;
    private int initialPerformanceFileCount;
    public SelfTestForm(string[] args)
    {
        int index = Array.IndexOf(args, "--report-dir");
        directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(AppContext.BaseDirectory, "self-test");
        Directory.CreateDirectory(directory);
        Text = "BottomScreen 自测"; ClientSize = new Size(450, 90);
        Controls.Add(new Label { Text = "正在验证窗口捕获、GPU缩放和区域位置…\n测试结束后窗口会自动关闭。", Dock = DockStyle.Fill, Padding = new Padding(14), AutoSize = false });
        Shown += (_, _) => { try { Begin(); } catch (Exception e) { Finish(e); } };
        timer.Tick += (_, _) => { try { Tick(); } catch (Exception e) { Finish(e); } };
    }
    private void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        checks.Add("PASS: " + description);
    }
    private void Begin()
    {
        Check(!new Settings().AllowAutoHdr, "Auto HDR defaults off, including existing settings without the new property");
        var process = SourceProcess.ForWindow(Handle);
        Check(process.Id == Environment.ProcessId && Path.GetFullPath(process.Executable).Equals(Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)
            && process.StartedFileTimeUtc > 0, "selected HWND resolves its actual executable path and process creation time with limited query access");
        Check(AutoHdrPreference.Merge(null, false) == "AutoHDREnable=48;", "Auto HDR off uses the preference observed in Windows Settings");
        const string otherPreferences = "GpuPreference=2;SwapEffectUpgradeEnable=1;UnknownFutureFlag=hello;";
        Check(AutoHdrPreference.Merge(otherPreferences + "AutoHDREnable=1;", false) == otherPreferences + "AutoHDREnable=48;",
            "disabling Auto HDR preserves GPU selection, windowed optimization and unknown flags");
        Check(AutoHdrPreference.Merge(otherPreferences + "AutoHDREnable=48;", true) == otherPreferences,
            "allowing Auto HDR removes only this app's override and inherits global policy");
        Check(AutoHdrPreference.Merge("AutoHDREnable=48;", true) == "", "allowing HDR removes an otherwise empty preference");
        Check(AutoHdrPreference.Merge("autohdrenable=1;AutoHDREnable=48;", false) == "AutoHDREnable=48;", "HDR updates remove duplicate case-insensitive overrides");
        string fixture = Path.Combine(directory, "hdr-preference-fixture.exe");
        using (var preferences = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutoHdrPreference.KeyPath, true))
        {
            object? original = preferences.GetValue(fixture);
            object? global = preferences.GetValue("DirectXUserGlobalSettings");
            try
            {
                preferences.SetValue(fixture, otherPreferences);
                var policy = new AutoHdrPreference(false, fixture);
                Check(policy.Error == null && policy.Changed && (string?)preferences.GetValue(fixture) == otherPreferences + "AutoHDREnable=48;",
                    "real per-executable HDR preference writes and reads back correctly");
                Check(policy.Apply(false) && !policy.Changed, "unchanged HDR policy does not rewrite or cause a startup restart loop");
                Check(policy.Apply(true) && policy.Changed && (string?)preferences.GetValue(fixture) == otherPreferences,
                    "real allow setting restores global inheritance without erasing other flags");
                Check(Equals(global, preferences.GetValue("DirectXUserGlobalSettings")), "HDR policy leaves the global Windows preference untouched");
                object? utility = preferences.GetValue(Environment.ProcessPath!);
                var gameSettings = new Settings();
                var runningGame = new SourceProcess(1234, fixture, DateTime.UtcNow.AddMinutes(-5).ToFileTimeUtc());
                var gamePreference = GameHdrController.Apply(runningGame, false, gameSettings);
                Check(gamePreference.Error == null && gamePreference.Changed && GameHdrController.NeedsRestart(runningGame, gameSettings),
                    "changing the captured game's HDR policy marks that running game as needing restart");
                string savedGameSettings = Path.Combine(directory, "game-hdr-settings.json");
                File.WriteAllText(savedGameSettings, JsonSerializer.Serialize(gameSettings));
                var loadedGameSettings = Settings.Load(savedGameSettings);
                Check(GameHdrController.NeedsRestart(runningGame, loadedGameSettings), "restarting only the utility cannot clear a game's persisted HDR restart requirement");
                var laterGame = runningGame with { Id = 4321, StartedFileTimeUtc = DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc() };
                var laterPreference = GameHdrController.Apply(laterGame, false, loadedGameSettings);
                Check(laterPreference.Error == null && !laterPreference.Changed && !GameHdrController.NeedsRestart(laterGame, loadedGameSettings),
                    "a genuinely restarted game with matching HDR preference is accepted without further changes");
                Check(Equals(utility, preferences.GetValue(Environment.ProcessPath!)) && Equals(global, preferences.GetValue("DirectXUserGlobalSettings")),
                    "applying captured-game HDR leaves the utility's own and global graphics settings untouched");
            }
            finally
            {
                if (original == null) preferences.DeleteValue(fixture, false);
                else preferences.SetValue(fixture, original);
            }
        }
        foreach (var size in new[] { new Size(3840, 2160), new Size(1920, 1080) })
        foreach (int inches in new[] { 24, 27 })
        {
            var r = Geometry.Target(size, 31.5, inches);
            Check(r.Bottom == size.Height && Math.Abs((size.Width - r.Width) - 2 * r.X) <= 1,
                $"{size.Width}×{size.Height} / {inches} inch: centered, bottom aligned, {r.Width}×{r.Height}");
            Check(Math.Abs((double)r.Width / r.Height - 16.0 / 9) < .002, "16:9 target aspect");
        }
        bool invalidRejected = false;
        try { Geometry.Target(new Size(1920, 1080), 23, 27); } catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        Check(invalidRejected, "invalid physical size rejected");
        Check(Geometry.SourceCrop(new Rectangle(100, 50, 1920, 1080), true) == new Rectangle(340, 50, 1440, 1080),
            "centered 4:3 source selection preserves client offset");
        Check(Geometry.SourceCrop(new Rectangle(0, 0, 1280, 960), true) == new Rectangle(0, 0, 1280, 960),
            "4:3 source is not cropped again");
        Check(Geometry.SourceCrop(new Rectangle(0, 0, 1920, 1080), false) == new Rectangle(0, 0, 1920, 1080),
            "full-image mode preserves all 16:9 content");
        var meter = new SubmissionRateMeter();
        Check(meter.Observe(0, 0) && meter.FramesPerSecond == null, "FPS meter starts with no invented rate");
        Check(meter.Observe(120, Stopwatch.Frequency) && meter.FramesPerSecond == 120, "FPS uses submitted count over elapsed time");
        Check(!meter.Observe(140, Stopwatch.Frequency + Stopwatch.Frequency / 2) && meter.FramesPerSecond == 120,
            "FPS display does not resample on every game frame");
        Check(meter.Observe(180, 2 * Stopwatch.Frequency) && meter.FramesPerSecond == 60, "FPS updates to actual changed rate");
        Check(meter.Observe(480, 3 * Stopwatch.Frequency) && meter.FramesPerSecond == 300, "FPS is not fabricated or clamped to monitor Hz");
        Check(meter.Observe(480, 4 * Stopwatch.Frequency) && meter.FramesPerSecond == 0,
            "no new source frames reports zero FPS instead of counting cached redraws");
        using (var summary = JsonDocument.Parse(JsonSerializer.Serialize(PerformanceLog.Summarize(new double[] { 1, 2, 3, 4, 100 }))))
        {
            Check(summary.RootElement.GetProperty("avg").GetDouble() == 22, "performance average includes spikes");
            Check(summary.RootElement.GetProperty("p95").GetDouble() == 100, "performance p95 exposes stalls");
        }
        var log = new PerformanceLog(new { test = true }, Path.Combine(directory, "diagnostic-test"));
        for (int i = 0; i < 3; i++)
            log.Record(new FrameTiming(Stopwatch.GetTimestamp(), 0, .1, .2, 3, i == 0 ? 0 : 100000,
                true, true, false, new Size(640, 480), new Rectangle(0, 0, 640, 480), new DrawTiming(0, .1, .2, 0, 2)));
        log.Record(new FrameTiming(Stopwatch.GetTimestamp(), 0, 0, 0, .01, null, false, false, false, Size.Empty, Rectangle.Empty, default));
        log.Complete("diagnostic self-test"); log.Dispose();
        Check(log.Error == null && File.Exists(log.FilePath), "background performance log is written and drained on stop");
        var rows = File.ReadAllLines(log.FilePath).Select(x => JsonDocument.Parse(x)).ToArray();
        try
        {
            Check(rows[0].RootElement.GetProperty("type").GetString() == "session", "performance log includes session metadata");
            var interval = rows.Single(x => x.RootElement.GetProperty("type").GetString() == "interval").RootElement;
            Check(interval.GetProperty("submitted").GetInt32() == 3 && interval.GetProperty("empty").GetInt32() == 1,
                "diagnostics distinguish successful submits from empty callbacks");
            Check(interval.GetProperty("presentMs").GetProperty("avg").GetDouble() == 2, "Present CPU duration is aggregated separately");
            Check(interval.GetProperty("sourceRepeated").GetInt32() == 1 &&
                interval.GetProperty("sourceTimestampGapMs").GetProperty("avg").GetDouble() == 10, "source timestamps distinguish gaps and repeats without claiming latency");
            Check(rows[^1].RootElement.GetProperty("totalSubmitted").GetInt64() == 3 &&
                rows[^1].RootElement.GetProperty("reason").GetString() == "diagnostic self-test", "final log preserves frame totals and stop reason");
        }
        finally { foreach (var row in rows) row.Dispose(); }
        string uiFixture = Path.Combine(directory, "hdr-ui-fixture.exe");
        bool simulateHdrReadFailure = false;
        long fixtureStarted = DateTime.UtcNow.AddMinutes(-5).ToFileTimeUtc();
        int hdrQueryCount = 0;
        try
        {
        using (var main = new MainForm(_ =>
        {
            hdrQueryCount++;
            return simulateHdrReadFailure
                ? throw new InvalidOperationException("fixture process query denied")
                : new SourceProcess(5678, uiFixture, fixtureStarted);
        }))
        {
            main.Show(); Application.DoEvents(); main.PerformLayout();
            using (var initialLayout = new Bitmap(main.Width, main.Height))
            {
                main.DrawToBitmap(initialLayout, new Rectangle(Point.Empty, initialLayout.Size));
                initialLayout.Save(Path.Combine(directory, "settings.png"), ImageFormat.Png);
            }
            if (!Program.PerformanceLoggingEnabled)
            {
                static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
                var buttonLabels = Descendants(main).OfType<Button>().Select(x => x.Text).ToArray();
                Check(main.Text == "BottomScreen", "window title contains only the product name");
                Check(Descendants(main).OfType<CheckBox>().Any(x => x.Text == "允许所选游戏使用自动 HDR"), "HDR checkbox explicitly targets the selected captured game");
                var form = Descendants(main).OfType<TableLayoutPanel>().Single(x => x.ColumnCount == 3);
                void CheckAlignment(string scale)
                {
                    for (int row = 0; row < 6; row++)
                    {
                        var label = form.GetControlFromPosition(0, row)!;
                        var value = form.GetControlFromPosition(1, row)!;
                        Check(Math.Abs(label.Top * 2 + label.Height - value.Top * 2 - value.Height) <= 2,
                            $"{scale}: row {row} label and input share the vertical center ({label.Bounds}; {value.Bounds}; DPI {main.DeviceDpi})");
                    }
                    var input = form.GetControlFromPosition(1, 0)!;
                    var action = form.GetControlFromPosition(2, 0)!;
                    Check(Math.Abs(input.Top * 2 + input.Height - action.Top * 2 - action.Height) <= 2,
                        $"{scale}: window selector and refresh button share the vertical center");
                }
                CheckAlignment("native DPI");
                int originalDpi = main.DeviceDpi;
                var originalBounds = main.Bounds;
                void CheckTextFits(int dpi)
                {
                    Check(main.ClientSize.Width >= 850 * dpi / 96,
                        $"{dpi} DPI: initial logical window width is not prematurely scaled down");
                    foreach (var button in Descendants(main).OfType<Button>())
                    {
                        var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine);
                        Check(button.ClientSize.Width >= text.Width + 8 * dpi / 96 && button.ClientSize.Height >= text.Height + 6 * dpi / 96,
                            $"{dpi} DPI: {button.Text} has room for its text and padding ({button.Size}; text {text})");
                    }
                    for (int row = 0; row < 6; row++)
                    {
                        var label = form.GetControlFromPosition(0, row)!;
                        var text = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine);
                        Check(label.Width >= text.Width && label.Height <= text.Height + 2,
                            $"{dpi} DPI: {label.Text} stays on one line");
                    }
                }
                CheckTextFits(originalDpi);
                foreach (int dpi in new[] { 144, 96, 192, 96, originalDpi })
                {
                    double factor = (double)dpi / main.DeviceDpi;
                    var bounds = new Native.Rect { Left = main.Left, Top = main.Top,
                        Right = main.Left + (int)Math.Round(main.Width * factor),
                        Bottom = main.Top + (int)Math.Round(main.Height * factor) };
                    SendDpiMessage(main.Handle, 0x02E0, (nint)(dpi | dpi << 16), ref bounds);
                    main.PerformLayout(); form.PerformLayout();
                    Check(main.DeviceDpi == dpi, $"owned settings window handles WM_DPICHANGED to {dpi} DPI");
                    CheckTextFits(dpi);
                    CheckAlignment($"after DPI change to {dpi}");
                    using var scaledLayout = new Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(scaledLayout, new Rectangle(Point.Empty, scaledLayout.Size));
                    scaledLayout.Save(Path.Combine(directory, $"settings-dpi-{dpi}.png"), ImageFormat.Png);
                }
                main.Bounds = originalBounds;
                var hdrBox = Descendants(main).OfType<CheckBox>().Single(x => x.Text == "允许所选游戏使用自动 HDR");
                var startButton = Descendants(main).OfType<Button>().Single(x => x.Text == "开始显示");
                var applyButton = Descendants(main).OfType<Button>().Single(x => x.Text == "应用设置");
                bool couldStartBefore = startButton.Enabled;
                hdrBox.Checked = true; form.PerformLayout();
                Check(Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json")).AllowAutoHdr, "HDR checkbox persists the allow setting");
                using (var preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoHdrPreference.KeyPath))
                    Check(preferences?.GetValue(uiFixture) == null, "browsing windows or toggling HDR alone never changes another program's Windows settings");
                Check(!buttonLabels.Contains("重启生效"), "UI does not incorrectly offer to restart the utility for a game's HDR change");
                CheckAlignment("game HDR selected");
                hdrBox.Checked = false; form.PerformLayout();
                applyButton.PerformClick();
                using (var preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoHdrPreference.KeyPath))
                    Check((string?)preferences?.GetValue(uiFixture) == "AutoHDREnable=48;", "apply button writes the selected game's executable, not BottomScreen");
                Check(Descendants(main).OfType<Label>().Any(x => x.Text.Contains("请手动重启游戏")), "changed game preference visibly asks the user to restart the game");
                Check(startButton.Enabled == couldStartBefore, "HDR UI leaves the start action available to revalidate a newly selected game");
                var originalSize = main.ClientSize;
                main.ClientSize = new Size(760, 700); main.PerformLayout(); form.PerformLayout();
                CheckAlignment("compact window at native DPI");
                main.ClientSize = originalSize; main.PerformLayout(); form.PerformLayout();
                Check(!buttonLabels.Contains("打开测试画面") && !buttonLabels.Contains("打开性能日志"), "normal UI hides both diagnostic buttons");
                Check(buttonLabels.Contains("开始显示") && buttonLabels.Contains("停止"), "normal UI preserves start and stop controls");
                Check(!buttonLabels.Contains("重置鼠标输入") && !Descendants(main).OfType<CheckBox>().Any(x => x.Text.Contains("鼠标5")), "automatic cursor has no mode switching or mouse5 toggle controls");
                Check(typeof(Program).Assembly.GetType("BottomScreen.Mouse5Monitor") == null && typeof(Program).Assembly.GetType("BottomScreen.MenuMouseRouter") == null,
                    "automatic cursor build does not contain input hooks or coordinate-correction routing");
                using var ownedGame = new PatternForm();
                ownedGame.Show(); Application.DoEvents();
                var selector = (ComboBox)form.GetControlFromPosition(1, 0)!;
                selector.Items.Clear();
                selector.Items.Add(new WindowChoice(ownedGame.Handle, "BottomScreen self-test", "owned capture startup fixture"));
                selector.SelectedIndex = 0;
                applyButton.PerformClick();
                var stopButton = Descendants(main).OfType<Button>().Single(x => x.Text == "停止");
                var engineField = typeof(MainForm).GetField("engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var overlayField = typeof(MainForm).GetField("overlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                void VerifyStartButton(string label, string? warning)
                {
                    int queriesBefore = hdrQueryCount;
                    startButton.PerformClick();
                    Check(stopButton.Enabled && !startButton.Enabled, label + ": clicking Start enters capture mode");
                    Check(hdrQueryCount == queriesBefore, label + ": Start never queries the HDR target process or validates its restart state");
                    var initialOverlay = overlayField.GetValue(main) as OverlayForm;
                    Check(initialOverlay?.Visible == true, label + ": Start creates and shows the simulated screen before foreground monitoring");
                    long deadline = Environment.TickCount64 + 4000;
                    while (engineField.GetValue(main) is CaptureEngine active && active.PictureFrameCount == 0 && Environment.TickCount64 < deadline)
                    { Application.DoEvents(); Thread.Sleep(10); }
                    var running = engineField.GetValue(main) as CaptureEngine;
                    var shown = overlayField.GetValue(main) as OverlayForm;
                    Check(running?.PictureFrameCount > 0, label + ": the actual UI startup path captures and presents owned source frames");
                    Check(shown != null && shown.Bounds == Screen.FromHandle(shown.Handle).Bounds,
                        label + ": the simulated screen overlay fills the output monitor bounds");
                    if (warning != null)
                        Check(Descendants(main).OfType<Label>().Any(x => x.Text.StartsWith("显示中") && x.Text.Contains(warning)),
                            label + ": capture keeps the HDR warning visible in settings status");
                    stopButton.PerformClick(); Application.DoEvents();
                    Check(engineField.GetValue(main) == null && overlayField.GetValue(main) == null && startButton.Enabled && !stopButton.Enabled,
                        label + ": Stop releases capture, overlay and restores controls");
                }
                VerifyStartButton("HDR game restart pending", "请手动重启游戏");
                Check(Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json")).GameHdrRestartAfterUtc.Count > 0,
                    "starting capture while HDR is pending preserves the game's restart marker");
                simulateHdrReadFailure = true;
                applyButton.PerformClick();
                VerifyStartButton("HDR source preference query fails", "自动 HDR 未能应用");
                simulateHdrReadFailure = false;
                fixtureStarted = DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc();
                applyButton.PerformClick();
                VerifyStartButton("HDR matching after game restart", null);
                Check(!Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json")).GameHdrRestartAfterUtc.ContainsKey(Path.GetFullPath(uiFixture).ToUpperInvariant()),
                    "matching a restarted source clears only its fulfilled HDR restart marker");
            }
            using var bitmap = new Bitmap(main.Width, main.Height);
            main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, "settings.png"), ImageFormat.Png);
            main.Close();
        }
        }
        finally
        {
            using var preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoHdrPreference.KeyPath, true);
            preferences?.DeleteValue(uiFixture, false);
        }
        pattern = new PatternForm(); pattern.Show();
        var area = Screen.PrimaryScreen!.WorkingArea;
        output = new OverlayForm(new Rectangle(area.Right - 660, area.Top + 60, 640, 360), excludeFromCapture: false);
        _ = output.Handle;
        // For this test only, permit screen readback of our own window to verify actual composition.
        Native.SetWindowDisplayAffinity(output.Handle, 0);
        target = Geometry.Target(output.ClientSize, 31.5, 24);
        initialPerformanceFileCount = Directory.Exists(PerformanceLog.DirectoryPath) ? Directory.GetFiles(PerformanceLog.DirectoryPath).Length : 0;
        capture = new CaptureEngine(pattern.Handle, output.Handle, output.ClientSize, target, false, enablePerformanceLogging: false);
        capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
        output.Show(); capture.Start();
        timer.Start();
    }
    private void Tick()
    {
        if (complete) return;
        if ((DateTime.UtcNow - start).TotalSeconds > 25) throw new TimeoutException("capture/render test timed out");
        if (capture == null || pattern == null || output == null || capture.FrameCount - stageFrames < 20) return;
        if (stage == 0)
        {
            Check(capture.MaximumFrameLatency == 1, "DXGI device maximum render queue latency is set to one and read back");
            Check(capture.EarlyFrameReleases == capture.FrameCount, "all capture-driven presentations release the WGC frame before rendering and Present");
            Check(capture.ImageCopies <= capture.FrameCount, "cached image copies never exceed capture-driven submissions");
            Check(capture.PerformanceLogPath == null && capture.PerformanceLogError == null, "normal capture does not start a performance logger");
            Check((Directory.Exists(PerformanceLog.DirectoryPath) ? Directory.GetFiles(PerformanceLog.DirectoryPath).Length : 0) == initialPerformanceFileCount,
                "normal capture renders frames without creating performance log files");
            using var frame = capture.Snapshot();
            Verify(frame, target, "24"); frame.Save(Path.Combine(directory, "capture-24.png"), ImageFormat.Png);
            Check(capture.SourceSize == pattern.ClientSize, $"client-only source crop: {capture.SourceSize}");
            // Validate the pixels actually visible on the desktop, not only GPU readback.
            using var screen = new Bitmap(output.Width, output.Height);
            using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(output.Location, Point.Empty, screen.Size);
            screen.Save(Path.Combine(directory, "visible-output.png"), ImageFormat.Png);
            Verify(screen, target, "visible click-through composition");
            Check(Native.WindowFromPoint(new Point(output.Left + output.Width / 2, output.Top + output.Height / 2)) != output.Handle,
                "layered output window passes native hit testing through to underlying windows");
            pattern.ClientSize = new Size(800, 600);
            stageFrames = capture.FrameCount; stage = 1; return;
        }
        if (stage == 1)
        {
            if (capture.SourceSize != pattern.ClientSize) return;
            using var frame = capture.Snapshot(); Verify(frame, target, "resized source");
            Check(capture.SourceSize == new Size(800, 600), "capture pool recreates after source resize");
            capture.Dispose(); capture = null;
            target = Geometry.Target(output.ClientSize, 31.5, 27);
            capture = new CaptureEngine(pattern.Handle, output.Handle, output.ClientSize, target, true);
            capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
            capture.Start(); stageFrames = 0; stage = 2; return;
        }
        if (stage == 2)
        {
            using (var frame = capture.Snapshot())
            {
                Verify(frame, target, "27 nearest"); frame.Save(Path.Combine(directory, "capture-27.png"), ImageFormat.Png);
            }
            capture.Dispose(); capture = null;
            target = Geometry.Target(new Size(3840, 2160), 31.5, 24);
            capture = new CaptureEngine(pattern.Handle, output.Handle, new Size(3840, 2160), target, false);
            capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
            capture.Start(); stageFrames = 0; stage = 3; return;
        }
        if (stage == 3)
        {
            using var frame = capture.Snapshot();
            Verify(frame, target, "real 4K GPU backbuffer");
            Check(frame.Size == new Size(3840, 2160), "4K GPU output dimensions");
            var fullHud = capture.HudMetrics;
            Check(fullHud.Bounds.Bottom < target.Top && fullHud.Bounds.Width > 0, "4K FPS HUD fits above the game viewport");
            frame.Save(Path.Combine(directory, "fps-hud-4k.png"), ImageFormat.Png);
            capture.Dispose(); capture = null;
            target = Geometry.Target(new Size(1920, 1080), 31.5, 27);
            capture = new CaptureEngine(pattern.Handle, output.Handle, new Size(1920, 1080), target, false);
            capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
            capture.Start(); stageFrames = 0; stage = 4; return;
        }
        if (stage == 4)
        {
            using (var frame = capture.Snapshot())
            {
                Verify(frame, target, "real 1080p GPU backbuffer");
                Check(frame.Size == new Size(1920, 1080), "1080p GPU output dimensions");
            }
            capture.Dispose(); capture = null;
            pattern.FormBorderStyle = FormBorderStyle.None;
            pattern.Bounds = Screen.PrimaryScreen!.Bounds;
            pattern.Activate();
            target = Geometry.Target(output.ClientSize, 31.5, 24);
            capture = new CaptureEngine(pattern.Handle, output.Handle, output.ClientSize, target, false);
            capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
            capture.Start(); stageFrames = 0; stage = 5; return;
        }
        if (stage == 5)
        {
            using (var frame = capture.Snapshot())
            {
                Verify(frame, target, "borderless fullscreen complete source");
                Check(capture.SourceSize == Screen.PrimaryScreen!.Bounds.Size, "borderless fullscreen captures entire native-resolution client");
                frame.Save(Path.Combine(directory, "borderless-full.png"), ImageFormat.Png);
            }
            capture.Dispose(); capture = null;
            pattern.Pillarboxed = true; pattern.Invalidate(); pattern.Update();
            capture = new CaptureEngine(pattern.Handle, output.Handle, output.ClientSize, target, false, true, Native.RefreshRate(Screen.PrimaryScreen!), enablePerformanceLogging: true);
            capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
            capture.Start(); stageFrames = 0; stage = 6; return;
        }
        if (stage == 6)
        {
            using (var frame = capture.Snapshot())
            {
                Verify(frame, target, "borderless fullscreen centered 4:3");
                var expected = Geometry.SourceCrop(new Rectangle(Point.Empty, pattern.ClientSize), true);
                Check(capture.SourceSize == expected.Size && capture.ClientSize == pattern.ClientSize, "effective crop and original fullscreen dimensions reported separately");
                Check(IsColor(frame.GetPixel(target.Left + 3, target.Top + target.Height / 4), Color.Red), "left source pillarbox removed");
                Check(IsColor(frame.GetPixel(target.Right - 4, target.Top + target.Height / 4), Color.Lime), "right source pillarbox removed");
                frame.Save(Path.Combine(directory, "borderless-4x3.png"), ImageFormat.Png);
            }
            stage = 7; return;
        }
        var hud = capture.HudMetrics;
        if (hud.Fps == null) return;
        if (stage == 7)
        {
            // Let both flip buffers contain the measured number before taking the verification image.
            stageFrames = capture.FrameCount; stage = 8; return;
        }
        using (var frame = capture.Snapshot())
        {
            Check(hud.Fps > 0 && hud.Text.Contains("画面更新") && hud.Text.Contains("FPS"), "HUD shows a measured nonzero source-image update rate");
            Check(hud.Bounds.Top >= 0 && hud.Bounds.Bottom < target.Top, "FPS HUD stays entirely inside the top black region");
            int bright = 0;
            for (int y = hud.Bounds.Top; y < hud.Bounds.Bottom; y++)
            for (int x = hud.Bounds.Left; x < hud.Bounds.Right; x++)
            {
                var c = frame.GetPixel(x, y);
                if (c.R > 180 && c.G > 180 && c.B > 180) bright++;
            }
            Check(bright > 30, "FPS text is present in actual GPU output pixels");
            Verify(frame, target, "FPS HUD preserves game content");
            frame.Save(Path.Combine(directory, "fps-hud.png"), ImageFormat.Png);
        }
        checks.Add($"INFO: delivered {capture.FrameCount} frames in final test; no 320Hz or CS2 performance claim");
        string finalLog = capture.PerformanceLogPath!;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) && Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
        {
            Check(capture.UpdateIntervalSupported && capture.AppliedUpdateIntervalMs == 0,
                "supported WGC session reads back zero minimum update interval");
            checks.Add($"INFO: original WGC minimum update interval {capture.OriginalUpdateIntervalMs:F4} ms; applied {capture.AppliedUpdateIntervalMs:F4} ms; no guaranteed game FPS");
        }
        long finalCount = capture.FrameCount;
        capture.Dispose(); capture = null;
        var finalRows = File.ReadAllLines(finalLog).Select(x => JsonDocument.Parse(x)).ToArray();
        try
        {
            var stopRow = finalRows[^1].RootElement;
            Check(stopRow.GetProperty("type").GetString() == "stop" && stopRow.GetProperty("totalSubmitted").GetInt64() >= finalCount,
                "real WGC session drains performance log with submitted frame totals");
            Check(stopRow.GetProperty("droppedLogSamples").GetInt64() == 0, "real capture diagnostic queue has no lost log samples in self-test");
            Check(finalRows.Any(x => x.RootElement.GetProperty("type").GetString() == "interval" &&
                x.RootElement.GetProperty("textureWidth").GetInt32() > 0), "real capture intervals contain texture dimensions and stage timings");
            var metadata = finalRows[0].RootElement.GetProperty("metadata");
            Check(metadata.GetProperty("maximumFrameLatency").GetInt32() == 1 && metadata.GetProperty("captureFrameLifetime").GetString()!.Contains("before rendering and Present"),
                "diagnostics record the actual queue limit and early frame release policy");
            Check(finalRows.Where(x => x.RootElement.GetProperty("type").GetString() == "interval").All(x =>
                x.RootElement.GetProperty("framesReleasedBeforePresent").GetInt32() == x.RootElement.GetProperty("submitted").GetInt32()),
                "native capture diagnostics confirm every submitted frame was released before Present");
            Check(metadata.TryGetProperty("originalMinUpdateIntervalMs", out _) && metadata.TryGetProperty("appliedMinUpdateIntervalMs", out _),
                "capture metadata records original and applied update intervals");
        }
        finally { foreach (var row in finalRows) row.Dispose(); }
        VerifyGpuCacheAndCursor();
        Finish(null);
    }

    private unsafe void VerifyGpuCacheAndCursor()
    {
        using var window = new OverlayForm(new Rectangle(40, 40, 640, 360), excludeFromCapture: false);
        using var renderer = new GpuRenderer(window.Handle, new Size(640, 360), false, showFps: false);
        var pixels = new byte[640 * 360 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = 150; pixels[i + 3] = 255; }
        fixed (byte* pointer = pixels)
        {
            using var texture = renderer.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
            {
                Width = 640, Height = 360, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Immutable,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource
            }, new Vortice.Direct3D11.SubresourceData((nint)pointer, 640 * 4, 0));
            renderer.StoreImage(texture);
        }
        // Source texture is gone; cached redraws must not rely on its lifetime.
        var crop = new Rectangle(0, 0, 640, 360);
        var destination = Geometry.Target(new Size(640, 360), 31.5, 24);
        for (int i = 0; i < 3; i++) renderer.Draw(null, crop, destination);
        Check(renderer.PictureFrameCount == 1, "cached redraws count only the first successful source-image presentation");
        using (var cached = renderer.Snapshot())
            Check(IsColor(cached.GetPixel(destination.X + 20, destination.Y + 20), Color.FromArgb(0, 150, 0)),
                "GPU cache survives release of the original source and redraws without recopying");
        foreach (var style in new[] { Cursors.Arrow, Cursors.Hand, Cursors.IBeam, Cursors.Cross })
        {
            var sprite = CursorSpriteCache.Capture(style.Handle);
            renderer.Draw(null, crop, destination, cursor: new CursorFrame(sprite, new Point(320, 180)));
            renderer.Draw(null, crop, destination, cursor: new CursorFrame(sprite, new Point(320, 180)));
            using var rendered = renderer.Snapshot();
            int changed = 0;
            for (int y = destination.Top + destination.Height / 2 - 45; y < destination.Top + destination.Height / 2 + 65; y++)
            for (int x = destination.Left + destination.Width / 2 - 45; x < destination.Left + destination.Width / 2 + 65; x++)
                if (!IsColor(rendered.GetPixel(x, y), Color.FromArgb(0, 150, 0))) changed++;
            Check(changed > 5, "cached GPU redraw preserves native cursor shape pixels: " + style);
        }
        Check(renderer.ImageCopies == 1, "repeated GPU redraws and cursor changes do not recopy the game image");
        Check(renderer.PictureFrameCount == 1, "all four cursor shapes and cursor redraws are excluded from picture FPS");
        renderer.Draw(null, crop, destination);
        renderer.Draw(null, crop, destination);
        using (var hidden = renderer.Snapshot())
            Check(IsColor(hidden.GetPixel(destination.X + destination.Width / 2, destination.Y + destination.Height / 2), Color.FromArgb(0, 150, 0)),
                "cached GPU redraw removes the simulated cursor when the source hides it");
        Check(renderer.PictureFrameCount == 1, "cursor removal and additional cached submissions are excluded from picture FPS");
        fixed (byte* pointer = pixels)
        {
            using var next = renderer.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
            {
                Width = 640, Height = 360, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Immutable,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource
            }, new Vortice.Direct3D11.SubresourceData((nint)pointer, 640 * 4, 0));
            renderer.StoreImage(next);
        }
        Check(renderer.PictureFrameCount == 1, "copying a new source without presenting it does not increment picture FPS");
        renderer.Draw(null, crop, destination);
        Check(renderer.PictureFrameCount == 2, "a successfully presented new source image increments picture FPS exactly once");
        renderer.Draw(null, crop, destination);
        Check(renderer.PictureFrameCount == 2, "redrawing the second source image does not double count it");
    }

    private void Verify(Bitmap image, Rectangle region, string label)
    {
        Check(IsColor(image.GetPixel(5, 5), Color.Black), label + ": top black");
        Check(IsColor(image.GetPixel(2, image.Height - 4), Color.Black), label + ": left black");
        Check(IsColor(image.GetPixel(image.Width - 3, image.Height - 4), Color.Black), label + ": right black");
        foreach (var (x, y, color, name) in new[] { (.25, .25, Color.Red, "top-left"), (.75, .25, Color.Lime, "top-right"), (.25, .75, Color.Blue, "bottom-left"), (.75, .75, Color.White, "bottom-right") })
            Check(IsColor(image.GetPixel(region.Left + (int)(region.Width * x), region.Top + (int)(region.Height * y)), color), label + ": full-frame " + name);
        Check(IsColor(image.GetPixel(region.Left + region.Width / 4, image.Height - 2), Color.Blue), label + ": image touches bottom");
    }
    private static bool IsColor(Color a, Color b) => Math.Abs(a.R - b.R) < 35 && Math.Abs(a.G - b.G) < 35 && Math.Abs(a.B - b.B) < 35;
    private void Finish(Exception? error)
    {
        if (complete) return; complete = true;
        timer.Stop(); capture?.Dispose(); output?.Dispose(); pattern?.Dispose();
        var result = new { passed = error == null, timestamp = DateTimeOffset.Now, checks, error = error?.ToString(), limitations = "CS2 compatibility, HDR, 320Hz throughput and end-to-end latency require actual game/hardware testing." };
        File.WriteAllText(Path.Combine(directory, "self-test.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = error == null ? 0 : 1;
        Close();
    }
}
