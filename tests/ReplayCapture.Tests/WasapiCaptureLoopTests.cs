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

    [Fact]
    public void Ordinary_per_packet_jitter_is_not_seen_as_drift()
    {
        // The same handful-of-frames jitter that ResolveFrameStart is built to ignore must not trip
        // the drift check either, or the two would fight over every single packet.
        var drifted = WasapiCaptureLoop.HasDrifted(
            nextFrame: 96_000, reportedFrameStart: 95_997, WasapiCaptureLoop.MaxDriftFrames);

        Assert.False(drifted);
    }

    [Fact]
    public void A_gap_right_at_the_drift_budget_is_not_yet_a_drift()
    {
        var drifted = WasapiCaptureLoop.HasDrifted(
            nextFrame: 96_000, reportedFrameStart: 96_000 + WasapiCaptureLoop.MaxDriftFrames,
            WasapiCaptureLoop.MaxDriftFrames);

        Assert.False(drifted);
    }

    [Fact]
    public void Chronic_clock_mismatch_past_the_budget_is_reported_as_drift()
    {
        // No discontinuity ever fired for this gap (WASAPI only flags real over/underruns), but the
        // running count has quietly fallen this far behind the device's own reported position —
        // exactly what a slow clock-rate mismatch between the audio hardware and the nominal 48 kHz
        // this loop assumes looks like.
        var drifted = WasapiCaptureLoop.HasDrifted(
            nextFrame: 96_000, reportedFrameStart: 96_000 + WasapiCaptureLoop.MaxDriftFrames + 1,
            WasapiCaptureLoop.MaxDriftFrames);

        Assert.True(drifted);
    }

    [Fact]
    public void Drift_is_detected_in_either_direction()
    {
        var drifted = WasapiCaptureLoop.HasDrifted(
            nextFrame: 96_000, reportedFrameStart: 96_000 - WasapiCaptureLoop.MaxDriftFrames - 1,
            WasapiCaptureLoop.MaxDriftFrames);

        Assert.True(drifted);
    }

    [Fact]
    public void The_very_first_packet_is_never_reported_as_drifted()
    {
        // There is nothing yet to have drifted from; ResolveFrameStart already handles this packet
        // by trusting the reported position outright.
        var drifted = WasapiCaptureLoop.HasDrifted(
            nextFrame: long.MinValue, reportedFrameStart: 500_000, WasapiCaptureLoop.MaxDriftFrames);

        Assert.False(drifted);
    }

    [Fact]
    public void A_detected_drift_resyncs_the_frame_start_to_the_devices_reported_position()
    {
        // Mirrors how Drain() combines the two: once HasDrifted trips, ResolveFrameStart is called
        // exactly as it is for a flagged discontinuity.
        const long nextFrame = 96_000;
        const long reportedFrameStart = 96_000 + WasapiCaptureLoop.MaxDriftFrames + 1;

        var drifted = WasapiCaptureLoop.HasDrifted(nextFrame, reportedFrameStart, WasapiCaptureLoop.MaxDriftFrames);
        var frameStart = WasapiCaptureLoop.ResolveFrameStart(nextFrame, reportedFrameStart, discontinuous: drifted);

        Assert.Equal(reportedFrameStart, frameStart);
    }
}
