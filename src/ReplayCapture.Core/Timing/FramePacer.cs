using ReplayCapture.Core.Diagnostics;
#if WINDOWS
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
#endif

namespace ReplayCapture.Core.Timing;

/// <summary>
/// Drives encoding at a genuinely constant frame rate.
/// <para>
/// Windows.Graphics.Capture only delivers a frame when the screen content <i>changes</i>, which
/// produces variable frame rate. Premiere Pro handles VFR badly — it is the difference between a
/// clip that drops straight onto a timeline and one that needs conforming and still drifts out of
/// sync. So the pacer ticks on its own schedule and asks the encoder for a frame every time,
/// resubmitting the previous texture when nothing new arrived. NVENC turns an unchanged frame into
/// a near-empty P-frame, so a static screen costs almost nothing.
/// </para>
/// <para>
/// Each tick's deadline is computed from the absolute schedule rather than by adding an interval to
/// "now", so scheduling jitter cannot accumulate into long-term drift — <i>except</i> when the
/// system genuinely cannot sustain the target rate on average. A per-tick overrun too small to ever
/// trip <see cref="LateTicks"/> (which only counts a single tick missing its deadline by a whole
/// frame) still compounds over a multi-minute session into seconds of schedule drift, because the
/// deadline is a pure function of frame index — nothing here ever re-bases it against real elapsed
/// time. Video stamped from that drifting schedule then disagrees with audio, which is positioned
/// from WASAPI's wall-clock-accurate timestamps and never drifts: the saved file is out of sync from
/// its very first frame, worse the longer the session runs. <see cref="MaxDriftTicks"/> bounds that
/// by resyncing to real time once the schedule falls chronically behind, at the cost of an
/// intentional, bounded skip in the frame sequence rather than an unbounded, invisible one.
/// </para>
/// </summary>
public sealed class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    /// <summary>
    /// How far behind schedule the real clock must be before the pacer resyncs rather than
    /// continuing to grind through the backlog one tick at a time. Comfortably above ordinary OS
    /// scheduling jitter (tens of ms), but small enough that the resulting A/V offset is never more
    /// than a brief, one-time skip.
    /// </summary>
    private static readonly long MaxDriftTicks = Clock.FromMilliseconds(250);

    private readonly int _framesPerSecond;
    private readonly long _ticksPerFrame;
    private readonly Action<long, long> _onTick;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();

    private long _frameIndex;
    private long _lateTicks;
    private long _framesSkippedForDrift;
    private long _driftResyncEvents;

    /// <summary>Ticks whose deadline had already passed when they were serviced.</summary>
    public long LateTicks => Interlocked.Read(ref _lateTicks);

    /// <summary>
    /// Frame indices skipped to resync the schedule against real time after chronic drift. Zero on
    /// a system that can actually sustain the target rate; a steadily climbing count means the
    /// requested frame rate is not sustainable under the current load and is being throttled down to
    /// whatever the system can really deliver instead of silently losing sync.
    /// </summary>
    public long FramesSkippedForDrift => Interlocked.Read(ref _framesSkippedForDrift);

    public long FrameIndex => Interlocked.Read(ref _frameIndex);

    /// <param name="onTick">Receives (frameIndex, scheduledQpcTicks) on the pacing thread.</param>
    public FramePacer(int framesPerSecond, Action<long, long> onTick, string name)
    {
        _framesPerSecond = framesPerSecond;
        _ticksPerFrame = Clock.TicksPerFrame(framesPerSecond);
        _onTick = onTick;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"pacer-{name}",
            // Above normal, not highest: the pacer must beat ordinary work to the CPU, but must not
            // be able to starve the capture callbacks it depends on.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    /// <summary>
    /// Advances <paramref name="index"/>/<paramref name="deadline"/> to resync against
    /// <paramref name="now"/> when the schedule has drifted more than <paramref name="maxDriftTicks"/>
    /// behind it, by skipping whole frame indices rather than grinding through the backlog one tick
    /// at a time. Pure and side-effect-free so the resync arithmetic can be verified without a real
    /// clock or thread. Returns how many indices were skipped, or 0 when no resync was needed.
    /// </summary>
    internal static long Resync(
        ref long index, ref long deadline, long start, long ticksPerFrame, long now, long maxDriftTicks)
    {
        var drift = now - deadline;
        if (drift <= maxDriftTicks) return 0;

        var skip = drift / ticksPerFrame;
        index += skip;
        deadline = start + index * ticksPerFrame;
        return skip;
    }

    private void Run()
    {
        using var timer = CreateTimer();

        var start = Clock.Now;
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            var index = _frameIndex;
            var deadline = start + index * _ticksPerFrame;
            var now = Clock.Now;

            var skip = Resync(ref index, ref deadline, start, _ticksPerFrame, now, MaxDriftTicks);
            if (skip > 0)
            {
                _frameIndex = index;
                var skipped = Interlocked.Add(ref _framesSkippedForDrift, skip);
                var events = Interlocked.Increment(ref _driftResyncEvents);

                // Logged sparingly: a system that structurally cannot sustain the target rate will
                // hit this every cycle, and a line per occurrence would flood the log for no benefit
                // over knowing it is happening at all and roughly how often.
                if (events == 1 || events % 20 == 0)
                {
                    Log.Warn($"Pacer '{_thread.Name}' fell behind real time; skipped {skip} frame(s) " +
                             $"to resync ({skipped} total, {events} resync(s)).");
                }
            }

            var remaining = deadline - now;

            if (remaining > 0)
            {
                if (!TryWaitPrecise(timer, remaining))
                {
                    var milliseconds = (int)Clock.ToSeconds(remaining).ClampMilliseconds();
                    if (milliseconds > 0) Thread.Sleep(milliseconds);
                }
            }
            else if (remaining < -_ticksPerFrame)
            {
                Interlocked.Increment(ref _lateTicks);
            }

            if (token.IsCancellationRequested) break;

            try
            {
                // The frame is stamped with its *scheduled* time, not the moment it was serviced.
                // That is what makes the output exactly constant-rate and lets audio share the grid.
                _onTick(index, deadline);
            }
            catch (Exception ex)
            {
                Log.Error("Frame pacer tick failed", ex);
            }

            Interlocked.Increment(ref _frameIndex);
        }

        Log.Info($"Pacer '{_thread.Name}' stopped after {_frameIndex} ticks " +
                 $"({_lateTicks} late, {_framesSkippedForDrift} skipped for drift) at {_framesPerSecond} fps.");
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
    }

