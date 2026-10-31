using System.Runtime.InteropServices;
using System.Text;

namespace BottomScreen;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo { public int Size; public uint Flags; public nint Cursor; public Point Position; }
    [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CursorInfo value);
    [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagInitialize();
    [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagUninitialize();
    [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagShowSystemCursor(bool show);
    [StructLayout(LayoutKind.Sequential)] internal struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint CopyImage(nint image, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyCursor(nint cursor);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetIconInfo(nint cursor, out IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool DrawIconEx(nint dc, int x, int y, nint cursor, int width, int height, uint frame, nint brush, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] internal static extern int GetObject(nint value, int size, out BitmapInfo info);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    internal delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int mode, ref DevMode value);
    [DllImport("combase.dll")] internal static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, uint length, out nint value);
    [DllImport("combase.dll")] internal static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] internal static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("d3d11.dll")] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields; public int PositionX, PositionY; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels; public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    public static int RefreshRate(Screen screen)
    {
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>(), DeviceName = "", FormName = "" };
        return EnumDisplaySettings(screen.DeviceName, -1, ref mode) ? (int)mode.DisplayFrequency : 0;
    }

    public static Rectangle ClientCrop(nint hwnd, Size capture)
        => ClientCrop(hwnd, capture, out _);

    public static Rectangle ClientCrop(nint hwnd, Size capture, out Point captureOrigin)
    {
        captureOrigin = Point.Empty;
        if (!GetClientRect(hwnd, out var client)) return new Rectangle(Point.Empty, capture);
        var origin = Point.Empty;
        ClientToScreen(hwnd, ref origin);
        if (DwmGetWindowAttribute(hwnd, 9, out var outer, Marshal.SizeOf<Rect>()) != 0) GetWindowRect(hwnd, out outer);
        captureOrigin = new Point(outer.Left, outer.Top);
        // WGC window bounds correspond to DWM extended frame bounds, excluding the shadow.
        return Rectangle.Intersect(new Rectangle(origin.X - outer.Left, origin.Y - outer.Top,
            client.Right, client.Bottom), new Rectangle(Point.Empty, capture));
    }

    public static List<WindowChoice> Windows()
    {
        var list = new List<WindowChoice>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            var text = new StringBuilder(512);
            GetWindowText(hwnd, text, text.Capacity);
            if (text.Length == 0) return true;
            GetClientRect(hwnd, out var rect);
            if (rect.Right < 100 || rect.Bottom < 100) return true;
            string name = "";
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
            list.Add(new WindowChoice(hwnd, name, text.ToString()));
            return true;
        }, 0);
        return list.OrderByDescending(x => x.Process.Equals("cs2", StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.Title).ToList();
    }
}

internal sealed record WindowChoice(nint Handle, string Process, string Title)
{
    public override string ToString() => $"{Title}  ·  {Process}";
}
