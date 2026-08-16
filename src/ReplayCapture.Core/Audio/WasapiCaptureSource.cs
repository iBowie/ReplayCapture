using System.Runtime.InteropServices;
using ReplayCapture.Core.Audio.Interop;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;
using Windows.Win32.Media.Audio;

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
    /// <summary>
    /// Endpoint buffer length.
    /// <para>
    /// 20 ms is the usual "low latency" choice, but this pipeline never plays audio back live — it
    /// only reads samples off a QPC-addressed timeline — so latency here buys nothing. A 20 ms
    /// endpoint buffer gives the poll thread almost no slack: on a machine also NVENC-encoding a
    /// 200 Hz display, an occasional scheduling delay past 20 ms overruns the endpoint before the
    /// driver can flag it as a discontinuity, and the lost/corrupted samples come out as clicks in
    /// the saved file (confirmed by comparing this endpoint's loopback capture against a
    /// process-loopback capture of the identical audio: the process-loopback device — which is not
    /// bound to real hardware DMA timing — stayed clean while this one clicked). A much longer
    /// buffer costs nothing here and gives the poll thread far more room to fall behind.
    /// </para>
    /// </summary>
    private const long BufferDurationTicks = 500 * TimeSpan.TicksPerMillisecond;

    // Fixed, documented subformat GUIDs. Spelled out rather than projected because CsWin32's
    // generation of these varies with what else is requested.
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly WasapiCaptureLoop _loop;
    private readonly SilentRenderKeepAlive? _keepAlive;

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
        device.Activate(audioClientIid, Clsctx.All, 0, out var clientPtr);
        var client = ComInterop.WrapAndRelease<IAudioClient>(clientPtr);

        // Shared mode has no format of its own — the endpoint's mix format is what it will give us,
        // and asking for anything else fails rather than converting.
        client.GetMixFormat(out var mixFormatPtr);
        var mixFormat = (WAVEFORMATEX*)mixFormatPtr;

        AudioResampler resampler;
        try
        {
            var inputChannels = mixFormat->nChannels;
            var inputSampleRate = (int)mixFormat->nSamplesPerSec;
            var inputFormat = DescribeFormat(mixFormat);

            client.Initialize(
                AudclntSharemode.Shared,
                loopback ? Constants.StreamflagsLoopback : 0u,
                BufferDurationTicks,
                0,
                (nint)mixFormat,
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
        client.GetService(captureIid, out var capturePtr);

        _loop = new WasapiCaptureLoop(
            client, ComInterop.WrapAndRelease<IAudioCaptureClient>(capturePtr), resampler, name,
            (qpc, samples) => SamplesReady?.Invoke(qpc, samples));

        // Loopback only: keeps the shared audio engine from idling between sounds, which is what
        // was producing an audible pop on this endpoint every time it woke back up (confirmed by
        // comparing against a process-loopback capture of the same audio, which has no such engine
        // to idle and stayed clean at the exact same timestamps). Capture-mode sources don't tap a
        // render engine at all, so there is nothing here for them to keep awake.
        if (loopback) _keepAlive = new SilentRenderKeepAlive(device);
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

    public void Start()
    {
        // Started first: the loopback capture should never see the "engine cold-starting" glitch
        // this exists to prevent, not even for the first sound after Start.
        _keepAlive?.Start();
        _loop.Start();
    }

    public void Dispose()
    {
        _loop.Dispose();
        _keepAlive?.Dispose();
    }
}

internal static class Constants
{
    public const uint StreamflagsLoopback = 0x00020000;
    public const uint StreamflagsEventcallback = 0x00040000;
    public const uint StreamflagsAutoconvertpcm = 0x80000000;
    public const uint StreamflagsSrcDefaultQuality = 0x08000000;
}
