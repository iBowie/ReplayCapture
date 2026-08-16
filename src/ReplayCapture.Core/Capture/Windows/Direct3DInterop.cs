using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ReplayCapture.Core.Audio.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace ReplayCapture.Core.Capture;

/// <summary>
/// The COM plumbing that joins Windows.Graphics.Capture (a WinRT API) to Direct3D 11 (a classic COM
/// API). None of these bridges are projected into C#, so they have to be declared by hand.
/// </summary>
internal static partial class Direct3DInterop
{
    /// <summary>IID of the WinRT <c>GraphicsCaptureItem</c> runtime class.</summary>
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// Lets us build a <see cref="GraphicsCaptureItem"/> from an HMONITOR or HWND. WinRT itself
    /// only exposes the interactive picker; the interop interface is the non-interactive route.
    /// <para>
    /// Declared as <c>[GeneratedComInterface]</c>, not classic <c>[ComImport]</c>: under Native AOT,
    /// CsWinRT's own <c>IObjectReference.AsInterface&lt;T&gt;()</c> throws
    /// <c>NotSupportedException</c> ("COM Interop requires ComWrapper instance registered for
    /// marshalling") for an arbitrary hand-declared classic COM interface, since AOT ships no
    /// built-in COM RCW system and nothing registers a global <c>ComWrappers</c> for types CsWinRT
    /// doesn't itself know about. The fix is the same one used throughout <c>Audio/Interop</c>: get
    /// the raw activation-factory pointer via <see cref="WinRT.IObjectReference.GetRef"/>,
    /// <c>QueryInterface</c> it by hand, and wrap the result via
    /// <see cref="ComInterop.WrapAndRelease{T}"/> — bypassing CsWinRT's marshaller entirely for this
    /// one non-projected interface.
    /// </para>
    /// </summary>
    [GeneratedComInterface]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    internal partial interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    /// <summary>
    /// IID of <c>IDirect3DDxgiInterfaceAccess</c>, which unwraps the DXGI/D3D11 object living
    /// inside a WinRT surface.
    /// <para>
    /// This one is invoked through its vtable rather than declared as a <c>[ComImport]</c>
    /// interface. Objects handed out by CsWinRT are projected WinRT objects, not classic COM RCWs,
    /// so a plain C# cast to a <c>[ComImport]</c> interface throws
    /// <see cref="InvalidCastException"/> — the interface has to be reached by an explicit
    /// QueryInterface on the native pointer.
    /// </para>
    /// </summary>
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>Vtable slot of <c>GetInterface</c>: after the three IUnknown methods.</summary>
    private const int GetInterfaceSlot = 3;

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>Creates a capture item for a monitor, identified by its HMONITOR.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        using var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var factoryPtr = factory.GetRef();

        IGraphicsCaptureItemInterop interop;
        try
        {
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factoryPtr, in interopIid, out var interopPtr));
            interop = ComInterop.WrapAndRelease<IGraphicsCaptureItemInterop>(interopPtr);
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }

        var iid = GraphicsCaptureItemIid;
        var abi = interop.CreateForMonitor(monitorHandle, in iid);
        if (abi == IntPtr.Zero)
            throw new InvalidOperationException($"CreateForMonitor returned null for HMONITOR 0x{monitorHandle:X}.");

        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            // FromAbi takes its own reference; release the one the interop call handed us.
            Marshal.Release(abi);
        }
    }

    /// <summary>Wraps an <see cref="ID3D11Device"/> as the WinRT device the frame pool expects.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var abi);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>
    /// Pulls the underlying <see cref="ID3D11Texture2D"/> out of a captured frame's surface.
    /// The texture stays owned by the frame pool — it must not outlive the frame.
    /// </summary>
    public static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var inspectable = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = DxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in accessIid, out var access));

            try
            {
                var textureIid = typeof(ID3D11Texture2D).GUID;
                IntPtr texture;

                var vtable = *(void***)access;
                var getInterface =
                    (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[GetInterfaceSlot];

                Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));

                // GetInterface returns an AddRef'd pointer, so the Vortice wrapper takes ownership
                // and the caller is responsible for disposing it.
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }
}
