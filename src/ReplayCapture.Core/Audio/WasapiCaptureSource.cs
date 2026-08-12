using System.Runtime.InteropServices;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace ReplayCapture.Core.Audio;

/// <summary>Anything that can feed samples into a track.</summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }
    event Action<long, ReadOnlyMemory<float>>? SamplesReady;
    void Start();
}

/// <summary>
/// Captures one WASAPI endpoint — a microphone, or a playback device in loopback mode.
/// <para>
/// Hand-rolled rather than delegated to NAudio, for one reason: synchronisation.
/// <c>IAudioCaptureClient::GetBuffer</c> reports the QPC time the samples were captured, and that
/// timestamp is the entire basis for keeping every audio track aligned with video. NAudio's
/// <c>DataAvailable</c> does not surface it, so sync would have to be inferred from arrival time —
/// exactly the drift this design avoids.
/// </para>
/// </summary>
public sealed unsafe class WasapiCaptureSource : IAudioSource
{
    /// <summary>Endpoint buffer length. Low enough for latency, long enough to be safe.</summary>
    private const long BufferDurationTicks = 20 * TimeSpan.TicksPerMillisecond;

    // Fixed, documented subformat GUIDs. Spelled out rather than projected because CsWin32's
    // generation of these varies with what else is requested.
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly WasapiCaptureLoop _loop;

    public string Name { get; }
    public bool IsLoopback { get; }

    public long FramesCaptured => _loop.FramesCaptured;
    public long Discontinuities => _loop.Discontinuities;

    public event Action<long, ReadOnlyMemory<float>>? SamplesReady;

    /// <summary>
    /// Internal because <c>IMMDevice</c> is an internal generated COM type; construct through
    /// <see cref="AudioDeviceEnumerator"/> instead.
    /// </summary>
    internal WasapiCaptureSource(IMMDevice device, bool loopback, string name)
    {
        Name = name;
        IsLoopback = loopback;

        var audioClientIid = typeof(IAudioClient).GUID;
        device.Activate(&audioClientIid, CLSCTX.CLSCTX_ALL, null, out var clientObject);
        var client = (IAudioClient)clientObject;

        // Shared mode has no format of its own — the endpoint's mix format is what it will give us,
        // and asking for anything else fails rather than converting.
        WAVEFORMATEX* mixFormat;
        client.GetMixFormat(&mixFormat);

        AudioResampler resampler;
        try
        {
            var inputChannels = mixFormat->nChannels;
            var inputSampleRate = (int)mixFormat->nSamplesPerSec;
            var inputFormat = DescribeFormat(mixFormat);

            client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                loopback ? Constants.StreamflagsLoopback : 0u,
                BufferDurationTicks,
                0,
                mixFormat,
                null);

            resampler = new AudioResampler(inputSampleRate, inputChannels, inputFormat);

            Log.Info($"Audio source '{name}': {inputSampleRate} Hz, {inputChannels} ch, " +
                     $"{inputFormat}{(loopback ? ", loopback" : "")}.");
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)mixFormat);
        }

        var captureIid = typeof(IAudioCaptureClient).GUID;
        client.GetService(&captureIid, out var captureObject);

        _loop = new WasapiCaptureLoop(
            client, (IAudioCaptureClient)captureObject, resampler, name,
            (qpc, samples) => SamplesReady?.Invoke(qpc, samples));
    }

    /// <summary>Maps the endpoint's mix format onto the FFmpeg sample format the resampler wants.</summary>
    private static AVSampleFormat DescribeFormat(WAVEFORMATEX* format)
    {
        // Shared-mode mix formats are essentially always extensible/float32, but integer endpoints
        // do exist and silently misreading one produces noise rather than an error.
        const ushort WaveFormatIeeeFloat = 0x0003;
        const ushort WaveFormatExtensible = 0xFFFE;

        if (format->wFormatTag == WaveFormatIeeeFloat) return AVSampleFormat.Flt;

        if (format->wFormatTag == WaveFormatExtensible)
        {
            var subFormat = ((WAVEFORMATEXTENSIBLE*)format)->SubFormat;
            if (subFormat == SubtypeIeeeFloat) return AVSampleFormat.Flt;
            if (subFormat == SubtypePcm)
                return format->wBitsPerSample == 16 ? AVSampleFormat.S16 : AVSampleFormat.S32;
        }

        return format->wBitsPerSample switch
        {
            16 => AVSampleFormat.S16,
            32 => AVSampleFormat.S32,
            _ => AVSampleFormat.Flt,
        };
    }

    public void Start() => _loop.Start();

    public void Dispose() => _loop.Dispose();
}

internal static class Constants
{
    public const uint StreamflagsLoopback = 0x00020000;
    public const uint StreamflagsEventcallback = 0x00040000;
    public const uint StreamflagsAutoconvertpcm = 0x80000000;
    public const uint StreamflagsSrcDefaultQuality = 0x08000000;
}
