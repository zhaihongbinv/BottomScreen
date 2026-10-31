using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using System.Diagnostics;

namespace BottomScreen;

internal sealed record PublishedCursorState(Rectangle Source, bool Rendered);
internal readonly record struct SourceFrameIdentity(long Timestamp, Size Content, Size Texture, Rectangle Crop);

internal sealed class CaptureEngine : IDisposable
{
    private readonly object gate = new();
    private readonly nint source;
    private readonly bool centerFourThree;
    private readonly GpuRenderer renderer;
    private readonly CursorSpriteCache cursorCache = new();
    private readonly System.Threading.Timer cursorRefresh;
    private Rectangle lastCrop;
    private CursorFrame? lastCursor;
    private long lastCaptureTime;
    private long lastPresentTime;
    private SourceFrameIdentity? lastIdentity;
    private readonly bool showFps;
    private PublishedCursorState cursorState = new(Rectangle.Empty, false);
    public PublishedCursorState CursorState => Volatile.Read(ref cursorState);
    public long RepeatedSourceFrames { get; private set; }
    public long SupersededFrames { get; private set; }
    public long EarlyFrameReleases { get; private set; }
    public long ImageCopies { get { lock (gate) return renderer.ImageCopies; } }
    public uint MaximumFrameLatency => renderer.MaximumFrameLatency;
    private readonly IDirect3DDevice captureDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private Windows.Graphics.SizeInt32 poolSize;
    private Rectangle destination;
    private bool disposed;
    private bool failed;
    private readonly PerformanceLog? performance;
    public string StopReason { get; set; } = "disposed";
    public string? PerformanceLogPath => performance?.FilePath;
    public string? PerformanceLogError => performance?.Error;
    public bool UpdateIntervalSupported { get; }
    public double? OriginalUpdateIntervalMs { get; }
    public double? AppliedUpdateIntervalMs { get; }
    public long FrameCount { get; private set; }
    public Size SourceSize { get; private set; }
    public Size ClientSize { get; private set; }
    private Rectangle cursorSourceScreen;
    public Rectangle CursorSourceScreen => CursorState.Source;
    public bool CursorIsRendered => CursorState.Rendered;
    public event Action<string>? Failed;
    public (double? Fps, string Text, Rectangle Bounds) HudMetrics
    {
        get { lock (gate) return (renderer.SubmissionFps, renderer.HudText, renderer.HudBounds); }
    }

    public CaptureEngine(nint source, nint output, Size size, Rectangle destination, bool nearest, bool centerFourThree = false, int refreshHz = 0, bool showFps = true, bool? enablePerformanceLogging = null)
    {
        this.source = source; this.destination = destination;
        this.showFps = showFps;
        this.centerFourThree = centerFourThree;
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("系统不支持 Windows 窗口捕获。");
        renderer = new GpuRenderer(output, size, nearest, refreshHz, showFps);
        try
        {
            using var dxgi = renderer.Device.QueryInterface<IDXGIDevice>();
            captureDevice = CaptureInterop.Device(dxgi.NativePointer);
            item = CaptureInterop.ForWindow(source);
            poolSize = item.Size;
            if (poolSize.Width <= 0 || poolSize.Height <= 0) throw new InvalidOperationException("游戏窗口没有有效画面，请先还原窗口。");
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            // Request frames as soon as WGC can deliver them. This is a request, not a promised FPS.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
                Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
            {
                UpdateIntervalSupported = true;
                OriginalUpdateIntervalMs = session.MinUpdateInterval.TotalMilliseconds;
                session.MinUpdateInterval = TimeSpan.Zero;
                AppliedUpdateIntervalMs = session.MinUpdateInterval.TotalMilliseconds;
            }
            // Keep the system capture border; removing it requires a separate Windows consent capability.
            pool.FrameArrived += OnFrame;
            item.Closed += OnClosed;
            if (enablePerformanceLogging ?? Program.PerformanceLoggingEnabled)
                performance = new PerformanceLog(new { outputWidth = size.Width, outputHeight = size.Height,
                refreshHz, sourceMonitorHz = Native.RefreshRate(Screen.FromHandle(source)),
                sourceInitialWidth = poolSize.Width, sourceInitialHeight = poolSize.Height,
                destination = new { destination.X, destination.Y, destination.Width, destination.Height },
                nearest, centerFourThree, showFps, adapter = renderer.AdapterName,
                capture = "WGC CreateFreeThreaded", captureBuffers = 2,
                minUpdateIntervalSupported = UpdateIntervalSupported,
                originalMinUpdateIntervalMs = OriginalUpdateIntervalMs,
                requestedMinUpdateIntervalMs = UpdateIntervalSupported ? 0.0 : (double?)null,
                appliedMinUpdateIntervalMs = AppliedUpdateIntervalMs,
                swapChain = "BGRA8 FlipSequential composition; buffers=2; Present(0,None)",
                maximumFrameLatency = renderer.MaximumFrameLatency,
                captureFrameLifetime = "released after GPU copy enqueue, before rendering and Present",
                frameSelection = "newest of at most two queued frames; identical source timestamps reuse cached image" });
            cursorRefresh = new System.Threading.Timer(RefreshCursor, null, 8, 8);
        }
        catch
        {
            session?.Dispose(); pool?.Dispose(); (captureDevice as IDisposable)?.Dispose();
            renderer.Dispose(); throw;
        }
    }

    public void Start() => session.StartCapture();

