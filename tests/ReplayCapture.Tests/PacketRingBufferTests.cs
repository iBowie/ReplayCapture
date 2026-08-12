using ReplayCapture.Core.Buffering;
using ReplayCapture.Core.Timing;

namespace ReplayCapture.Tests;

public class PacketRingBufferTests
{
    private const int Fps = 60;
    private const int PacketBytes = 1000;

    private static readonly long TicksPerFrame = Clock.TicksPerFrame(Fps);

    /// <summary>Feeds <paramref name="frames"/> frames at 60 fps with a keyframe every GOP.</summary>
    private static void Fill(PacketRingBuffer ring, int frames, int gop = Fps, long startTicks = 0)
    {
        var payload = new byte[PacketBytes];
        for (var i = 0; i < frames; i++)
        {
            ring.Add(EncodedPacket.Rent(
                payload,
                frameIndex: i,
                qpcTicks: startTicks + i * TicksPerFrame,
                isKeyframe: i % gop == 0));
        }
    }

    private static long TicksAt(int frameIndex) => frameIndex * TicksPerFrame;

    [Fact]
    public void Retains_at_least_the_requested_window()
    {
        var ring = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 30);

        // The whole point of the buffer: it must never hold less than what was asked for.
        Assert.True(ring.SecondsBuffered >= 10.0,
            $"buffered only {ring.SecondsBuffered:0.000}s, expected at least 10s");
    }

    [Fact]
    public void Does_not_hoard_more_than_one_extra_gop()
    {
        var ring = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 30);

        // One extra GOP is the necessary cost of starting on a keyframe; two would be a leak.
        Assert.True(ring.SecondsBuffered < 11.0,
            $"buffered {ring.SecondsBuffered:0.000}s, expected under 11s");
    }

    [Fact]
    public void Saved_clip_is_never_shorter_than_the_window()
    {
        // Regression test. The first implementation dropped the leading GOP as soon as its own
        // keyframe aged out, so a 10s request produced a 9.07s clip.
        var ring = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);

        for (var totalFrames = Fps * 11; totalFrames <= Fps * 30; totalFrames += 7)
        {
            var fresh = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);
            Fill(fresh, totalFrames);

            var now = TicksAt(totalFrames - 1);
            var clip = fresh.Snapshot(now, windowSeconds: 10);

            var duration = (double)clip.Count / Fps;
            Assert.True(duration >= 10.0,
                $"after {totalFrames} frames the clip was {duration:0.000}s, expected at least 10s");
        }

        Assert.True(ring.Count == 0);
    }

    [Fact]
    public void Snapshot_always_starts_on_a_keyframe()
    {
        var ring = new PacketRingBuffer(windowSeconds: 5, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 20);

        var clip = ring.Snapshot(TicksAt(Fps * 20 - 1), windowSeconds: 5);

        Assert.NotEmpty(clip);
        // A decoder cannot start anywhere else.
        Assert.True(clip[0].IsKeyframe);
    }

    [Fact]
    public void Snapshot_returns_everything_when_the_buffer_is_younger_than_the_window()
    {
        var ring = new PacketRingBuffer(windowSeconds: 60, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 3);

        var clip = ring.Snapshot(TicksAt(Fps * 3 - 1), windowSeconds: 60);

        Assert.True(clip[0].IsKeyframe);
        // Only 3s exist, so that is what comes out - short, but not empty.
        Assert.InRange(clip.Count, Fps * 2, Fps * 3);
    }

    [Fact]
    public void Snapshot_is_empty_before_the_first_keyframe()
    {
        var ring = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);
        var payload = new byte[PacketBytes];
        for (var i = 0; i < 30; i++)
            ring.Add(EncodedPacket.Rent(payload, i, TicksAt(i), isKeyframe: false));

        Assert.Empty(ring.Snapshot(TicksAt(29), windowSeconds: 10));
    }

    [Fact]
    public void Snapshot_copies_payloads_so_the_ring_can_keep_recycling()
    {
        var ring = new PacketRingBuffer(windowSeconds: 1, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 3);

        var clip = ring.Snapshot(TicksAt(Fps * 3 - 1), windowSeconds: 1);
        var firstBytes = clip[0].Data;

        // Keep buffering hard enough that the original pooled arrays are recycled and reused.
        Fill(ring, frames: Fps * 10, startTicks: TicksAt(Fps * 3));

        Assert.Same(firstBytes, clip[0].Data);
        Assert.Equal(PacketBytes, clip[0].Data.Length);
    }

    [Fact]
    public void Memory_cap_bounds_the_buffer()
    {
        const long cap = 50 * PacketBytes;
        var ring = new PacketRingBuffer(windowSeconds: 3600, memoryLimitBytes: cap);

        // A one-hour window would otherwise grow without bound; the cap has to win.
        Fill(ring, frames: Fps * 60);

        Assert.True(ring.Bytes <= cap, $"held {ring.Bytes} bytes against a {cap}-byte cap");
    }

    [Fact]
    public void Clear_releases_everything()
    {
        var ring = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 12);

        ring.Clear();

        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Bytes);
        Assert.Empty(ring.Snapshot(TicksAt(Fps * 12), windowSeconds: 10));
    }

    [Theory]
    [InlineData(30)]   // half-second GOP
    [InlineData(60)]   // one-second GOP (shipping default)
    [InlineData(120)]  // two-second GOP
    public void Window_guarantee_holds_across_gop_lengths(int gop)
    {
        var ring = new PacketRingBuffer(windowSeconds: 10, memoryLimitBytes: long.MaxValue);
        Fill(ring, frames: Fps * 40, gop: gop);

        var clip = ring.Snapshot(TicksAt(Fps * 40 - 1), windowSeconds: 10);

        Assert.True(clip[0].IsKeyframe);
        Assert.True((double)clip.Count / Fps >= 10.0);
        // Never more than the window plus one whole GOP.
        Assert.True((double)clip.Count / Fps <= 10.0 + (double)gop / Fps + 0.05);
    }
}
