using System.Diagnostics;

namespace BottomScreen;

// Counts successful Present calls over measured elapsed time; never substitutes monitor Hz.
internal sealed class SubmissionRateMeter
{
    private bool initialized;
    private long baseTimestamp;
    private long baseCount;
    public double? FramesPerSecond { get; private set; }

    public bool Observe(long completedFrames, long timestamp)
    {
        if (!initialized || timestamp < baseTimestamp || completedFrames < baseCount)
        {
            initialized = true; baseTimestamp = timestamp; baseCount = completedFrames;
            FramesPerSecond = null; return true;
        }
        long ticks = timestamp - baseTimestamp;
        if (ticks < Stopwatch.Frequency) return false;
        FramesPerSecond = (completedFrames - baseCount) * (double)Stopwatch.Frequency / ticks;
        baseTimestamp = timestamp; baseCount = completedFrames;
        return true;
    }
}
