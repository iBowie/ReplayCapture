using ReplayCapture.Core.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace ReplayCapture.Core.Capture;

/// <summary>
/// The single D3D11 device shared by every stage: capture (either backend — see
/// <see cref="Config.CaptureBackend"/>), NV12 conversion and NVENC.
/// <para>
/// Sharing one device is what keeps the pipeline zero-copy — a second device would force every
/// frame through a cross-device (and possibly cross-adapter) copy.
/// </para>
/// </summary>
public sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext ImmediateContext { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext VideoContext { get; }

    /// <summary>The same device projected as WinRT, which is what WGC's frame pool wants.</summary>
    public IDirect3DDevice WinRTDevice { get; }

    public D3DContext()
    {
        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        ];

        // BgraSupport: WGC hands back B8G8R8A8 surfaces.
        // VideoSupport: required before ID3D11VideoDevice can be queried for the NV12 conversion.
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        var result = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            flags,
            featureLevels,
            out var device,
            out var featureLevel,
            out var context);

        if (result.Failure)
            throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Description}");

        Device = device;
        ImmediateContext = context;

        // Capture callbacks, the pacing thread and the encode thread all touch this device, so the
        // driver must serialise access for us.
        using (var multithread = Device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            if (multithread is not null) multithread.SetMultithreadProtected(true);
            else Log.Warn("ID3D11Multithread unavailable; device access will not be driver-serialised.");
        }

        VideoDevice = Device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = ImmediateContext.QueryInterface<ID3D11VideoContext>();
        WinRTDevice = Direct3DInterop.CreateDirect3DDevice(Device);

        Log.Info($"D3D11 device created at feature level {featureLevel}.");
    }

    /// <summary>
    /// True once the GPU has been reset or removed (driver update, TDR, eGPU unplug). The pipeline
    /// watches this so it can rebuild rather than spin on a dead device.
    /// </summary>
    public bool IsDeviceLost
    {
        get
        {
            var reason = Device.DeviceRemovedReason;
            return reason.Failure;
        }
    }

    public void Dispose()
    {
        VideoContext.Dispose();
        VideoDevice.Dispose();
        ImmediateContext.Dispose();
        Device.Dispose();
    }
}
