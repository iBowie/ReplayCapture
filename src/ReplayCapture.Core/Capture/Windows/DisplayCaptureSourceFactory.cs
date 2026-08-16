using ReplayCapture.Core.Config;

using D3DTexture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ReplayCapture.Core.Capture;

/// <summary>Builds the capture backend a display should use. See <see cref="CaptureBackend"/> for the trade-offs.</summary>
public static class DisplayCaptureSourceFactory
{
    public static IDisplayCaptureSource<D3DTexture2D> Create(CaptureBackend backend, D3DContext d3d, DisplayInfo display) => backend switch
    {
        CaptureBackend.Wgc => new WgcDisplayCaptureSource(d3d, display),
        CaptureBackend.Dxgi => new DxgiDisplayCaptureSource(d3d, display),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };
}
