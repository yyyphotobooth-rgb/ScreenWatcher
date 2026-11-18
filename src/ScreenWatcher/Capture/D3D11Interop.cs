using System.Runtime.InteropServices;

namespace ScreenWatcher.Capture;

/// <summary>
/// Minimal raw Direct3D 11 / WinRT interop for Windows.Graphics.Capture: create a device, wrap it
/// for WinRT, create a capture item for a window, and copy part of a captured surface into a CPU
/// staging texture. Only our own GPU objects are touched; nothing here talks to the game process.
/// Vtable slot numbers follow d3d11.h.
/// </summary>
internal static unsafe class D3D11Interop
{
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    const int D3D_DRIVER_TYPE_HARDWARE = 1;
    const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    const uint D3D11_SDK_VERSION = 7;
    const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    const uint D3D11_USAGE_STAGING = 3;
    const uint D3D11_CPU_ACCESS_READ = 0x20000;
    const uint D3D11_MAP_READ = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappedSubresource { public IntPtr Data; public uint RowPitch, DepthPitch; }

    [StructLayout(LayoutKind.Sequential)]
    struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    public static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero) Marshal.Release(p);
        p = IntPtr.Zero;
    }

    /// <summary>Creates a hardware D3D11 device; returns (device, immediate context, WinRT IInspectable device).</summary>
    public static (IntPtr Device, IntPtr Context, IntPtr WinRtDevice) CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out var device, out _, out var context));
        IntPtr dxgi = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IID_IDXGIDevice, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var winrt));
            return (device, context, winrt);
        }
        catch
        {
            Release(ref context);
            Release(ref device);
            throw;
        }
        finally { Release(ref dxgi); }
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow. Returns an owned IGraphicsCaptureItem pointer.</summary>
    public static IntPtr CreateCaptureItemForWindow(IntPtr hwnd)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hs));
        IntPtr factory = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, in IID_IGraphicsCaptureItemInterop, out factory));
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(factory, 3);
            IntPtr item;
            Guid iid = IID_IGraphicsCaptureItem;
            Marshal.ThrowExceptionForHR(create(factory, hwnd, &iid, &item));
            return item;
        }
        finally
        {
            Release(ref factory);
            WindowsDeleteString(hs);
        }
    }

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface(ID3D11Texture2D) on a WinRT surface pointer.</summary>
    public static IntPtr GetTexture(IntPtr surface)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in IID_IDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, 3);
            IntPtr tex;
            Guid iid = IID_ID3D11Texture2D;
            Marshal.ThrowExceptionForHR(get(access, &iid, &tex));
            return tex;
        }
        finally { Marshal.Release(access); }
    }

    /// <summary>ID3D11Device::CreateTexture2D (slot 5) for a CPU-readable BGRA staging texture.</summary>
    public static IntPtr CreateStaging(IntPtr device, int width, int height)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, Usage = D3D11_USAGE_STAGING, CpuAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        var create = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Slot(device, 5);
        IntPtr tex;
        Marshal.ThrowExceptionForHR(create(device, &desc, IntPtr.Zero, &tex));
        return tex;
    }

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion (slot 46): src rect -> dst (0,0).</summary>
    public static void CopyRegion(IntPtr context, IntPtr dst, IntPtr src, int x, int y, int w, int h)
    {
        var box = new Box { Left = (uint)x, Top = (uint)y, Front = 0, Right = (uint)(x + w), Bottom = (uint)(y + h), Back = 1 };
        var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(context, 46);
        copy(context, dst, 0, 0, 0, 0, src, 0, &box);
    }

    /// <summary>ID3D11DeviceContext::Map (slot 14) for reading.</summary>
    public static MappedSubresource Map(IntPtr context, IntPtr resource)
    {
        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Slot(context, 14);
        MappedSubresource m;
        Marshal.ThrowExceptionForHR(map(context, resource, 0, D3D11_MAP_READ, 0, &m));
        return m;
    }

    /// <summary>ID3D11DeviceContext::Unmap (slot 15).</summary>
    public static void Unmap(IntPtr context, IntPtr resource)
    {
        var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(context, 15);
        unmap(context, resource, 0);
    }
}
