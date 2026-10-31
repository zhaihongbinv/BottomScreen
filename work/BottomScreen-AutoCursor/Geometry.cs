namespace BottomScreen;

internal static class Geometry
{
    public static Rectangle SourceCrop(Rectangle client, bool centerFourThree)
    {
        if (!centerFourThree || (long)client.Width * 3 <= (long)client.Height * 4) return client;
        int width = (int)Math.Round(client.Height * 4.0 / 3);
        return new Rectangle(client.X + (client.Width - width) / 2, client.Y, width, client.Height);
    }

    public static Rectangle Target(Size output, double panelInches, double targetInches)
    {
        if (output.Width <= 0 || output.Height <= 0 || panelInches < targetInches || targetInches <= 0)
            throw new ArgumentOutOfRangeException(nameof(panelInches), "实体屏幕尺寸必须不小于模拟尺寸。");
        // Physical 16:9 area, including when the desktop happens to use another aspect ratio.
        double diagonal = Math.Sqrt((double)output.Width * output.Width + (double)output.Height * output.Height);
        double height = diagonal * targetInches / panelInches / Math.Sqrt(337) * 9;
        int h = (int)Math.Round(height);
        int w = (int)Math.Round(height * 16 / 9);
        if (w > output.Width || h > output.Height)
            throw new InvalidOperationException("当前输出比例无法容纳所选的16:9区域，请使用16:9桌面分辨率。");
        return new Rectangle((output.Width - w) / 2, output.Height - h, w, h);
    }
}
