using System.Runtime.InteropServices;
using ReplayCapture.Core.Timing;
using Sdcb.FFmpeg.Raw;

namespace ReplayCapture.Core.Encoders;

/// <summary>
/// Shared send-frame/receive-packet plumbing for every <see cref="IVideoEncoder{TFrame}"/> backend.
/// What differs between NVENC, AMF and libx264 (and, on Linux, VAAPI) is how the source frame
/// becomes an <c>AVFrame</c> the codec can consume (GPU hardware-frames-context blit for the
/// GPU-resident backends, GPU→CPU readback into a system-memory frame for x264); everything from
/// <c>avcodec_send_frame</c> onward — timestamp reconstruction, packet draining, flush, disposal —
/// is identical, so it lives here once instead of being copy-pasted per backend. Generic over the
/// captured frame's native handle type so this shared plumbing needs no platform-specific GPU
/// context field of its own — subclasses that need one (see <see cref="GpuVideoEncoderBase"/>) own
/// it themselves.
/// </summary>
public abstract unsafe class VideoEncoderBase<TFrame> : IVideoEncoder<TFrame>
{
    /// <summary>AV_CODEC_FLAG_GLOBAL_HEADER — not surfaced as a constant by the Sdcb bindings.</summary>
    protected const int AvCodecFlagGlobalHeader = 1 << 22;

    protected AVCodecContext* Codec;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private long _baseQpc = -1;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public long TicksPerFrame { get; }

    public byte[] ExtraData { get; protected set; } = [];

    public long FramesEncoded { get; private set; }
    public long BytesProduced { get; private set; }

    public event Action<ReadOnlySpan<byte>, long, long, bool>? PacketReady;

    protected VideoEncoderBase(int width, int height, int framesPerSecond)
    {
        // Every backend here needs even dimensions; odd-sized displays are rare but do exist.
        Width = width & ~1;
        Height = height & ~1;
        FramesPerSecond = framesPerSecond;
        TicksPerFrame = Clock.TicksPerFrame(framesPerSecond);
    }

    /// <summary>Call once <see cref="Codec"/> has been opened, so <see cref="ExtraData"/> is set.</summary>
    protected void CaptureExtraData()
    {
        if (Codec->extradata_size <= 0) return;

        ExtraData = new byte[Codec->extradata_size];
        Marshal.Copy((IntPtr)Codec->extradata, ExtraData, 0, Codec->extradata_size);
    }

    /// <summary>Allocates the reusable frame/packet the send/receive loop uses. Call once codec setup is done.</summary>
    protected void AllocateFrameAndPacket()
    {
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>
    /// Fills <paramref name="frame"/> (already <c>av_frame_unref</c>'d) with <paramref name="source"/>'s
    /// pixel data, in whatever form this backend's codec expects — a hardware D3D11 frame for NVENC
    /// and AMF, or a system-memory NV12 buffer for x264.
    /// </summary>
    protected abstract void PopulateFrame(AVFrame* frame, TFrame source);

    /// <summary>Rebuilds whatever converts <typeparamref name="TFrame"/> into the encoder's fixed-size input for a new source size.</summary>
    protected abstract void ReconfigureSource(int width, int height);

    public void NotifySourceResized(int width, int height) => ReconfigureSource(width, height);

    public void Encode(TFrame source, long frameIndex, long qpcTicks, bool forceKeyframe = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_baseQpc < 0) _baseQpc = qpcTicks;

        ffmpeg.av_frame_unref(_frame);
        PopulateFrame(_frame, source);

        _frame->pts = frameIndex;
        _frame->pict_type = forceKeyframe ? AVPictureType.I : AVPictureType.None;

        Check(ffmpeg.avcodec_send_frame(Codec, _frame), "avcodec_send_frame");
        DrainPackets();
    }

    private void DrainPackets()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(Codec, _packet);
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

    public void Flush()
    {
        if (_disposed) return;
        ffmpeg.avcodec_send_frame(Codec, null);
        DrainPackets();
    }

    protected static void Check(int result, string what)
    {
        if (result >= 0) return;

        var buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        var message = Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
        throw new InvalidOperationException($"{what} failed: {message} ({result})");
    }

    /// <summary>Overridden to release backend-specific resources (hw device/frames, converters, staging textures).</summary>
    protected abstract void DisposeCore();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
        fixed (AVCodecContext** codec = &Codec) ffmpeg.avcodec_free_context(codec);

        DisposeCore();
    }
}
