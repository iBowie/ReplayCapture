using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;

namespace ReplayCapture.Core.Encoders;

/// <summary>
/// H.264 encoder running on AMD's Advanced Media Framework (VCE), fed directly from D3D11 textures
/// via <see cref="GpuVideoEncoderBase"/> — the same fully GPU-resident path NVENC uses, since AMF
/// also consumes a D3D11 hardware frames context directly rather than needing a system-memory frame.
/// </summary>
public sealed unsafe class AmfVideoEncoder : GpuVideoEncoderBase
{
    protected override string CodecName => "h264_amf";

    public AmfVideoEncoder(D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps)
        : base(d3d, width, height, framesPerSecond, bitrateMbps)
    {
        Log.Info($"AMF ready: {Width}x{Height}@{framesPerSecond} H.264 CBR {bitrateMbps} Mbps, " +
                 $"{ExtraData.Length}-byte extradata.");
    }

    protected override void ConfigureOptions(AVDictionary** options, int bitrateMbps)
    {
        ffmpeg.av_dict_set(options, "usage", "lowlatency", 0);
        ffmpeg.av_dict_set(options, "quality", "speed", 0);
        ffmpeg.av_dict_set(options, "rc", "cbr", 0);
        ffmpeg.av_dict_set(options, "profile", "high", 0);
    }
}
