using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;
using Vortice.DXGI;
using Vortice.Direct3D11;

using D3DTexture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ReplayCapture.Core.Encoders;

/// <summary>
/// Software H.264 encoder running on libx264, for machines with no usable NVENC or AMF encoder.
/// <para>
/// Unlike <see cref="NvencVideoEncoder"/> and <see cref="AmfVideoEncoder"/>, x264 cannot consume a
/// D3D11 hardware frame — it needs the pixels in system memory. The captured texture still gets its
/// BGRA→NV12 colour conversion done on the GPU via the same <see cref="Nv12Converter"/> the hardware
/// backends use (there is no reason to do that work on the CPU when the GPU does it for free), but
/// the NV12 result then has to be copied into a staging texture and mapped so it can be handed to
/// the encoder — a GPU→CPU round-trip the two hardware paths never pay.
/// </para>
/// </summary>
public sealed unsafe class X264VideoEncoder : VideoEncoderBase<D3DTexture2D>
{
    private readonly D3DContext _d3d;
    private readonly Nv12Converter _converter;
    private readonly D3DTexture2D _renderTarget;
    private readonly D3DTexture2D _staging;

    public X264VideoEncoder(D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps)
        : base(width, height, framesPerSecond)
    {
        _d3d = d3d;
        _converter = new Nv12Converter(d3d, Width, Height, framesPerSecond);

        _renderTarget = _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _staging = _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        OpenCodec(bitrateMbps);
        CaptureExtraData();
        AllocateFrameAndPacket();

        Log.Info($"libx264 ready: {Width}x{Height}@{framesPerSecond} H.264 CBR {bitrateMbps} Mbps, " +
                 $"{ExtraData.Length}-byte extradata.");
    }

    private void OpenCodec(int bitrateMbps)
    {
        var encoder = ffmpeg.avcodec_find_encoder_by_name("libx264");
        if (encoder == null)
            throw new InvalidOperationException("libx264 is not available in this FFmpeg build.");

        Codec = ffmpeg.avcodec_alloc_context3(encoder);
        Codec->width = Width;
        Codec->height = Height;
        Codec->pix_fmt = AVPixelFormat.Nv12;
        Codec->time_base = new AVRational { Num = 1, Den = FramesPerSecond };
        Codec->framerate = new AVRational { Num = FramesPerSecond, Den = 1 };

        // One IDR per second bounds how much a save has to over-read to find a clean start point.
        Codec->gop_size = FramesPerSecond;

        // No B-frames: PTS then always equals DTS, which keeps ring-buffer trimming and muxing
        // trivial and removes reordering latency.
        Codec->max_b_frames = 0;

        var bitrate = (long)bitrateMbps * 1_000_000;
        Codec->bit_rate = bitrate;
        Codec->rc_max_rate = bitrate;
        Codec->rc_buffer_size = (int)bitrate;   // one second of VBV

        // Tag the bitstream to match what the video processor actually produced. Omitting this is
        // the classic silent failure: the file plays, but every colour is subtly wrong.
        Codec->color_primaries = AVColorPrimaries.Bt709;
        Codec->color_trc = AVColorTransferCharacteristic.Bt709;
        Codec->colorspace = AVColorSpace.Bt709;
        Codec->color_range = AVColorRange.Mpeg;   // limited / studio range

        // With AV_CODEC_FLAG_GLOBAL_HEADER set, FFmpeg's libx264 wrapper switches x264 itself out of
        // Annex-B mode and emits length-prefixed (avcC) NAL units for both extradata and every
        // packet — exactly the form MovWriter already assumes NVENC produces, so no bitstream
        // filtering is needed here either.
        Codec->flags |= AvCodecFlagGlobalHeader;

        AVDictionary* options = null;
        try
        {
            // ultrafast/zerolatency: this pipeline needs to keep up with the display's frame rate in
            // real time on the CPU, with a save happening at an arbitrary moment — there is no
            // multi-pass or buffered-lookahead budget to spend on quality the way an offline encode
            // would.
            ffmpeg.av_dict_set(&options, "preset", "ultrafast", 0);
            ffmpeg.av_dict_set(&options, "tune", "zerolatency", 0);
            ffmpeg.av_dict_set(&options, "profile", "high", 0);

            Check(ffmpeg.avcodec_open2(Codec, encoder, &options), "avcodec_open2(libx264)");
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    protected override void ReconfigureSource(int width, int height) => _converter.Reconfigure(width, height);

    protected override void PopulateFrame(AVFrame* frame, D3DTexture2D source)
    {
        _converter.Convert(source, _renderTarget, 0);
        _d3d.ImmediateContext.CopyResource(_staging, _renderTarget);

        frame->format = (int)AVPixelFormat.Nv12;
        frame->width = Width;
        frame->height = Height;
        Check(ffmpeg.av_frame_get_buffer(frame, 32), "av_frame_get_buffer");

        var mapped = _d3d.ImmediateContext.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowPitch = (int)mapped.RowPitch;
            var basePtr = (byte*)mapped.DataPointer;
            var yPlane = (byte*)(nint)frame->data[0];
            var uvPlane = (byte*)(nint)frame->data[1];

            // Y plane: Height full-width rows.
            for (var row = 0; row < Height; row++)
            {
                Buffer.MemoryCopy(
                    basePtr + row * rowPitch,
                    yPlane + row * frame->linesize[0],
                    frame->linesize[0], Width);
            }

            // NV12's interleaved U/V plane sits right after the Y plane in the same staging
            // subresource, at half the vertical resolution but the same row pitch.
            var uvBase = basePtr + Height * rowPitch;
            var uvHeight = Height / 2;
            for (var row = 0; row < uvHeight; row++)
            {
                Buffer.MemoryCopy(
                    uvBase + row * rowPitch,
                    uvPlane + row * frame->linesize[1],
                    frame->linesize[1], Width);
            }
        }
        finally
        {
            _d3d.ImmediateContext.Unmap(_staging, 0);
        }
    }

    protected override void DisposeCore()
    {
        _staging.Dispose();
        _renderTarget.Dispose();
        _converter.Dispose();
    }
}
