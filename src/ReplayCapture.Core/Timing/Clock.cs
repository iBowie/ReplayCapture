using System.Diagnostics;

namespace ReplayCapture.Core.Timing;

/// <summary>
/// The single time base for the whole pipeline.
/// <para>
/// Everything — video frames, every audio track, and the save trigger — is stamped from
/// QueryPerformanceCounter expressed in 100 ns units. That unit is not arbitrary: WGC's
/// <c>Direct3D11CaptureFrame.SystemRelativeTime</c> and WASAPI's <c>qpcPosition</c> both already
/// report QPC in 100 ns ticks, so using anything else would mean converting at every boundary and
/// inviting drift between streams.
/// </para>
/// </summary>
public static class Clock
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond; // 10,000,000 (100 ns units)

    private static readonly double TicksPerCount = (double)TicksPerSecond / Stopwatch.Frequency;

    /// <summary>Current QPC reading in 100 ns ticks.</summary>
    public static long Now => (long)(Stopwatch.GetTimestamp() * TicksPerCount);

    public static long FromSeconds(double seconds) => (long)(seconds * TicksPerSecond);

    public static double ToSeconds(long ticks) => (double)ticks / TicksPerSecond;

    public static long FromMilliseconds(double milliseconds) =>
        (long)(milliseconds * (TicksPerSecond / 1000.0));

    /// <summary>Ticks per video frame at the given rate, used to lay out constant-rate timestamps.</summary>
    public static long TicksPerFrame(int framesPerSecond) => TicksPerSecond / framesPerSecond;
}
