namespace ReplayCapture.Core.Audio;

/// <summary>
/// The canonical internal audio format. Every source — microphone, device loopback, per-process
/// loopback — is resampled to this before it reaches a track, so mixing is a plain addition and
/// every track shares one sample grid.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>Bytes per frame once written to the file as PCM signed 16-bit.</summary>
    public const int BytesPerOutputFrame = Channels * sizeof(short);

    /// <summary>
    /// Frames in a given number of 100 ns ticks, rounded to the nearest frame.
    /// <para>
    /// Rounding rather than flooring matters twice over: one frame at 48 kHz is 208.33 ticks, so
    /// flooring would place every block up to 20.8 µs late instead of within ±10.4 µs, and
    /// <see cref="FramesToTicks"/> would not round-trip (frame 1 → 208 ticks → frame 0).
    /// </para>
    /// </summary>
    public static long TicksToFrames(long ticks)
    {
        var numerator = ticks * SampleRate;
        const long half = TimeSpan.TicksPerSecond / 2;
        return numerator >= 0
            ? (numerator + half) / TimeSpan.TicksPerSecond
            : (numerator - half) / TimeSpan.TicksPerSecond;
    }

    public static long FramesToTicks(long frames) => frames * TimeSpan.TicksPerSecond / SampleRate;
}
