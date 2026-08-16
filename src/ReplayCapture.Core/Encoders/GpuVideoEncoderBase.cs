using ReplayCapture.Core.Capture;
using Sdcb.FFmpeg.Raw;

using D3DTexture2D = Vortice.Direct3D11.ID3D11Texture2D;
using FFmpegD3D11Device = Sdcb.FFmpeg.Raw.ID3D11Device;

namespace ReplayCapture.Core.Encoders;

/// <summary>
/// Shared base for the two fully GPU-resident H.264 backends (NVENC and AMD AMF): both consume an
/// FFmpeg D3D11VA hardware frames context directly, so the captured texture never leaves the GPU —
/// desktop texture → video processor → hardware encoder input surface, with only the compressed
/// bitstream crossing to system memory. Subclasses supply the codec name and its private options;
/// this class owns the hw device, the frame pool, and feeding the pool through
/// <see cref="Nv12Converter"/>.
/// </summary>
public abstract unsafe class GpuVideoEncoderBase : VideoEncoderBase
{
    private const int D3D11BindRenderTarget = 0x20;

    /// <summary>
    /// How many hardware input surfaces to pool. Needs to cover the frames the encoder keeps in
    /// flight; too few and <c>av_hwframe_get_buffer</c> blocks the pacer.
    /// </summary>
    private const int SurfacePoolSize = 16;

    private readonly Nv12Converter _converter;

    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;

    protected GpuVideoEncoderBase(D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps)
        : base(d3d, width, height, framesPerSecond)
    {
        _converter = new Nv12Converter(d3d, Width, Height, framesPerSecond);

        CreateHardwareDevice();
        CreateFramePool();
        OpenCodec(bitrateMbps);
        CaptureExtraData();

        AllocateFrameAndPacket();
    }

    private void CreateHardwareDevice()
    {
        _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.D3d11va);
        if (_hwDevice == null) throw new InvalidOperationException("av_hwdevice_ctx_alloc(D3D11VA) failed.");

        var deviceContext = (AVHWDeviceContext*)_hwDevice->data;
        var d3dContext = (AVD3D11VADeviceContext*)deviceContext->hwctx;

        // FFmpeg takes ownership of a reference and will Release it on teardown, so hand it one of
        // its own rather than letting it steal ours.
        D3d.Device.AddRef();
        d3dContext->device = (FFmpegD3D11Device*)D3d.Device.NativePointer;

        Check(ffmpeg.av_hwdevice_ctx_init(_hwDevice), "av_hwdevice_ctx_init");
    }

    private void CreateFramePool()
    {
        _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
        if (_hwFrames == null) throw new InvalidOperationException("av_hwframe_ctx_alloc failed.");

        var frames = (AVHWFramesContext*)_hwFrames->data;
        frames->format = AVPixelFormat.D3d11;
        frames->sw_format = AVPixelFormat.Nv12;
        frames->width = Width;
        frames->height = Height;
        frames->initial_pool_size = SurfacePoolSize;

        // RENDER_TARGET is what lets the video processor write straight into these surfaces; without
        // it CreateVideoProcessorOutputView fails and the pipeline needs an extra staging copy.
        var framesHwContext = (AVD3D11VAFramesContext*)frames->hwctx;
        framesHwContext->BindFlags = D3D11BindRenderTarget;

        Check(ffmpeg.av_hwframe_ctx_init(_hwFrames), "av_hwframe_ctx_init");
    }

    /// <summary>
    /// Allocates and opens <see cref="VideoEncoderBase.Codec"/> against <see cref="_hwFrames"/>.
    /// Implementations set the codec name, private options (preset/rc/profile/...), and bitrate
    /// fields; everything shared (dimensions, time base, GOP, colour tagging, global header,
    /// hw_frames_ctx) is set here.
    /// </summary>
    private void OpenCodec(int bitrateMbps)
    {
        var encoder = ffmpeg.avcodec_find_encoder_by_name(CodecName);
        if (encoder == null)
            throw new InvalidOperationException($"{CodecName} is not available in this FFmpeg build.");

        Codec = ffmpeg.avcodec_alloc_context3(encoder);
        Codec->width = Width;
        Codec->height = Height;
        Codec->pix_fmt = AVPixelFormat.D3d11;
        Codec->sw_pix_fmt = AVPixelFormat.Nv12;
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

        // Parameter sets out-of-band, because the muxer writes them into the MOV header at save
        // time rather than carrying them inline in every clip.
        Codec->flags |= AvCodecFlagGlobalHeader;

        Codec->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);

        AVDictionary* options = null;
        try
        {
            ConfigureOptions(&options, bitrateMbps);
            Check(ffmpeg.avcodec_open2(Codec, encoder, &options), $"avcodec_open2({CodecName})");
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    protected abstract string CodecName { get; }

    /// <summary>Sets the encoder's private (<c>AVDictionary</c>) options — preset, rate control, profile, etc.</summary>
    protected abstract void ConfigureOptions(AVDictionary** options, int bitrateMbps);

    protected override void PopulateFrame(AVFrame* frame, D3DTexture2D source)
    {
        Check(ffmpeg.av_hwframe_get_buffer(_hwFrames, frame, 0), "av_hwframe_get_buffer");

        // For D3D11 frames FFmpeg puts the texture array in data[0] and the slice index in data[1].
        using var destination = new D3DTexture2D((IntPtr)frame->data[0]);
        destination.AddRef();
        var slice = (uint)(nint)frame->data[1];

        _converter.Convert(source, destination, slice);
    }

    protected override void DisposeCore()
    {
        fixed (AVBufferRef** frames = &_hwFrames) ffmpeg.av_buffer_unref(frames);
        fixed (AVBufferRef** device = &_hwDevice) ffmpeg.av_buffer_unref(device);

        _converter.Dispose();
    }
}
