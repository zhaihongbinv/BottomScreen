using System.Drawing.Imaging;
using System.Diagnostics;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using Color = System.Drawing.Color;
using static Vortice.Direct3D11.D3D11;

namespace BottomScreen;

internal sealed class GpuRenderer : IDisposable
{
    internal readonly ID3D11Device Device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11RenderTargetView target;
    private readonly IDCompositionDevice composition;
    private readonly IDCompositionTarget compositionTarget;
    private readonly IDCompositionVisual visual;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11PixelShader cursorPixel;
    private readonly ID3D11Buffer cursorConstants;
    private ID3D11Texture2D? cursorBlackTexture, cursorWhiteTexture;
    private ID3D11ShaderResourceView? cursorBlackView, cursorWhiteView;
    private CursorSprite? currentCursorSprite;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11Buffer constants;
    private ID3D11Texture2D? image;
    private ID3D11ShaderResourceView? imageView;
    private Texture2DDescription imageDescription;
    private readonly Size outputSize;
    private bool disposed;
    private readonly bool showFps;
    private readonly int refreshHz;
    private readonly SubmissionRateMeter rateMeter = new();
    private long lastPresentedImageVersion;
    private ID3D11Texture2D? hudTexture;
    private ID3D11ShaderResourceView? hudView;
    public double? PictureFps => rateMeter.FramesPerSecond;
    public long PictureFrameCount { get; private set; }
    public string HudText { get; private set; } = "";
    public Rectangle HudBounds { get; private set; }
    public string AdapterName { get; }
    public uint MaximumFrameLatency { get; }
    public long ImageCopies { get; private set; }

    public GpuRenderer(nint hwnd, Size output, bool nearest, int refreshHz = 0, bool showFps = true)
    {
        outputSize = output;
        this.refreshHz = refreshHz; this.showFps = showFps;
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out Device, out context).CheckError();
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        using (var latencyDevice = Device.QueryInterface<IDXGIDevice1>())
        {
            latencyDevice.SetMaximumFrameLatency(1).CheckError();
            latencyDevice.GetMaximumFrameLatency(out uint maximum).CheckError();
            MaximumFrameLatency = maximum;
        }
        using var adapter = dxgi.GetAdapter();
        AdapterName = adapter.Description.Description;
        using var factory = adapter.GetParent<IDXGIFactory2>();
        swapChain = factory.CreateSwapChainForComposition(Device, new SwapChainDescription1
        {
            Width = (uint)output.Width, Height = (uint)output.Height,
            Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch, AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied
        });
        using (var buffer = swapChain.GetBuffer<ID3D11Texture2D>(0)) target = Device.CreateRenderTargetView(buffer);
        composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
        composition.CreateTargetForHwnd(hwnd, true, out compositionTarget).CheckError();
        visual = composition.CreateVisual();
        visual.SetContent(swapChain).CheckError();
        compositionTarget.SetRoot(visual).CheckError();
        composition.Commit().CheckError();

