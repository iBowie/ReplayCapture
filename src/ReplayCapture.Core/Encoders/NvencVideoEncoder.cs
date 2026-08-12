using System.Runtime.InteropServices;
using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Timing;
using Sdcb.FFmpeg.Raw;
using Vortice.Direct3D11;

// Both Sdcb.FFmpeg.Raw and Vortice declare ID3D11Texture2D / ID3D11Device. Alias the D3D-side ones
// so it is always obvious which library a texture belongs to.
using D3DTexture2D = Vortice.Direct3D11.ID3D11Texture2D;
using FFmpegD3D11Device = Sdcb.FFmpeg.Raw.ID3D11Device;

namespace ReplayCapture.Core.Encoders;

/// <summary>
/// H.264 encoder running on NVENC, fed directly from D3D11 textures.
/// <para>
/// The encoder is built on top of the <i>same</i> <see cref="ID3D11Device"/> the capture uses, so a
/// captured frame never leaves the GPU: WGC texture → video processor → NVENC input surface. The
/// only thing crossing to system memory is the compressed bitstream.
/// </para>
/// </summary>
public sealed unsafe class NvencVideoEncoder : IDisposable
{
    private const int D3D11BindRenderTarget = 0x20;

    /// <summary>AV_CODEC_FLAG_GLOBAL_HEADER — not surfaced as a constant by the Sdcb bindings.</summary>
    private const int AvCodecFlagGlobalHeader = 1 << 22;

    /// <summary>
    /// How many NVENC input surfaces to pool. Needs to cover the frames NVENC keeps in flight;
    /// too few and <c>av_hwframe_get_buffer</c> blocks the pacer.
    /// </summary>
    private const int SurfacePoolSize = 16;

    private readonly D3DContext _d3d;
    private readonly Nv12Converter _converter;

    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private long _baseQpc = -1;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public long TicksPerFrame { get; }

    /// <summary>
    /// SPS/PPS produced at open time. The MOV muxer needs these as stream extradata, because a clip
    /// is written long after the encoder started and cannot rely on in-band parameter sets.
    /// </summary>
    public byte[] ExtraData { get; private set; } = [];

    public long FramesEncoded { get; private set; }
    public long BytesProduced { get; private set; }

    /// <summary>Raised for every packet NVENC emits, on the calling (pacer) thread.</summary>
    public event Action<ReadOnlySpan<byte>, long, long, bool>? PacketReady;

    public NvencVideoEncoder(D3DContext d3d, int width, int height, int framesPerSecond, int bitrateMbps)
    {
        // NVENC requires even dimensions; odd-sized displays are rare but do exist.
        Width = width & ~1;
        Height = height & ~1;
        FramesPerSecond = framesPerSecond;
        TicksPerFrame = Clock.TicksPerFrame(framesPerSecond);

        _d3d = d3d;
        _converter = new Nv12Converter(d3d, Width, Height, framesPerSecond);

        CreateHardwareDevice();
        CreateFramePool();
        OpenCodec(bitrateMbps);

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        Log.Info($"NVENC ready: {Width}x{Height}@{framesPerSecond} H.264 CBR {bitrateMbps} Mbps, " +
                 $"{ExtraData.Length}-byte extradata.");
    }

    private void CreateHardwareDevice()
    {
        _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.D3d11va);
        if (_hwDevice == null) throw new InvalidOperationException("av_hwdevice_ctx_alloc(D3D11VA) failed.");

        var deviceContext = (AVHWDeviceContext*)_hwDevice->data;
        var d3dContext = (AVD3D11VADeviceContext*)deviceContext->hwctx;

        // FFmpeg takes ownership of a reference and will Release it on teardown, so hand it one of
        // its own rather than letting it steal ours.
        _d3d.Device.AddRef();
        d3dContext->device = (FFmpegD3D11Device*)_d3d.Device.NativePointer;

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

