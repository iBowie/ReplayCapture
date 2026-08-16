using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;

namespace ReplayCapture.Core.Encoders;

/// <summary>
/// H.264 encoder running on NVENC, fed directly from D3D11 textures via <see cref="GpuVideoEncoderBase"/>.
/// </summary>
public sealed unsafe class NvencVideoEncoder : GpuVideoEncoderBase
{
    protected override string CodecName => "h264_nvenc";

    public NvencVideoEncoder(D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps)
        : base(d3d, width, height, framesPerSecond, bitrateMbps)
    {
        Log.Info($"NVENC ready: {Width}x{Height}@{framesPerSecond} H.264 CBR {bitrateMbps} Mbps, " +
                 $"{ExtraData.Length}-byte extradata.");
    }

    protected override void ConfigureOptions(AVDictionary** options, int bitrateMbps)
    {
        ffmpeg.av_dict_set(options, "preset", "p4", 0);        // balanced quality/perf
        ffmpeg.av_dict_set(options, "tune", "ll", 0);          // low latency
        ffmpeg.av_dict_set(options, "rc", "cbr", 0);
        ffmpeg.av_dict_set(options, "profile", "high", 0);
        ffmpeg.av_dict_set(options, "delay", "0", 0);          // emit packets immediately
    }
}
