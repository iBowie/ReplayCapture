using ReplayCapture.Core.Audio;

namespace ReplayCapture.Tests;

public class WasapiCaptureLoopTests
{
    [Fact]
    public void The_first_packet_always_uses_its_own_reported_position()
    {
        var frameStart = WasapiCaptureLoop.ResolveFrameStart(
            nextFrame: long.MinValue, reportedFrameStart: 4800, discontinuous: false);

        Assert.Equal(4800, frameStart);
    }

    [Fact]
    public void A_continuous_packet_is_placed_right_after_the_previous_one_regardless_of_its_own_jittery_timestamp()
    {
        // The device's own timestamp for this packet claims it started a few frames early — normal
        // jitter on a render-loopback endpoint mixing several clients, not a real gap or overlap.
        // Trusting that jitter instead of continuity is exactly what produced audible clicks: either
        // dropping real frames (trim-as-overlap) or double-summing them (accept-as-overlap).
        var frameStart = WasapiCaptureLoop.ResolveFrameStart(
            nextFrame: 96_000, reportedFrameStart: 95_997, discontinuous: false);

        Assert.Equal(96_000, frameStart);
    }

    [Fact]
    public void A_continuous_packet_is_placed_after_the_previous_one_even_if_its_timestamp_jitters_late()
    {
        var frameStart = WasapiCaptureLoop.ResolveFrameStart(
            nextFrame: 96_000, reportedFrameStart: 96_004, discontinuous: false);

        Assert.Equal(96_000, frameStart);
    }

    [Fact]
    public void A_flagged_discontinuity_resyncs_to_the_devices_reported_position()
    {
        // WASAPI is asserting a real gap here, so the reported position is trusted again — this is
        // what lets the gap read back as silence in the right place instead of being papered over.
        var frameStart = WasapiCaptureLoop.ResolveFrameStart(
            nextFrame: 96_000, reportedFrameStart: 150_000, discontinuous: true);

        Assert.Equal(150_000, frameStart);
    }
}
