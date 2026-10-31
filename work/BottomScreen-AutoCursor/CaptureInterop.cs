using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace BottomScreen;

internal static class CaptureInterop
{
    public static unsafe GraphicsCaptureItem ForWindow(nint hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Guid interopId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        Guid itemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(name, (uint)name.Length, out var hstring));
        nint factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstring, interopId, out factory));
            var method = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(method(factory, hwnd, &itemId, &item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            Native.WindowsDeleteString(hstring);
        }
    }

    public static IDirect3DDevice Device(nint dxgi)
    {
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var pointer));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    public static unsafe nint Texture(IDirect3DSurface surface)
    {
        using var inspectable = WinRT.MarshalInspectable<IDirect3DSurface>.CreateMarshaler(surface);
        Guid accessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        Guid textureId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable.ThisPtr, in accessId, out var access));
        try
        {
            nint texture = 0;
            var method = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            Marshal.ThrowExceptionForHR(method(access, &textureId, &texture));
            return texture;
        }
        finally { Marshal.Release(access); }
    }
}
