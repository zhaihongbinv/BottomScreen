namespace BottomScreen;

internal sealed class RememberedGame
{
    public string Executable { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public double PanelInches { get; set; } = 31.5;
    public int TargetInches { get; set; } = 24;
    public bool Nearest { get; set; }
    public bool CenterFourThree { get; set; }
    public bool ShowFps { get; set; } = true;
    public string Monitor { get; set; } = "";
    public string MonitorDeviceId { get; set; } = "";
    public long LastUsedFileTimeUtc { get; set; }

    internal string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
        ? Path.GetFileNameWithoutExtension(Executable) : ProcessName;
}

internal static class AutoResumeRules
{
    internal const int DefaultDelaySeconds = 5;
    internal const int MaximumDelaySeconds = 120;

    internal static int ClampDelay(int seconds) => Math.Clamp(seconds, 0, MaximumDelaySeconds);

    internal static bool Matches(RememberedGame? game, SourceProcess process)
    {
        if (game == null || string.IsNullOrWhiteSpace(game.Executable)) return false;
        try { return string.Equals(process.Key, Path.GetFullPath(game.Executable).ToUpperInvariant(), StringComparison.Ordinal); }
        catch { return false; }
    }

    // Suppression must only cover the process instance the user stopped. A restarted
    // game has a new creation time and is allowed to resume automatically again.
    internal static bool IsSuppressed(SourceProcess process, string? key, long startedFileTimeUtc)
        => key != null && process.StartedFileTimeUtc == startedFileTimeUtc &&
           string.Equals(process.Key, key, StringComparison.Ordinal);
}

internal sealed class AutoResumeCountdown
{
    private long since;

    internal bool Pending { get; private set; }

    internal int RemainingSeconds(long now, int delaySeconds)
        => Pending ? Math.Max(0, AutoResumeRules.ClampDelay(delaySeconds) - (int)((now - since) / 1000)) : 0;

    // Returns true once the remembered game has stayed the candidate for the full delay.
    internal bool Observe(bool candidate, long now, int delaySeconds)
    {
        if (!candidate) { Pending = false; return false; }
        int delay = AutoResumeRules.ClampDelay(delaySeconds);
        if (!Pending) { Pending = true; since = now; return delay == 0; }
        if (now - since < delay * 1000L) return false;
        Pending = false;
        return true;
    }

    internal void Reset() => Pending = false;
}
