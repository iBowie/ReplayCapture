using ReplayCapture.Core.Config;

namespace ReplayCapture.Core.Capture;

/// <summary>Builds the capture backend a display should use. See <see cref="CaptureBackend"/> for the trade-offs.</summary>
public static class DisplayCaptureSourceFactory
{
    public static IDisplayCaptureSource Create(CaptureBackend backend, D3DContext d3d, DisplayInfo display) => backend switch
    {
        CaptureBackend.Wgc => new WgcDisplayCaptureSource(d3d, display),
        CaptureBackend.Dxgi => new DxgiDisplayCaptureSource(d3d, display),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };
}
