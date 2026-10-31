using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BottomScreen;

internal sealed record SourceProcess(uint Id, string Executable, long StartedFileTimeUtc)
{
    public string Key => Path.GetFullPath(Executable).ToUpperInvariant();
    public static SourceProcess ForWindow(nint hwnd)
    {
        if (!Native.IsWindow(hwnd)) throw new InvalidOperationException("所选窗口已关闭，请刷新。");
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        using var handle = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION only
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取所选程序的信息。");
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length) ||
            !GetProcessTimes(handle, out long created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确定所选程序的路径或启动时间。");
        Native.GetWindowThreadProcessId(hwnd, out uint currentPid);
        if (!Native.IsWindow(hwnd) || currentPid != pid)
            throw new InvalidOperationException("所选窗口已变化，请刷新后重选。");
        return new SourceProcess(pid, Path.GetFullPath(path.ToString()), created);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
}

internal static class GameHdrController
{
    internal static bool NeedsRestart(SourceProcess process, Settings settings)
        => settings.GameHdrRestartAfterUtc.TryGetValue(process.Key, out long applied) && process.StartedFileTimeUtc <= applied;

    internal static AutoHdrPreference Apply(SourceProcess process, bool allow, Settings settings)
    {
        var preference = new AutoHdrPreference(allow, process.Executable);
        if (preference.Error == null && preference.Changed)
            settings.GameHdrRestartAfterUtc[process.Key] = DateTime.UtcNow.ToFileTimeUtc();
        // Retain the marker until a later process is actually observed. Restarting
        // BottomScreen must never falsely clear a running game's restart warning.
        if (preference.Error == null && !NeedsRestart(process, settings))
            settings.GameHdrRestartAfterUtc.Remove(process.Key);
        return preference;
    }
}
