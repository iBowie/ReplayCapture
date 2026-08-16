using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Config;

using D3DTexture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ReplayCapture.Core.Encoders;

/// <summary>Builds the video encoder a display should use. See <see cref="VideoEncoderBackend"/> for the trade-offs.</summary>
public static class VideoEncoderFactory
{
    public static IVideoEncoder<D3DTexture2D> Create(
        VideoEncoderBackend backend, D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps) => backend switch
    {
        VideoEncoderBackend.Nvenc => new NvencVideoEncoder(d3d, width, height, framesPerSecond, bitrateMbps),
        VideoEncoderBackend.Amf => new AmfVideoEncoder(d3d, width, height, framesPerSecond, bitrateMbps),
        VideoEncoderBackend.X264 => new X264VideoEncoder(d3d, width, height, framesPerSecond, bitrateMbps),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };
}
