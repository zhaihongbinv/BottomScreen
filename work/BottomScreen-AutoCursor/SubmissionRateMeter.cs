using System.Diagnostics;

namespace BottomScreen;

// Measures the supplied completed-frame count over elapsed time. The HUD supplies
// distinct source images successfully presented, not cached cursor/HUD redraws.
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
