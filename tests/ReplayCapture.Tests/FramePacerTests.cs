using ReplayCapture.Core.Timing;

namespace ReplayCapture.Tests;

public class FramePacerTests
{
    private const long TicksPerFrame = 50_000; // 200 fps
    private const long MaxDriftTicks = 2_500_000; // 250 ms

    [Fact]
    public void Within_the_drift_budget_the_schedule_is_left_alone()
    {
        long index = 100;
        var deadline = index * TicksPerFrame;
        var now = deadline + MaxDriftTicks; // exactly at the budget, not over it

        var skipped = FramePacer.Resync(ref index, ref deadline, start: 0, TicksPerFrame, now, MaxDriftTicks);

        Assert.Equal(0, skipped);
        Assert.Equal(100, index);
        Assert.Equal(100 * TicksPerFrame, deadline);
    }

    [Fact]
    public void Chronic_drift_skips_whole_frames_instead_of_grinding_through_the_backlog()
    {
        long index = 100;
        var deadline = index * TicksPerFrame;
        // Six seconds behind: nowhere near catching up one tick at a time.
        var now = deadline + Clock.FromSeconds(6);

        var skipped = FramePacer.Resync(ref index, ref deadline, start: 0, TicksPerFrame, now, MaxDriftTicks);

        Assert.True(skipped > 0);
        Assert.Equal(100 + skipped, index);
    }

    [Fact]
    public void A_resync_never_leaves_the_deadline_more_than_one_frame_period_behind_now()
    {
        // The whole point of resyncing is to bound the residual drift, not just reduce it.
        long index = 0;
        var deadline = 0L;
        var now = Clock.FromSeconds(37); // an arbitrary, badly-drifted amount

        FramePacer.Resync(ref index, ref deadline, start: 0, TicksPerFrame, now, MaxDriftTicks);

        Assert.InRange(now - deadline, 0, TicksPerFrame - 1);
    }

    [Fact]
    public void A_resync_never_moves_the_deadline_into_the_future()
    {
        long index = 0;
        var deadline = 0L;
        var now = Clock.FromSeconds(3);

        FramePacer.Resync(ref index, ref deadline, start: 0, TicksPerFrame, now, MaxDriftTicks);

        Assert.True(deadline <= now);
    }

    [Fact]
    public void Resync_respects_a_nonzero_schedule_start()
    {
        var start = Clock.FromSeconds(1_000); // the pacer's own epoch, not the Unix epoch
        long index = 5;
        var deadline = start + index * TicksPerFrame;
        var now = deadline + Clock.FromSeconds(2);

        var skipped = FramePacer.Resync(ref index, ref deadline, start, TicksPerFrame, now, MaxDriftTicks);

        Assert.True(skipped > 0);
        Assert.Equal(start + index * TicksPerFrame, deadline);
    }
}
