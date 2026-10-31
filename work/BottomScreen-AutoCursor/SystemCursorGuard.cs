using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace BottomScreen;

// The helper owns the temporary display-only hide. A short lease and an independent
// foreground check restore the desktop cursor if the UI hangs, exits or loses focus.
internal sealed class SystemCursorGuard : IDisposable
{
    internal const int LeaseMilliseconds = 750;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle stop, ready, done;
    private readonly Process worker;
    private readonly long started = Environment.TickCount64;
    private bool disposed;

    public SystemCursorGuard(int? parentProcessId = null)
    {
        string name = "Local\\BottomScreen.Cursor." + Guid.NewGuid().ToString("N");
        mapping = MemoryMappedFile.CreateNew(name, 64);
        view = mapping.CreateViewAccessor();
        stop = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".stop");
        ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
        done = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".done");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--cursor-guard");
        start.ArgumentList.Add((parentProcessId ?? Environment.ProcessId).ToString());
        start.ArgumentList.Add(name);
        try { worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动光标恢复保护进程。"); }
        catch { view.Dispose(); mapping.Dispose(); stop.Dispose(); ready.Dispose(); done.Dispose(); throw; }
    }

    public bool IsReady => ready.WaitOne(0) && view.ReadInt32(20) == 1;
    public bool IsHidden => view.ReadInt32(28) == 1;
    internal bool HasRestoredAndExited => done.WaitOne(0) && worker.HasExited;
    internal string DebugStatus => $"ready={IsReady}, hidden={IsHidden}, workerForeground={view.ReadInt64(48)}, flags={view.ReadInt32(56)}, age={Environment.TickCount64 - view.ReadInt64(8)}";

    public void Pulse(nint source, Rectangle sampledSourceScreen)
    {
        if (disposed) return;
        if (worker.HasExited || view.ReadInt32(20) < 0)
            throw new InvalidOperationException("自动隐藏光标服务已停止；已恢复系统光标。");
        if (!IsReady && Environment.TickCount64 - started > 5000)
            throw new InvalidOperationException("光标恢复保护进程启动超时。");
        if (IsReady && unchecked((uint)(Environment.TickCount - view.ReadInt32(60))) > 1500)
            throw new InvalidOperationException("光标恢复保护进程无响应；已停止并恢复系统光标。");
        view.Write(0, source.ToInt64());
        view.Write(32, sampledSourceScreen.X); view.Write(36, sampledSourceScreen.Y);
        view.Write(40, sampledSourceScreen.Width); view.Write(44, sampledSourceScreen.Height);
        view.Write(8, Environment.TickCount64);
        view.Write(16, source != 0 && sampledSourceScreen.Width > 0 && sampledSourceScreen.Height > 0 ? 1 : 0);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        view.Write(16, 0);
        stop.Set();
        bool finished = done.WaitOne(1000);
        if (!finished || worker.HasExited || view.ReadInt32(20) < 0)
        {
            // An exited helper normally restored in finally; repeat harmlessly as
            // a fallback for abnormal helper termination or failed shutdown.
            RestoreSystemCursor();
        }
        worker.Dispose(); done.Dispose(); ready.Dispose(); stop.Dispose(); view.Dispose(); mapping.Dispose();
    }

    internal static void RestoreSystemCursor()
    {
        if (!Native.MagInitialize()) return;
        try { Native.MagShowSystemCursor(true); }
        finally { Native.MagUninitialize(); }
    }

    internal static bool ShouldHide(nint source, nint foreground, Rectangle crop, Point cursor,
        uint cursorFlags, long now, long heartbeat, bool requested)
        => requested && source != 0 && source == foreground && crop.Contains(cursor) &&
            (cursorFlags & 1) != 0 && now >= heartbeat && now - heartbeat <= LeaseMilliseconds;
}

internal static class CursorGuardWorker
{
    internal static void Run(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out int parentId)) return;
        bool initialized = false, hidden = false;
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? done = null;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var mapping = MemoryMappedFile.OpenExisting(args[2]);
            using var accessor = mapping.CreateViewAccessor(); view = accessor;
            using var stop = EventWaitHandle.OpenExisting(args[2] + ".stop");
            using var ready = EventWaitHandle.OpenExisting(args[2] + ".ready");
            using var completion = EventWaitHandle.OpenExisting(args[2] + ".done"); done = completion;
            try
            {
                initialized = Native.MagInitialize();
                view.Write(60, Environment.TickCount);
                view.Write(20, initialized ? 1 : -1);
                ready.Set();
                if (!initialized) return;
                while (!stop.WaitOne(10) && !parent.HasExited)
                {
                    view.Write(60, Environment.TickCount);
                    long heartbeat = view.ReadInt64(8);
                    var cursor = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
                    bool readCursor = Native.GetCursorInfo(ref cursor);
                    nint foreground = Native.GetForegroundWindow();
                    view.Write(48, foreground.ToInt64()); view.Write(56, readCursor ? (int)cursor.Flags : -1);
                    bool hide = readCursor && SystemCursorGuard.ShouldHide((nint)view.ReadInt64(0), foreground,
                        new Rectangle(view.ReadInt32(32), view.ReadInt32(36), view.ReadInt32(40), view.ReadInt32(44)),
                        cursor.Position, cursor.Flags, Environment.TickCount64, heartbeat, view.ReadInt32(16) == 1);
                    if (hide == hidden) continue;
                    if (!Native.MagShowSystemCursor(!hide)) { view.Write(20, -2); break; }
                    hidden = hide;
                    view.Write(28, hidden ? 1 : 0);
                }
            }
            finally
            {
                if (initialized)
                {
                    bool restored = Native.MagShowSystemCursor(true);
                    if (!restored) view.Write(20, -3);
                    Native.MagUninitialize(); initialized = false;
                }
                view.Write(28, 0);
                done.Set();
            }
        }
        catch
        {
            if (initialized) { Native.MagShowSystemCursor(true); Native.MagUninitialize(); }
            // If resources vanished because the parent died, restoring is still
            // required. Never show UI or take focus from the user's game here.
        }
    }
}