    private void OnClosed(GraphicsCaptureItem sender, object args) => Fail("游戏窗口已关闭。");

    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        bool profile = performance != null;
        long arrival = profile ? Stopwatch.GetTimestamp() : 0;
        lock (gate)
        {
            if (disposed || failed) return;
            long entered = profile ? Stopwatch.GetTimestamp() : 0, acquired = entered, prepared = entered;
            long? sourceTicks = null;
            bool hasFrame = false, submitted = false, recreated = false, duplicate = false, released = false;
            int superseded = 0;
            Size textureSize = Size.Empty;
            Rectangle loggedCrop = Rectangle.Empty;
            DrawTiming timing = default;
            try
            {
                Windows.Graphics.SizeInt32 content = default;
                Rectangle crop = Rectangle.Empty, client = Rectangle.Empty, sampledScreen = Rectangle.Empty;
                CursorFrame? cursor = null;
                (double ResourceMs, double CopyMs) stored = default;
                bool copied = false;
                var frame = sender.TryGetNextFrame();
                try
                {
                    acquired = profile ? Stopwatch.GetTimestamp() : 0; prepared = acquired;
                    if (frame == null) return;
                    // Bounded drain: don't display an older queued frame when a newer
                    // one is already available, and never spin waiting for a new frame.
                    var newer = sender.TryGetNextFrame();
                    if (newer != null) { frame.Dispose(); frame = newer; superseded = 1; SupersededFrames++; }
                    hasFrame = true;
                    sourceTicks = frame.SystemRelativeTime.Ticks;
                    content = frame.ContentSize;
                    if (content.Width <= 0 || content.Height <= 0) return;
                    using var texture = new ID3D11Texture2D(CaptureInterop.Texture(frame.Surface));
                    var desc = texture.Description;
                    textureSize = new Size((int)desc.Width, (int)desc.Height);
                    var valid = new Size(Math.Min(content.Width, (int)desc.Width), Math.Min(content.Height, (int)desc.Height));
                    client = Native.ClientCrop(source, valid, out var captureOrigin);
                    crop = Geometry.SourceCrop(client, centerFourThree);
                    loggedCrop = crop;
                    prepared = profile ? Stopwatch.GetTimestamp() : 0;
                    if (crop.Width > 0 && crop.Height > 0)
                    {
                        sampledScreen = crop; sampledScreen.Offset(captureOrigin);
                        cursor = cursorCache.Read(sampledScreen);
                        var identity = new SourceFrameIdentity(sourceTicks.Value, new Size(content.Width, content.Height), textureSize, crop);
                        duplicate = lastIdentity == identity;
                        if (duplicate) RepeatedSourceFrames++;
                        else
                        {
                            stored = renderer.StoreImage(texture, profile);
                            lastIdentity = identity; copied = true;
                        }
                    }
                }
                finally { frame?.Dispose(); released = true; }
                // The frame-pool slot is free before any rendering or Present wait.
                if (crop.Width > 0 && crop.Height > 0)
                {
                    long now = Environment.TickCount64;
                    if (copied || cursor != lastCursor || (showFps && now - lastPresentTime >= 1000))
                    {
                        timing = renderer.Draw(null, crop, destination, profile, cursor);
                        timing = timing with { ResourceMs = stored.ResourceMs, CopyEnqueueMs = stored.CopyMs };
                        submitted = true; FrameCount++; EarlyFrameReleases++;
                        lastPresentTime = now;
                    }
                    SourceSize = crop.Size; ClientSize = client.Size;
                    cursorSourceScreen = sampledScreen;
                    lastCrop = crop; lastCursor = cursor; lastCaptureTime = now;
                    PublishCursorState();
                }
                if (content.Width != poolSize.Width || content.Height != poolSize.Height)
                {
                    poolSize = content;
                    sender.Recreate(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
                    recreated = true;
                }
            }
            catch (Exception e) { Fail("捕获或显示中断：" + e.Message); }
            finally
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                performance?.Record(new FrameTiming(arrival, (entered - arrival) * ms,
                    (acquired - entered) * ms, (prepared - acquired) * ms,
                    (Stopwatch.GetTimestamp() - arrival) * ms, sourceTicks,
                    hasFrame, submitted, recreated, textureSize, loggedCrop, timing, superseded, duplicate, released));
            }
        }
    }

    private void Fail(string message)
    {
        if (disposed || failed) return;
        failed = true;
        StopReason = message;
        Program.Log(message);
        Failed?.Invoke(message);
    }

    public Bitmap Snapshot() { lock (gate) return renderer.Snapshot(); }

    private void RefreshCursor(object? state)
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (disposed || failed || lastCrop.IsEmpty || Environment.TickCount64 - lastCaptureTime < 12) return;
            var cursor = cursorCache.Read(cursorSourceScreen);
            long now = Environment.TickCount64;
            if (cursor == lastCursor && (!showFps || now - lastPresentTime < 1000)) return;
            // Static menus need pointer updates even without a new game frame.
            // Reuse the GPU image; never read back or recapture a full frame here.
            renderer.Draw(null, lastCrop, destination, false, cursor);
            lastCursor = cursor; lastPresentTime = now;
            PublishCursorState();
        }
        catch (Exception e) { Fail("光标显示中断：" + e.Message); }
        finally { Monitor.Exit(gate); }
    }

    private void PublishCursorState()
    {
        var previous = CursorState;
        bool rendered = lastCursor != null;
        if (previous.Source != cursorSourceScreen || previous.Rendered != rendered)
            Volatile.Write(ref cursorState, new PublishedCursorState(cursorSourceScreen, rendered));
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        cursorRefresh.Dispose();
        pool.FrameArrived -= OnFrame;
        item.Closed -= OnClosed;
        session.Dispose(); pool.Dispose();
        lock (gate) renderer.Dispose();
        (captureDevice as IDisposable)?.Dispose();
        performance?.Complete(StopReason);
        performance?.Dispose();
    }
}
