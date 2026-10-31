using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BottomScreen;

// Only uses its own fixture windows and child processes. No game input or cursor movement.
internal sealed class CursorSelfTestForm : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 40 };
    private readonly List<string> checks = new();
    private readonly string directory;
    private readonly long started = Environment.TickCount64;
    private Fixture? source;
    private OverlayForm? output;
    private CaptureEngine? capture;
    private SystemCursorGuard? guard;
    private Process? testParent;
    private int stage;
    private long stageFrame, stageTime;
    private nint originalCursor;
    private nint eligibilityWindow;
    private bool finished;

    public CursorSelfTestForm(string[] args)
    {
        int index = Array.IndexOf(args, "--report-dir");
        directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(AppContext.BaseDirectory, "cursor-test");
        Directory.CreateDirectory(directory);
        Text = "BottomScreen 自动光标自测"; Size = new Size(400, 100);
        Shown += (_, _) => { try { Begin(); } catch (Exception e) { Finish(e); } };
        timer.Tick += (_, _) => { try { Tick(); } catch (Exception e) { Finish(e); } };
    }

    private void Check(bool ok, string text)
    {
        if (!ok) throw new InvalidOperationException(text);
        checks.Add("PASS: " + text);
    }
    private static Native.CursorInfo CursorInfo()
    {
        var cursor = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
        if (!Native.GetCursorInfo(ref cursor)) throw new InvalidOperationException("GetCursorInfo failed");
        return cursor;
    }
    private void Begin()
    {
        var rect = new Rectangle(-100, -100, 200, 200);
        Check(SystemCursorGuard.ShouldHide(1, 1, rect, Point.Empty, 1, 1000, 900, true), "fresh foreground cursor inside sampled area is eligible");
        Check(!SystemCursorGuard.ShouldHide(1, 2, rect, Point.Empty, 1, 1000, 900, true), "foreground loss restores original cursor");
        Check(!SystemCursorGuard.ShouldHide(1, 1, rect, new Point(1000, 0), 1, 1000, 900, true), "pointer outside the sampled crop remains visible");
        Check(!SystemCursorGuard.ShouldHide(1, 1, rect, Point.Empty, 0, 1000, 900, true), "game-hidden cursor is not forced visible");
        Check(!SystemCursorGuard.ShouldHide(1, 1, rect, Point.Empty, 1, 2000, 900, true), "expired lease restores cursor after UI stalls");
        Check(!SystemCursorGuard.ShouldHide(1, 1, rect, Point.Empty, 1, 1000, 900, false), "stop request restores original cursor");
        foreach (var style in new[] { Cursors.Arrow, Cursors.Hand, Cursors.IBeam, Cursors.Cross })
        {
            var shape = CursorSpriteCache.Capture(style.Handle);
            Check(shape.Width > 0 && shape.Height > 0 && shape.Hotspot.X >= 0 && shape.Hotspot.Y >= 0 &&
                shape.Hotspot.X < shape.Width && shape.Hotspot.Y < shape.Height && shape.Black.Length == shape.Width * shape.Height * 4 &&
                shape.White.Length == shape.Black.Length && !shape.Black.SequenceEqual(shape.White), "native cursor copy retains dimensions, hotspot and dual-background pixels: " + style);
        }
        var screen = Screen.FromPoint(CursorInfo().Position);
        source = new Fixture(screen.Bounds); source.Show(); source.Activate();
        output = new OverlayForm(new Rectangle(screen.Bounds.Right - 660, screen.Bounds.Top + 40, 640, 360));
        _ = output.Handle;
        var destination = Geometry.Target(screen.Bounds.Size, 31.5, 24);
        capture = new CaptureEngine(source.Handle, output.Handle, screen.Bounds.Size, destination, false, showFps: false, enablePerformanceLogging: false);
        capture.Failed += message => BeginInvoke(() => Finish(new Exception(message)));
        output.Show(); capture.Start(); timer.Start();
    }
    private void Tick()
    {
        if (Environment.TickCount64 - started > 15000) throw new TimeoutException($"cursor integration test timed out at stage {stage}; source={source?.Handle}; foreground={Native.GetForegroundWindow()}; region={capture?.CursorSourceScreen}; cursor={CursorInfo().Position}; {guard?.DebugStatus}");
        if (source == null || capture == null || capture.FrameCount < 15) return;
        if (guard != null && stage is 1 or 2 or 4) guard.Pulse(eligibilityWindow, capture.CursorSourceScreen);
        if (stage == 0)
        {
            var cursor = CursorInfo(); originalCursor = cursor.Cursor;
            // Windows may refuse focus to a background-launched test fixture.
            // Read the existing foreground identity for the display-only guard;
            // never activate it or send any input to that window.
            eligibilityWindow = Native.GetForegroundWindow();
            Check((cursor.Flags & 1) != 0 && originalCursor != 0, "own fixture has an actual visible system cursor");
            using var frame = capture.Snapshot();
            Check(capture.CursorIsRendered && CountCursor(frame, cursor.Position) > 10, "default GPU output contains the copied source cursor before hiding");
            frame.Save(Path.Combine(directory, "native-cursor-before-hide.png"), ImageFormat.Png);
            guard = new SystemCursorGuard(); stage = 1; return;
        }
        if (stage == 1)
        {
            if (!guard!.IsHidden) return;
            Check(guard.IsReady, "native display-only hide initialized in the recovery helper");
            stageFrame = capture.FrameCount; stage = 2; return;
        }
        if (stage == 2)
        {
            if (capture.FrameCount - stageFrame < 15) return;
            var cursor = CursorInfo();
            Check((cursor.Flags & 1) != 0 && cursor.Cursor == originalCursor, "display-only hiding preserves original cursor visibility state and shape handle");
            using var frame = capture.Snapshot();
            Check(capture.CursorIsRendered && CountCursor(frame, cursor.Position) > 10, "GPU retains copied original cursor pixels while desktop cursor is hidden");
            frame.Save(Path.Combine(directory, "native-cursor-desktop-hidden.png"), ImageFormat.Png);
            guard!.Pulse(0, Rectangle.Empty); stageTime = Environment.TickCount64; stage = 3; return;
        }
        if (stage == 3)
        {
            if (guard!.IsHidden) return;
            Check(true, "empty source request restores cursor while the application remains running");
            stage = 4; return;
        }
        if (stage == 4)
        {
            if (!guard!.IsHidden) return;
            stageTime = Environment.TickCount64; stage = 5; return;
        }
        if (stage == 5)
        {
            if (Environment.TickCount64 - stageTime <= SystemCursorGuard.LeaseMilliseconds + 100 || guard!.IsHidden) return;
            Check(true, "independent helper restores cursor when heartbeat expires");
            guard.Dispose(); guard = null;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--cursor-test-parent");
            testParent = Process.Start(start) ?? throw new InvalidOperationException("test parent start failed");
            guard = new SystemCursorGuard(testParent.Id);
            stage = 6; return;
        }
        if (stage == 6)
        {
            guard!.Pulse(eligibilityWindow, capture.CursorSourceScreen);
            if (!guard.IsHidden) return;
            testParent!.Kill(); testParent.WaitForExit(1000);
            stage = 7; return;
        }
        if (stage == 7)
        {
            if (!guard!.HasRestoredAndExited) return;
            Check(!guard.IsHidden, "helper restores cursor and exits after its parent is terminated without cleanup");
            Finish(null);
        }
    }
    private int CountCursor(Bitmap frame, Point cursorScreen)
    {
        Rectangle sampled = capture!.CursorSourceScreen;
        var target = Geometry.Target(frame.Size, 31.5, 24);
        int x = target.X + (int)((cursorScreen.X - sampled.X + .5) * target.Width / sampled.Width);
        int y = target.Y + (int)((cursorScreen.Y - sampled.Y + .5) * target.Height / sampled.Height);
        Rectangle patch = Rectangle.Intersect(new Rectangle(x - 35, y - 35, 100, 100), target);
        int count = 0;
        for (int py = patch.Top; py < patch.Bottom; py++)
        for (int px = patch.Left; px < patch.Right; px++)
        {
            var c = frame.GetPixel(px, py);
            if (Math.Abs(c.G - Fixture.Green.G) > 45 || c.R > 45 || c.B > 45) count++;
        }
        return count;
    }
    private void Finish(Exception? error)
    {
        if (finished) return; finished = true;
        timer.Stop(); guard?.Dispose(); capture?.Dispose(); output?.Dispose(); source?.Dispose();
        if (testParent != null) { if (!testParent.HasExited) testParent.Kill(); testParent.Dispose(); }
        File.WriteAllText(Path.Combine(directory, "cursor-self-test.json"), JsonSerializer.Serialize(new { passed = error == null, checks, error = error?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = error == null ? 0 : 1; Close();
    }
    private sealed class Fixture : Form
    {
        public static readonly Color Green = Color.FromArgb(0, 150, 0);
        private readonly System.Windows.Forms.Timer paint = new() { Interval = 16 };
        private int tick;
        public Fixture(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = bounds;
            Cursor = Cursors.Hand; DoubleBuffered = true;
            paint.Tick += (_, _) => { tick++; Invalidate(); }; paint.Start();
            FormClosed += (_, _) => paint.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Green);
            e.Graphics.FillRectangle(Brushes.White, Width - 3, tick % Math.Max(1, Height - 2), 2, 2);
        }
    }
}
