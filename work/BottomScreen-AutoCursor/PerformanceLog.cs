using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace BottomScreen;

internal readonly record struct DrawTiming(double ResourceMs, double CopyEnqueueMs, double DrawEnqueueMs, double HudMs, double PresentMs);
internal readonly record struct FrameTiming(long Arrival, double LockMs, double AcquireMs, double PrepareMs,
    double CallbackMs, long? SourceTicks, bool HasFrame, bool Submitted, bool Recreated,
    Size TextureSize, Rectangle Crop, DrawTiming Draw, int Superseded = 0, bool DuplicateSource = false, bool ReleasedBeforePresent = false);

// Only bounded, nonblocking enqueue occurs on the capture thread. Disk IO and aggregation run elsewhere.
internal sealed class PerformanceLog : IDisposable
{
    private readonly Channel<FrameTiming> queue = Channel.CreateBounded<FrameTiming>(new BoundedChannelOptions(2048)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private readonly long start = Stopwatch.GetTimestamp();
    private long dropped;
    private string reason = "disposed";
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "logs");
    public string FilePath { get; }
    public string? Error { get; private set; }
    public Task Completion => worker;

    public PerformanceLog(object metadata, string? directory = null)
    {
        FilePath = Path.Combine(directory ?? DirectoryPath, $"performance-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
        worker = Task.Run(() => Write(metadata));
    }

    public void Record(FrameTiming timing)
    {
        if (!queue.Writer.TryWrite(timing)) Interlocked.Increment(ref dropped);
    }

    private async Task Write(object metadata)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var output = new StreamWriter(FilePath, false, new System.Text.UTF8Encoding(false));
            void Line(object value) => output.WriteLine(JsonSerializer.Serialize(value));
            Line(new { type = "session", version = typeof(Program).Assembly.GetName().Version?.ToString(), time = DateTimeOffset.Now,
                os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), metadata,
                definitions = "All durations are CPU wall time, not GPU execution. WGC source timestamps are used only for deltas, not latency. Callback rate is backpressure-dependent, not independent capture capacity. Log submittedFps counts capture-driven successful Present calls; the HUD counts distinct source images successfully presented, excluding cached cursor/HUD redraws. Neither is panel scanout. No screenshots, window titles or keystrokes recorded." });
            await output.FlushAsync();
            var samples = new List<FrameTiming>(1024);
            long windowStart = start, previousArrival = 0;
            long? previousSource = null;
            long totalCallbacks = 0, totalFrames = 0, totalSubmitted = 0;
            var gaps = new List<double>(); var sourceGaps = new List<double>();
            int sourceRepeated = 0, sourceNonMonotonic = 0;
            void Flush(long now)
            {
                double seconds = (double)(now - windowStart) / Stopwatch.Frequency;
                if (samples.Count == 0 || seconds <= 0) return;
                int frames = samples.Count(x => x.HasFrame), submitted = samples.Count(x => x.Submitted);
                var last = samples[^1];
                Line(new { type = "interval", elapsedSeconds = (double)(now - start) / Stopwatch.Frequency,
                    seconds, callbacks = samples.Count, frames, submitted, submittedFps = submitted / seconds,
                    empty = samples.Count(x => !x.HasFrame), recreates = samples.Count(x => x.Recreated),
                    sourceRepeated, sourceNonMonotonic, textureWidth = last.TextureSize.Width, textureHeight = last.TextureSize.Height,
                    supersededQueuedFrames = samples.Sum(x => x.Superseded),
                    reusedSourceImages = samples.Count(x => x.DuplicateSource),
                    skippedRepeatedSubmissions = samples.Count(x => x.DuplicateSource && !x.Submitted),
                    framesReleasedBeforePresent = samples.Count(x => x.Submitted && x.ReleasedBeforePresent),
                    crop = new { last.Crop.X, last.Crop.Y, last.Crop.Width, last.Crop.Height },
                    callbackGapMs = Summarize(gaps), sourceTimestampGapMs = Summarize(sourceGaps),
                    lockWaitMs = Summarize(samples.Select(x => x.LockMs)), acquireMs = Summarize(samples.Select(x => x.AcquireMs)),
                    prepareMs = Summarize(samples.Select(x => x.PrepareMs)), callbackTotalMs = Summarize(samples.Select(x => x.CallbackMs)),
                    resourceSetupMs = Summarize(samples.Where(x => x.Submitted).Select(x => x.Draw.ResourceMs)),
                    copyEnqueueMs = Summarize(samples.Where(x => x.Submitted).Select(x => x.Draw.CopyEnqueueMs)),
                    drawEnqueueMs = Summarize(samples.Where(x => x.Submitted).Select(x => x.Draw.DrawEnqueueMs)),
                    hudMs = Summarize(samples.Where(x => x.Submitted).Select(x => x.Draw.HudMs)),
                    presentMs = Summarize(samples.Where(x => x.Submitted).Select(x => x.Draw.PresentMs)),
                    droppedLogSamples = Interlocked.Read(ref dropped) });
                output.Flush();
                samples.Clear(); gaps.Clear(); sourceGaps.Clear(); sourceRepeated = sourceNonMonotonic = 0; windowStart = now;
            }
            await foreach (var sample in queue.Reader.ReadAllAsync())
            {
                totalCallbacks++; if (sample.HasFrame) totalFrames++; if (sample.Submitted) totalSubmitted++;
                if (previousArrival != 0) gaps.Add((sample.Arrival - previousArrival) * 1000.0 / Stopwatch.Frequency);
                previousArrival = sample.Arrival;
                if (sample.SourceTicks is long source)
                {
                    if (previousSource is long previous)
                    {
                        long delta = source - previous;
                        if (delta > 0) sourceGaps.Add(delta / (double)TimeSpan.TicksPerMillisecond);
                        else if (delta == 0) sourceRepeated++; else sourceNonMonotonic++;
                    }
                    previousSource = source;
                }
                samples.Add(sample);
                long now = sample.Arrival + (long)(sample.CallbackMs * Stopwatch.Frequency / 1000);
                if (now - windowStart >= Stopwatch.Frequency || samples.Count >= 4096) Flush(now);
            }
            long end = Stopwatch.GetTimestamp(); Flush(end);
            Line(new { type = "stop", time = DateTimeOffset.Now, reason,
                elapsedSeconds = (double)(end - start) / Stopwatch.Frequency,
                totalCallbacks, totalFrames, totalSubmitted, droppedLogSamples = Interlocked.Read(ref dropped) });
            await output.FlushAsync();
        }
        catch (Exception e) { Error = e.Message; Program.Log("Performance logging failed: " + e); }
    }

    internal static object Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? new { count = 0, avg = 0.0, p95 = 0.0, max = 0.0 }
            : new { count = sorted.Length, avg = sorted.Average(), p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], max = sorted[^1] };
    }
    public void Complete(string stopReason) { reason = stopReason; queue.Writer.TryComplete(); }
    public void Dispose() { Complete(reason); worker.GetAwaiter().GetResult(); }
}