#if WINDOWS
    /// <summary>
    /// A high-resolution waitable timer, when the OS grants one. Sub-millisecond wait accuracy is
    /// what keeps <see cref="LateTicks"/> near zero at high frame rates; <see cref="TryWaitPrecise"/>
    /// falls back to <see cref="Thread.Sleep(int)"/> pacing when this comes back unavailable.
    /// </summary>
    private static SafeFileHandle? CreateTimer()
    {
        var timer = PInvoke.CreateWaitableTimerEx(
            (Windows.Win32.Security.SECURITY_ATTRIBUTES?)null,
            lpTimerName: null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);

        if (timer is null || timer.IsInvalid)
        {
            Log.Warn("High-resolution waitable timer unavailable; falling back to Thread.Sleep pacing.");
            return null;
        }

        return timer;
    }

    /// <summary>Waits out <paramref name="remainingTicks"/> on the high-res timer. False if none is available.</summary>
    private static unsafe bool TryWaitPrecise(SafeFileHandle? timer, long remainingTicks)
    {
        if (timer is null) return false;

        // Negative due time means "relative", in 100 ns units - the same unit the whole pipeline
        // already uses, so no conversion is needed.
        var dueTime = -remainingTicks;
        if (!PInvoke.SetWaitableTimerEx(new HANDLE(timer.DangerousGetHandle()), &dueTime, 0, null, null, null, 0))
            return false;

        PInvoke.WaitForSingleObject(new HANDLE(timer.DangerousGetHandle()), 1000);
        return true;
    }
#else
    /// <summary>
    /// No high-resolution timer on this platform yet — every tick falls back to
    /// <see cref="Thread.Sleep(int)"/> pacing. A Linux backend should replace this with a
    /// <c>clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, ...)</c> wait for parity with Windows'
    /// sub-ms accuracy; see the Linux support plan's timing phase.
    /// </summary>
    private static IDisposable? CreateTimer() => null;

    private static bool TryWaitPrecise(IDisposable? timer, long remainingTicks) => false;
#endif
}

internal static class PacerMath
{
    /// <summary>Rounds a sub-second wait up to whole milliseconds without overshooting a frame.</summary>
    public static double ClampMilliseconds(this double seconds) => Math.Floor(seconds * 1000.0);
}
