using System.Runtime.InteropServices;
using ReplayCapture.Core.Buffering;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;

namespace ReplayCapture.Core.Muxing;

/// <summary>
/// Writes one display's clip as a QuickTime <c>.mov</c> containing the H.264 video track and any
/// number of uncompressed PCM audio tracks.
/// <para>
/// The container choice is deliberate and load-bearing. Premiere Pro cannot import Matroska at all,
/// and its handling of multiple audio tracks inside MP4 is unreliable — it commonly surfaces only
/// the first. QuickTime is the production-standard multi-track container and imports here with no
/// conforming and no re-encode, which is exactly the requirement.
/// </para>
/// <para>
/// Nothing is re-encoded: packets come straight out of the ring buffer as NVENC produced them.
/// </para>
/// </summary>
public sealed unsafe class MovWriter : IDisposable
{
    private const int AvInputBufferPaddingSize = 64;

    // Constants the Sdcb bindings do not surface.
    private const int ProfileH264High = 100;
    private const int AvfmtNoFile = 0x0001;
    private const int AvioFlagWrite = 2;

    private AVFormatContext* _format;
    private AVPacket* _packet;
    private readonly List<AVStream> _streams = [];
    private bool _headerWritten;
    private bool _disposed;

    private readonly record struct AVStream(int Index, AVRational SourceTimeBase);

    public string Path { get; }

    public MovWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        AVFormatContext* format = null;
        Check(ffmpeg.avformat_alloc_output_context2(&format, null, "mov", path),
            "avformat_alloc_output_context2(mov)");
        _format = format;

        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>
    /// Declares the H.264 video track. <paramref name="extraData"/> is the SPS/PPS the encoder
    /// produced at open time — without it the MOV has no decoder configuration and nothing can
    /// play the file.
    /// </summary>
    public int AddVideoStream(int width, int height, int framesPerSecond, byte[] extraData)
    {
        var stream = ffmpeg.avformat_new_stream(_format, null);
        if (stream == null) throw new InvalidOperationException("avformat_new_stream failed for video.");

        var parameters = stream->codecpar;
        parameters->codec_type = AVMediaType.Video;
        parameters->codec_id = AVCodecID.H264;
        parameters->width = width;
        parameters->height = height;
        parameters->format = (int)AVPixelFormat.Nv12;
        parameters->profile = ProfileH264High;

        // Carried through from the encoder so Premiere renders the clip with correct colour rather
        // than a washed-out or crushed approximation.
        parameters->color_primaries = AVColorPrimaries.Bt709;
        parameters->color_trc = AVColorTransferCharacteristic.Bt709;
        parameters->color_space = AVColorSpace.Bt709;
        parameters->color_range = AVColorRange.Mpeg;

        SetExtraData(parameters, extraData);

        var timeBase = new AVRational { Num = 1, Den = framesPerSecond };
        stream->time_base = timeBase;
        stream->avg_frame_rate = new AVRational { Num = framesPerSecond, Den = 1 };
        stream->r_frame_rate = stream->avg_frame_rate;

        _streams.Add(new AVStream(stream->index, timeBase));
        return stream->index;
    }

    /// <summary>
    /// Declares one uncompressed PCM audio track. Uncompressed because Premiere imports PCM with no
    /// conforming pass and no generational loss, and because it means the app ships no audio encoder.
    /// </summary>
    public int AddPcmAudioStream(string name, int sampleRate, int channels)
    {
        var stream = ffmpeg.avformat_new_stream(_format, null);
        if (stream == null) throw new InvalidOperationException($"avformat_new_stream failed for '{name}'.");

        var parameters = stream->codecpar;
        parameters->codec_type = AVMediaType.Audio;
        parameters->codec_id = AVCodecID.PcmS16le;
        parameters->format = (int)AVSampleFormat.S16;
        parameters->sample_rate = sampleRate;
        parameters->bits_per_coded_sample = 16;
        parameters->bit_rate = sampleRate * channels * 16;
        ffmpeg.av_channel_layout_default(&parameters->ch_layout, channels);

        var timeBase = new AVRational { Num = 1, Den = sampleRate };
        stream->time_base = timeBase;

        // Track titles are what make six stems usable in an NLE instead of six anonymous rows.
        AVDictionary* metadata = null;
        ffmpeg.av_dict_set(&metadata, "title", name, 0);
        ffmpeg.av_dict_set(&metadata, "handler_name", name, 0);
        stream->metadata = metadata;

        _streams.Add(new AVStream(stream->index, timeBase));
        return stream->index;
    }

