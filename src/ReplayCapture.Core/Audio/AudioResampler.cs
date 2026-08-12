using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Raw;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Converts whatever an endpoint happens to produce into the pipeline's canonical
/// 48 kHz interleaved stereo float.
/// <para>
/// This is not optional plumbing: microphones commonly run at 44.1 kHz or 16 kHz, and a playback
/// endpoint may be 5.1 or 7.1. Without a resample-and-downmix step those sources would drift
/// against video or arrive with the wrong channel count.
/// </para>
/// </summary>
public sealed unsafe class AudioResampler : IDisposable
{
    private SwrContext* _context;
    private readonly bool _passthrough;
    private readonly int _inputChannels;

    public int InputSampleRate { get; }
    public AVSampleFormat InputFormat { get; }

    public AudioResampler(int inputSampleRate, int inputChannels, AVSampleFormat inputFormat)
    {
        InputSampleRate = inputSampleRate;
        InputFormat = inputFormat;
        _inputChannels = inputChannels;

        _passthrough = inputSampleRate == AudioFormat.SampleRate
                       && inputChannels == AudioFormat.Channels
                       && inputFormat == AVSampleFormat.Flt;

        if (_passthrough) return;

        AVChannelLayout inputLayout, outputLayout;
        ffmpeg.av_channel_layout_default(&inputLayout, inputChannels);
        ffmpeg.av_channel_layout_default(&outputLayout, AudioFormat.Channels);

        SwrContext* context = null;
        var result = ffmpeg.swr_alloc_set_opts2(
            &context,
            &outputLayout, AVSampleFormat.Flt, AudioFormat.SampleRate,
            &inputLayout, inputFormat, inputSampleRate,
            0, null);

        if (result < 0) throw new InvalidOperationException($"swr_alloc_set_opts2 failed ({result}).");

        result = ffmpeg.swr_init(context);
        if (result < 0) throw new InvalidOperationException($"swr_init failed ({result}).");

        _context = context;
    }

    /// <summary>
    /// Converts <paramref name="inputFrames"/> frames of interleaved input into
    /// <paramref name="output"/>, returning how many output frames were produced.
    /// </summary>
    public int Convert(byte* input, int inputFrames, float[] output)
    {
        if (inputFrames <= 0) return 0;

        if (_passthrough)
        {
            var count = inputFrames * AudioFormat.Channels;
            if (count > output.Length) count = output.Length;
            Marshal.Copy((IntPtr)input, output, 0, count);
            return count / AudioFormat.Channels;
        }

        var capacityFrames = output.Length / AudioFormat.Channels;

        fixed (float* outputPointer = output)
        {
            var outputPlanes = stackalloc byte*[1];
            outputPlanes[0] = (byte*)outputPointer;

            var inputPlanes = stackalloc byte*[1];
            inputPlanes[0] = input;

            var produced = ffmpeg.swr_convert(_context, outputPlanes, capacityFrames, inputPlanes, inputFrames);
            return produced < 0 ? 0 : produced;
        }
    }

    /// <summary>Upper bound on output frames for a given input, used to size scratch buffers.</summary>
    public int EstimateOutputFrames(int inputFrames) =>
        _passthrough
            ? inputFrames
            : (int)ffmpeg.swr_get_out_samples(_context, inputFrames) + AudioFormat.SampleRate / 100;

    /// <summary>Silence in this input format, in bytes per frame — used to synthesise gaps.</summary>
    public int InputBytesPerFrame =>
        ffmpeg.av_get_bytes_per_sample(InputFormat) * _inputChannels;

    public void Dispose()
    {
        if (_context == null) return;
        fixed (SwrContext** context = &_context) ffmpeg.swr_free(context);
    }
}
