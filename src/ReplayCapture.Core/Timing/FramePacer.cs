using ReplayCapture.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

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
/// "now", so scheduling jitter cannot accumulate into long-term drift.
/// </para>
/// </summary>
public sealed class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly int _framesPerSecond;
    private readonly long _ticksPerFrame;
    private readonly Action<long, long> _onTick;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();

    private long _frameIndex;
    private long _lateTicks;

    /// <summary>Ticks whose deadline had already passed when they were serviced.</summary>
    public long LateTicks => Interlocked.Read(ref _lateTicks);

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

    private unsafe void Run()
    {
        using var timer = PInvoke.CreateWaitableTimerEx(
            (Windows.Win32.Security.SECURITY_ATTRIBUTES?)null,
            lpTimerName: null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);

        if (timer is null || timer.IsInvalid)
        {
            Log.Warn("High-resolution waitable timer unavailable; falling back to Thread.Sleep pacing.");
        }

        var start = Clock.Now;
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            var index = _frameIndex;
            var deadline = start + index * _ticksPerFrame;
            var now = Clock.Now;
            var remaining = deadline - now;

            if (remaining > 0)
            {
                if (timer is { IsInvalid: false })
                {
                    // Negative due time means "relative", in 100 ns units - the same unit the whole
                    // pipeline already uses, so no conversion is needed.
                    var dueTime = -remaining;
                    if (PInvoke.SetWaitableTimerEx(new HANDLE(timer.DangerousGetHandle()), &dueTime, 0, null, null, null, 0))
                    {
                        PInvoke.WaitForSingleObject(new HANDLE(timer.DangerousGetHandle()), 1000);
                    }
                }
                else
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
                 $"({_lateTicks} late) at {_framesPerSecond} fps.");
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
    }
}

internal static class PacerMath
{
    /// <summary>Rounds a sub-second wait up to whole milliseconds without overshooting a frame.</summary>
    public static double ClampMilliseconds(this double seconds) => Math.Floor(seconds * 1000.0);
}
