using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BottomScreen;

internal sealed record CursorSprite(int Width, int Height, Point Hotspot, byte[] Black, byte[] White);
internal sealed record CursorFrame(CursorSprite Sprite, Point PositionInCrop);

internal sealed class CursorSpriteCache
{
    private nint lastHandle;
    private CursorSprite? sprite;

    public CursorFrame? Read(Rectangle sampledSourceScreen)
    {
        var cursor = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
        if (!Native.GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || cursor.Cursor == 0 || !sampledSourceScreen.Contains(cursor.Position)) return null;
        if (cursor.Cursor != lastHandle || sprite == null)
        {
            // If copying fails, leave the real cursor visible instead of hiding it.
            try { sprite = Capture(cursor.Cursor); lastHandle = cursor.Cursor; }
            catch { sprite = null; lastHandle = 0; return null; }
        }
        return new CursorFrame(sprite, new Point(cursor.Position.X - sampledSourceScreen.X, cursor.Position.Y - sampledSourceScreen.Y));
    }

    internal static CursorSprite Capture(nint cursor)
    {
        nint copy = Native.CopyImage(cursor, 2, 0, 0, 0); // IMAGE_CURSOR, independent copy
        if (copy == 0) throw new InvalidOperationException("无法复制原光标。");
        Native.IconInfo info = default;
        try
        {
            if (!Native.GetIconInfo(copy, out info)) throw new InvalidOperationException("无法读取光标样式。");
            nint bitmap = info.Color != 0 ? info.Color : info.Mask;
            if (bitmap == 0 || Native.GetObject(bitmap, Marshal.SizeOf<Native.BitmapInfo>(), out var dimensions) == 0)
                throw new InvalidOperationException("无法读取光标尺寸。");
            int width = dimensions.Width, height = Math.Abs(dimensions.Height) / (info.Color == 0 ? 2 : 1);
            if (width <= 0 || height <= 0 || width > 512 || height > 512) throw new InvalidOperationException("不支持的光标尺寸。");
            return new CursorSprite(width, height, new Point((int)info.HotspotX, (int)info.HotspotY),
                Rasterize(copy, width, height, Color.Black), Rasterize(copy, width, height, Color.White));
        }
        finally
        {
            if (info.Mask != 0) Native.DeleteObject(info.Mask);
            if (info.Color != 0) Native.DeleteObject(info.Color);
            Native.DestroyCursor(copy);
        }
    }

    private static byte[] Rasterize(nint cursor, int width, int height, Color background)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(background);
            nint dc = graphics.GetHdc();
            try { if (!Native.DrawIconEx(dc, 0, 0, cursor, width, height, 0, 0, 3)) throw new InvalidOperationException("无法绘制原光标。"); }
            finally { graphics.ReleaseHdc(dc); }
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * width * 4, width * 4);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
