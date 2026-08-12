using ReplayCapture.Core.Audio;

namespace ReplayCapture.Tests;

public class AudioTrackBufferTests
{
    private const long Epoch = 1_000_000_000;
    private const int Window = 5;

    private static AudioTrackBuffer NewBuffer(double gain = 1.0) =>
        new("test", Epoch, Window, gain);

    private static long QpcAtFrame(long frame) => Epoch + AudioFormat.FramesToTicks(frame);

    /// <summary>Interleaved stereo block where every sample has the same value.</summary>
    private static float[] Block(int frames, float value)
    {
        var samples = new float[frames * AudioFormat.Channels];
        Array.Fill(samples, value);
        return samples;
    }

    private static short[] Read(AudioTrackBuffer buffer, long startFrame, int frames)
    {
        var destination = new short[frames * AudioFormat.Channels];
        buffer.ReadPcm16(QpcAtFrame(startFrame), frames, destination);
        return destination;
    }

    [Fact]
    public void Samples_land_at_the_position_their_timestamp_implies()
    {
        var buffer = NewBuffer();
        buffer.Accumulate(QpcAtFrame(1000), Block(100, 0.5f));

        var read = Read(buffer, 1000, 100);

        Assert.All(read, sample => Assert.Equal(short.MaxValue / 2, sample, tolerance: 2));
    }

    [Fact]
    public void A_gap_between_writes_reads_as_silence_rather_than_being_compacted()
    {
        // This is the whole reason the buffer is addressed by timeline instead of by queue.
        // A queue would butt these two blocks together and slide the track out of sync with video.
        var buffer = NewBuffer();
        buffer.Accumulate(QpcAtFrame(0), Block(100, 0.5f));
        buffer.Accumulate(QpcAtFrame(5000), Block(100, 0.5f));

        var gap = Read(buffer, 2000, 100);
        Assert.All(gap, sample => Assert.Equal(0, sample));

        var second = Read(buffer, 5000, 100);
        Assert.All(second, sample => Assert.Equal(short.MaxValue / 2, sample, tolerance: 2));
    }

    [Fact]
    public void Two_sources_on_one_track_are_summed()
    {
        // "Desktop + Mic" is a real mix, not a special case.
        var buffer = NewBuffer();
        buffer.Accumulate(QpcAtFrame(500), Block(50, 0.25f));
        buffer.Accumulate(QpcAtFrame(500), Block(50, 0.25f));

        var read = Read(buffer, 500, 50);

        Assert.All(read, sample => Assert.Equal(short.MaxValue / 2, sample, tolerance: 2));
    }

    [Fact]
    public void Summing_past_full_scale_clamps_instead_of_wrapping()
    {
        // Wrapping would turn a loud moment into white noise, which is far worse than clipping.
        var buffer = NewBuffer();
        for (var i = 0; i < 4; i++) buffer.Accumulate(QpcAtFrame(0), Block(20, 0.5f));

        var read = Read(buffer, 0, 20);

        Assert.All(read, sample => Assert.Equal(short.MaxValue, sample));
    }

    [Fact]
    public void Negative_overflow_clamps_too()
    {
        var buffer = NewBuffer();
        for (var i = 0; i < 4; i++) buffer.Accumulate(QpcAtFrame(0), Block(20, -0.5f));

        Assert.All(Read(buffer, 0, 20), sample => Assert.Equal(short.MinValue, sample));
    }

    [Fact]
    public void Gain_scales_the_track()
    {
        var buffer = NewBuffer(gain: 0.5);
        buffer.Accumulate(QpcAtFrame(0), Block(20, 1.0f));

        Assert.All(Read(buffer, 0, 20), sample => Assert.Equal(short.MaxValue / 2, sample, tolerance: 2));
    }

    [Fact]
    public void Reading_a_track_that_never_produced_anything_is_silent()
    {
        // Game, Communications and Music behave exactly like this until M4 wires up process loopback.
        var buffer = NewBuffer();
        buffer.AdvanceTo(QpcAtFrame(48_000));

        Assert.All(Read(buffer, 0, 1000), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Reading_beyond_the_silence_frontier_is_silent()
    {
        var buffer = NewBuffer();
        buffer.Accumulate(QpcAtFrame(0), Block(100, 0.9f));

        // Nothing has been written this far ahead, so it must not return recycled ring contents.
        Assert.All(Read(buffer, 100_000, 100), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Data_older_than_the_window_reads_as_silence_not_as_stale_audio()
    {
        var buffer = NewBuffer();
        buffer.Accumulate(QpcAtFrame(0), Block(1000, 0.9f));

        // Push the frontier far past the retention window so the original write is aged out.
        buffer.AdvanceTo(QpcAtFrame(AudioFormat.SampleRate * (Window + 5)));

        Assert.All(Read(buffer, 0, 500), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Accumulating_far_in_the_past_is_dropped_rather_than_corrupting_current_audio()
    {
        var buffer = NewBuffer();
        buffer.AdvanceTo(QpcAtFrame(AudioFormat.SampleRate * (Window + 3)));

        buffer.Accumulate(QpcAtFrame(0), Block(100, 0.9f));

        Assert.True(buffer.FramesDroppedTooOld > 0);
    }

    [Fact]
    public void Frame_and_tick_conversions_round_trip()
    {
        var buffer = NewBuffer();

        foreach (var frame in (ReadOnlySpan<long>)[0, 1, 47_999, 48_000, 1_234_567])
            Assert.Equal(frame, buffer.QpcToFrame(buffer.FrameToQpc(frame)));
    }

    [Fact]
    public void Wrapping_the_ring_keeps_recent_audio_intact()
    {
        var buffer = NewBuffer();
        var capacityFrames = AudioFormat.SampleRate * (Window + 2);

        // Write continuously well past one full lap of the ring.
        for (var frame = 0L; frame < capacityFrames * 2; frame += 480)
            buffer.Accumulate(QpcAtFrame(frame), Block(480, 0.5f));

        var recent = Read(buffer, capacityFrames * 2 - 4800, 4800);
        Assert.All(recent, sample => Assert.Equal(short.MaxValue / 2, sample, tolerance: 2));
    }
}