    private static void SetExtraData(AVCodecParameters* parameters, byte[] extraData)
    {
        if (extraData.Length == 0)
        {
            Log.Warn("Video stream has no extradata; the resulting file may not decode.");
            return;
        }

        // libavformat frees this with av_free, so it must come from av_malloc, and decoders read a
        // little past the end, hence the padding.
        var buffer = (byte*)ffmpeg.av_mallocz((ulong)(extraData.Length + AvInputBufferPaddingSize));
        Marshal.Copy(extraData, 0, (IntPtr)buffer, extraData.Length);
        parameters->extradata = buffer;
        parameters->extradata_size = extraData.Length;
    }

    public void WriteHeader()
    {
        AVDictionary* options = null;
        // write_colr emits the 'colr' atom carrying the BT.709 / limited-range tagging that
        // Premiere and QuickTime read.
        ffmpeg.av_dict_set(&options, "movflags", "+write_colr", 0);

        try
        {
            if ((_format->oformat->flags & AvfmtNoFile) == 0)
            {
                AVIOContext* io = null;
                Check(ffmpeg.avio_open(&io, Path, AvioFlagWrite), $"avio_open({Path})");
                _format->pb = io;
            }

            Check(ffmpeg.avformat_write_header(_format, &options), "avformat_write_header");
            _headerWritten = true;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    /// <summary>
    /// Writes one already-encoded packet. <paramref name="timestamp"/> is in the stream's declared
    /// source time base and is rescaled to whatever timescale the muxer settled on.
    /// </summary>
    public void WritePacket(int streamIndex, ReadOnlySpan<byte> data, long timestamp, long duration, bool isKeyframe)
    {
        if (!_headerWritten) throw new InvalidOperationException("WriteHeader must be called first.");

        var stream = _streams[streamIndex];
        var target = _format->streams[streamIndex]->time_base;

        ffmpeg.av_packet_unref(_packet);
        Check(ffmpeg.av_new_packet(_packet, data.Length), "av_new_packet");
        data.CopyTo(new Span<byte>(_packet->data, data.Length));

        _packet->stream_index = streamIndex;
        _packet->pts = ffmpeg.av_rescale_q(timestamp, stream.SourceTimeBase, target);
        // No B-frames anywhere in this pipeline, so decode order equals presentation order.
        _packet->dts = _packet->pts;
        _packet->duration = ffmpeg.av_rescale_q(duration, stream.SourceTimeBase, target);
        _packet->flags = isKeyframe ? (int)ffmpeg.AV_PKT_FLAG_KEY : 0;

        Check(ffmpeg.av_interleaved_write_frame(_format, _packet), "av_interleaved_write_frame");
    }

    /// <summary>Convenience for writing a whole video clip snapshot, rebased so it starts at zero.</summary>
    public void WriteVideoClip(int streamIndex, IReadOnlyList<ClipPacket> packets)
    {
        if (packets.Count == 0) return;

        var firstFrame = packets[0].FrameIndex;
        foreach (var packet in packets)
        {
            WritePacket(
                streamIndex,
                packet.Data,
                packet.FrameIndex - firstFrame,
                duration: 1,
                packet.IsKeyframe);
        }
    }

    public void Finish()
    {
        if (!_headerWritten || _disposed) return;
        Check(ffmpeg.av_write_trailer(_format), "av_write_trailer");
        _headerWritten = false;
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

        try { Finish(); }
        catch (Exception ex) { Log.Error($"Failed to finalise {Path}", ex); }

        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);

        if (_format != null)
        {
            if (_format->pb != null) ffmpeg.avio_closep(&_format->pb);
            ffmpeg.avformat_free_context(_format);
            _format = null;
        }
    }
}
