using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Config;

namespace ReplayCapture.Core.Encoders;

/// <summary>Builds the video encoder a display should use. See <see cref="VideoEncoderBackend"/> for the trade-offs.</summary>
public static class VideoEncoderFactory
{
    public static IVideoEncoder Create(
        VideoEncoderBackend backend, D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps) => backend switch
    {
        VideoEncoderBackend.Nvenc => new NvencVideoEncoder(d3d, width, height, framesPerSecond, bitrateMbps),
        VideoEncoderBackend.Amf => new AmfVideoEncoder(d3d, width, height, framesPerSecond, bitrateMbps),
        VideoEncoderBackend.X264 => new X264VideoEncoder(d3d, width, height, framesPerSecond, bitrateMbps),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };
}