        const string shader = """
            cbuffer Crop : register(b0) { float4 crop; }
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            V VS(uint id : SV_VertexID) {
                V o;
                float2 p = float2((id << 1) & 2, id & 2);
                o.pos = float4(p * float2(2,-2) + float2(-1,1), 0, 1);
                o.uv = p; return o;
            }
            Texture2D source : register(t0); SamplerState sampleState : register(s0);
            float4 PS(V v) : SV_TARGET { return float4(source.Sample(sampleState, crop.xy + v.uv * crop.zw).rgb, 1); }
            """;
        var vs = Vortice.D3DCompiler.Compiler.Compile(shader, "VS", "BottomScreen.hlsl", "vs_5_0");
        var ps = Vortice.D3DCompiler.Compiler.Compile(shader, "PS", "BottomScreen.hlsl", "ps_5_0");
        vertex = Device.CreateVertexShader(vs.Span);
        pixel = Device.CreatePixelShader(ps.Span);
        const string cursorShader = """
            cbuffer CursorData : register(b0) { float4 crop; float4 destination; }
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            Texture2D scene : register(t0); Texture2D cursorBlack : register(t1); Texture2D cursorWhite : register(t2);
            SamplerState sampleState : register(s0);
            float4 PS(V v) : SV_TARGET {
                float2 q = (v.pos.xy - destination.xy) / destination.zw;
                if (any(q < 0) || any(q >= 1)) discard;
                float3 background = scene.Sample(sampleState, crop.xy + q * crop.zw).rgb;
                float3 black = cursorBlack.Sample(sampleState, v.uv).rgb;
                float3 white = cursorWhite.Sample(sampleState, v.uv).rgb;
                // Two native renderings retain alpha and monochrome XOR/invert
                // cursors, including hotspots and transparent edge pixels.
                return float4(saturate(black + background * (white - black)), 1);
            }
            """;
        var cps = Vortice.D3DCompiler.Compiler.Compile(cursorShader, "PS", "Cursor.hlsl", "ps_5_0");
        cursorPixel = Device.CreatePixelShader(cps.Span);
        cursorConstants = Device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Default));
        sampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = nearest ? Filter.MinMagMipPoint : Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunction.Never
        });
        constants = Device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        Clear();
        swapChain.Present(0, PresentFlags.None).CheckError();
    }

    private void Clear()
    {
        context.OMSetRenderTargets(target);
        context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
    }

    internal (double ResourceMs, double CopyMs) StoreImage(ID3D11Texture2D texture, bool profile = false)
    {
        long begin = profile ? Stopwatch.GetTimestamp() : 0;
        var desc = texture.Description;
        if (image == null || imageDescription.Width != desc.Width || imageDescription.Height != desc.Height || imageDescription.Format != desc.Format)
        {
            context.PSSetShaderResource(0, null!);
            imageView?.Dispose(); image?.Dispose();
            desc.BindFlags = BindFlags.ShaderResource;
            desc.Usage = ResourceUsage.Default;
            desc.CPUAccessFlags = CpuAccessFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            desc.MipLevels = 1; desc.ArraySize = 1;
            image = Device.CreateTexture2D(desc);
            imageView = Device.CreateShaderResourceView(image);
            imageDescription = desc;
        }
        context.PSSetShaderResource(0, null!);
        long resource = profile ? Stopwatch.GetTimestamp() : 0;
        context.CopyResource(image!, texture);
        ImageCopies++;
        long copy = profile ? Stopwatch.GetTimestamp() : 0;
        double ms = 1000.0 / Stopwatch.Frequency;
        return profile ? ((resource - begin) * ms, (copy - resource) * ms) : default;
    }

    internal DrawTiming Draw(ID3D11Texture2D? texture, Rectangle crop, Rectangle destination, bool profile = false, CursorFrame? cursor = null)
    {
        var stored = texture != null ? StoreImage(texture, profile) : default;
        if (image == null) return default;
        var desc = imageDescription;
        long copy = profile ? Stopwatch.GetTimestamp() : 0;
        Clear();
        var uv = new Vector4((float)crop.X / desc.Width, (float)crop.Y / desc.Height,
            (float)crop.Width / desc.Width, (float)crop.Height / desc.Height);
        context.UpdateSubresource(uv, constants);
        context.RSSetViewport(new Viewport(destination.X, destination.Y, destination.Width, destination.Height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetShader(pixel);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetShaderResource(0, imageView!);
        context.PSSetSampler(0, sampler);
        context.Draw(3, 0);
        if (cursor != null) DrawCursor(cursor, uv, crop, destination);
        long draw = profile ? Stopwatch.GetTimestamp() : 0;
        bool sampleChanged = rateMeter.Observe(PictureFrameCount, Stopwatch.GetTimestamp());
        if (showFps && destination.Top >= 32)
        {
            if (sampleChanged || hudView == null) UpdateHud(destination);
            DrawHud();
        }
        // WGC drives presentation. No CPU pixel copy, busy polling, or software frame limiter.
        long hud = profile ? Stopwatch.GetTimestamp() : 0;
        swapChain.Present(0, PresentFlags.None).CheckError();
        long present = profile ? Stopwatch.GetTimestamp() : 0;
        // Count a source image once, only after a successful Present. Cached
        // redraws for pointer movement or HUD refresh are not game updates.
        if (lastPresentedImageVersion != ImageCopies)
        {
            PictureFrameCount++;
            lastPresentedImageVersion = ImageCopies;
        }
        if (!profile) return default;
        double ms = 1000.0 / Stopwatch.Frequency;
        return new DrawTiming(stored.ResourceMs, stored.CopyMs,
            (draw - copy) * ms, (hud - draw) * ms, (present - hud) * ms);
    }

    private void UpdateHud(Rectangle destination)
    {
        // CPU text rasterization is limited to about once a second, never the game frame size.
        float scale = outputSize.Height / 1080f;
        float primarySize = Math.Clamp(18 * scale, 12, 36);
        float secondarySize = Math.Clamp(11 * scale, 11, 22);
        int width = Math.Min(outputSize.Width - 32, (int)Math.Max(360, 560 * scale));
        int height = Math.Min(destination.Top - 12, (int)Math.Ceiling(primarySize + secondarySize + 16 * scale));
        if (width <= 0 || height < 20) return;
        HudBounds = new Rectangle((outputSize.Width - width) / 2, (destination.Top - height) / 2, width, height);
        string rate = rateMeter.FramesPerSecond is double fps ? $"{fps:F1} FPS" : "采样中…";
        string refresh = refreshHz > 0 ? $"   |   刷新 {refreshHz} Hz" : "";
        HudText = $"画面更新 {rate}{refresh}";
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var primaryFont = new Font("Microsoft YaHei UI", primarySize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var secondaryFont = new Font("Microsoft YaHei UI", secondarySize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var secondaryBrush = new SolidBrush(Color.FromArgb(176, 176, 176)))
        {
            graphics.Clear(Color.Black);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            bool twoLines = height >= primarySize + secondarySize + 4;
            float topHeight = twoLines ? primarySize + 6 * scale : height;
            graphics.DrawString(HudText, primaryFont, Brushes.White, new RectangleF(0, 0, width, topHeight), format);
            if (twoLines)
                graphics.DrawString("约1秒统计 · 仅计不同源帧 / 非面板实显测量", secondaryFont, secondaryBrush,
                    new RectangleF(0, topHeight, width, height - topHeight), format);
        }
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            context.PSSetShaderResource(0, null!);
            hudView?.Dispose(); hudTexture?.Dispose();
            hudTexture = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, new SubresourceData(data.Scan0, (uint)data.Stride, 0));
            hudView = Device.CreateShaderResourceView(hudTexture);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void DrawHud()
    {
        if (hudView == null) return;
        context.UpdateSubresource(new Vector4(0, 0, 1, 1), constants);
        context.RSSetViewport(new Viewport(HudBounds.X, HudBounds.Y, HudBounds.Width, HudBounds.Height));
        context.PSSetShaderResource(0, hudView);
        context.PSSetShader(pixel);
        context.PSSetConstantBuffer(0, constants);
        context.Draw(3, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorUniforms { public Vector4 Crop, Destination; }

    private void DrawCursor(CursorFrame cursor, Vector4 uv, Rectangle crop, Rectangle destination)
    {
        var sprite = cursor.Sprite;
        if (!ReferenceEquals(sprite, currentCursorSprite))
        {
            context.PSSetShaderResource(1, null!); context.PSSetShaderResource(2, null!);
            cursorBlackView?.Dispose(); cursorWhiteView?.Dispose();
            cursorBlackTexture?.Dispose(); cursorWhiteTexture?.Dispose();
            cursorBlackTexture = CreateCursorTexture(sprite, sprite.Black);
            cursorWhiteTexture = CreateCursorTexture(sprite, sprite.White);
            cursorBlackView = Device.CreateShaderResourceView(cursorBlackTexture);
            cursorWhiteView = Device.CreateShaderResourceView(cursorWhiteTexture);
            currentCursorSprite = sprite;
        }
        float sx = destination.Width / (float)crop.Width, sy = destination.Height / (float)crop.Height;
        context.UpdateSubresource(new CursorUniforms { Crop = uv, Destination = new Vector4(destination.X, destination.Y, destination.Width, destination.Height) }, cursorConstants);
        context.RSSetViewport(new Viewport(destination.X + (cursor.PositionInCrop.X - sprite.Hotspot.X) * sx,
            destination.Y + (cursor.PositionInCrop.Y - sprite.Hotspot.Y) * sy, sprite.Width * sx, sprite.Height * sy));
        context.PSSetShader(cursorPixel); context.PSSetConstantBuffer(0, cursorConstants);
        context.PSSetShaderResource(0, imageView!);
        context.PSSetShaderResource(1, cursorBlackView!); context.PSSetShaderResource(2, cursorWhiteView!);
        context.Draw(3, 0);
    }

    private unsafe ID3D11Texture2D CreateCursorTexture(CursorSprite sprite, byte[] bytes)
    {
        fixed (byte* pointer = bytes)
            return Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)sprite.Width, Height = (uint)sprite.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, new SubresourceData((nint)pointer, (uint)sprite.Width * 4, 0));
    }

    internal unsafe Bitmap Snapshot()
    {
        using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = back.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = Device.CreateTexture2D(desc);
        context.CopyResource(staging, back);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var bitmap = new Bitmap(outputSize.Width, outputSize.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, outputSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < outputSize.Height; y++)
                Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch, (byte*)data.Scan0 + y * data.Stride,
                    data.Stride, outputSize.Width * 4);
        }
        finally { bitmap.UnlockBits(data); context.Unmap(staging, 0); }
        return bitmap;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        context.ClearState(); context.Flush();
        hudView?.Dispose(); hudTexture?.Dispose();
        cursorBlackView?.Dispose(); cursorWhiteView?.Dispose(); cursorBlackTexture?.Dispose(); cursorWhiteTexture?.Dispose();
        cursorConstants.Dispose(); cursorPixel.Dispose();
        imageView?.Dispose(); image?.Dispose(); constants.Dispose(); sampler.Dispose();
        pixel.Dispose(); vertex.Dispose(); visual.Dispose(); compositionTarget.Dispose(); composition.Dispose();
        target.Dispose(); swapChain.Dispose(); context.Dispose(); Device.Dispose();
    }
}