    private void OpenCodec(int bitrateMbps)
    {
        var encoder = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
        if (encoder == null)
            throw new InvalidOperationException("h264_nvenc is not available in this FFmpeg build.");

        _codec = ffmpeg.avcodec_alloc_context3(encoder);
        _codec->width = Width;
        _codec->height = Height;
        _codec->pix_fmt = AVPixelFormat.D3d11;
        _codec->sw_pix_fmt = AVPixelFormat.Nv12;
        _codec->time_base = new AVRational { Num = 1, Den = FramesPerSecond };
        _codec->framerate = new AVRational { Num = FramesPerSecond, Den = 1 };

        // One IDR per second bounds how much a save has to over-read to find a clean start point.
        _codec->gop_size = FramesPerSecond;

        // No B-frames: PTS then always equals DTS, which keeps ring-buffer trimming and muxing
        // trivial and removes reordering latency.
        _codec->max_b_frames = 0;

        var bitrate = (long)bitrateMbps * 1_000_000;
        _codec->bit_rate = bitrate;
        _codec->rc_max_rate = bitrate;
        _codec->rc_buffer_size = (int)bitrate;   // one second of VBV

        // Tag the bitstream to match what the video processor actually produced. Omitting this is
        // the classic silent failure: the file plays, but every colour is subtly wrong.
        _codec->color_primaries = AVColorPrimaries.Bt709;
        _codec->color_trc = AVColorTransferCharacteristic.Bt709;
        _codec->colorspace = AVColorSpace.Bt709;
        _codec->color_range = AVColorRange.Mpeg;   // limited / studio range

        // Parameter sets out-of-band, because the muxer writes them into the MOV header at save
        // time rather than carrying them inline in every clip.
        _codec->flags |= AvCodecFlagGlobalHeader;

        _codec->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "preset", "p4", 0);        // balanced quality/perf
        ffmpeg.av_dict_set(&options, "tune", "ll", 0);          // low latency
        ffmpeg.av_dict_set(&options, "rc", "cbr", 0);
        ffmpeg.av_dict_set(&options, "profile", "high", 0);
        ffmpeg.av_dict_set(&options, "delay", "0", 0);          // emit packets immediately

        try
        {
            Check(ffmpeg.avcodec_open2(_codec, encoder, &options), "avcodec_open2(h264_nvenc)");
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        if (_codec->extradata_size > 0)
        {
            ExtraData = new byte[_codec->extradata_size];
            Marshal.Copy((IntPtr)_codec->extradata, ExtraData, 0, _codec->extradata_size);
        }
    }

    /// <summary>
    /// Encodes one frame from the capture latch at constant-rate index <paramref name="frameIndex"/>.
    /// <para>
    /// The pacer calls this on every tick, resubmitting the previous texture when the display has
    /// not changed. NVENC turns an unchanged frame into a near-empty P-frame, so a static screen
    /// costs almost nothing while still producing genuine constant frame rate.
    /// </para>
    /// </summary>
    public void Encode(D3DTexture2D source, long frameIndex, long qpcTicks, bool forceKeyframe = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_baseQpc < 0) _baseQpc = qpcTicks;

        ffmpeg.av_frame_unref(_frame);
        Check(ffmpeg.av_hwframe_get_buffer(_hwFrames, _frame, 0), "av_hwframe_get_buffer");

        // For D3D11 frames FFmpeg puts the texture array in data[0] and the slice index in data[1].
        using var destination = new D3DTexture2D((IntPtr)_frame->data[0]);
        destination.AddRef();
        var slice = (uint)(nint)_frame->data[1];

        _converter.Convert(source, destination, slice);

        _frame->pts = frameIndex;
        _frame->pict_type = forceKeyframe ? AVPictureType.I : AVPictureType.None;

        Check(ffmpeg.avcodec_send_frame(_codec, _frame), "avcodec_send_frame");
        DrainPackets();
    }

    private void DrainPackets()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            Check(result, "avcodec_receive_packet");

            try
            {
                var isKeyframe = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                // Timestamps are reconstructed from the constant-rate index rather than read back
                // from the encoder, which is what guarantees the file is genuinely CFR.
                var qpc = _baseQpc + _packet->pts * TicksPerFrame;

                var span = new ReadOnlySpan<byte>(_packet->data, _packet->size);
                PacketReady?.Invoke(span, _packet->pts, qpc, isKeyframe);

                FramesEncoded++;
                BytesProduced += _packet->size;
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    /// <summary>Pushes any frames NVENC is still holding. Called before writing a clip.</summary>
    public void Flush()
    {
        if (_disposed) return;
        ffmpeg.avcodec_send_frame(_codec, null);
        DrainPackets();
    }

    private static void Check(int result, string what)
    {
        if (result >= 0) return;

        var buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        var message = Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
        throw new InvalidOperationException($"{what} failed: {message} ({result})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
        fixed (AVCodecContext** codec = &_codec) ffmpeg.avcodec_free_context(codec);
        fixed (AVBufferRef** frames = &_hwFrames) ffmpeg.av_buffer_unref(frames);
        fixed (AVBufferRef** device = &_hwDevice) ffmpeg.av_buffer_unref(device);

        _converter.Dispose();
    }
}
