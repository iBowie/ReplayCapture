using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Timing;

namespace ReplayCapture.Core.Buffering;

/// <summary>
/// Rolling window of encoded video packets covering at least the configured number of seconds.
/// <para>
/// A saved clip has to begin on an IDR frame — a decoder cannot start anywhere else — so the buffer
/// never discards a packet that is still needed to reach the oldest IDR covering the window. With a
/// one-second GOP that means a 60-second request yields 60 to 61 seconds, never less.
/// </para>
/// </summary>
public sealed class PacketRingBuffer
{
    private readonly Lock _gate = new();
    private readonly Queue<EncodedPacket> _packets = new();
    private readonly long _windowTicks;
    private readonly long _memoryLimitBytes;

    private long _bytes;
    private long _droppedForMemory;

    public PacketRingBuffer(int windowSeconds, long memoryLimitBytes)
    {
        _windowTicks = Clock.FromSeconds(windowSeconds);
        _memoryLimitBytes = memoryLimitBytes;
    }

    /// <summary>Current payload bytes held. Watched by the UI and by the memory-cap logic.</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }

    public int Count { get { lock (_gate) return _packets.Count; } }

    /// <summary>Seconds of history currently retained.</summary>
    public double SecondsBuffered
    {
        get
        {
            lock (_gate)
            {
                if (_packets.Count < 2) return 0;
                return Clock.ToSeconds(_packets.Last().QpcTicks - _packets.Peek().QpcTicks);
            }
        }
    }

    public void Add(EncodedPacket packet)
    {
        lock (_gate)
        {
            _packets.Enqueue(packet);
            _bytes += packet.Length;
            Trim(packet.QpcTicks);
        }
    }

    private void Trim(long nowTicks)
    {
        var cutoff = nowTicks - _windowTicks;

        // Drop whole GOPs only, and only once the *following* GOP already covers the window.
        //
        // Dropping the leading GOP as soon as its own keyframe ages out is subtly wrong: the buffer
        // then starts at a keyframe newer than the cutoff, and a save can only begin there, so the
        // clip comes out short. Retaining one extra GOP is what makes a 60-second request yield
        // 60-61 seconds instead of 59.
        while (true)
        {
            var leadingGopLength = FindSecondKeyframe(out var secondKeyframeTicks);
            if (leadingGopLength < 0 || secondKeyframeTicks > cutoff) break;

            for (var i = 0; i < leadingGopLength; i++)
            {
                var packet = _packets.Dequeue();
                _bytes -= packet.Length;
                packet.Return();
            }
        }

        // Safety valve: a stalled save or a pathological bitrate spike must not be allowed to grow
        // the buffer without bound.
        while (_bytes > _memoryLimitBytes && _packets.Count > 1)
        {
            var oldest = _packets.Dequeue();
            _bytes -= oldest.Length;
            oldest.Return();
            _droppedForMemory++;
        }

        if (_droppedForMemory > 0 && _droppedForMemory % 600 == 0)
            Log.Warn($"Ring buffer hit its {_memoryLimitBytes / (1024 * 1024)} MB cap; dropped {_droppedForMemory} packets.");
    }

    /// <summary>
    /// Returns how many packets make up the leading GOP — i.e. the offset of the second keyframe —
    /// or -1 when the buffer does not yet hold two keyframes.
    /// </summary>
    private int FindSecondKeyframe(out long secondKeyframeTicks)
    {
        secondKeyframeTicks = 0;
        var index = 0;
        var keyframes = 0;

        foreach (var packet in _packets)
        {
            if (packet.IsKeyframe && ++keyframes == 2)
            {
                secondKeyframeTicks = packet.QpcTicks;
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>
    /// Takes a snapshot of the clip to write, starting at the newest IDR that still covers
    /// <paramref name="windowSeconds"/> of history. Returns an empty list if no IDR has been seen.
    /// <para>
    /// The payloads are <b>copied</b> rather than referenced. Buffering continues during a save, so
    /// a referenced packet could be aged out and returned to the array pool — then overwritten by a
    /// new frame — while the muxer was still reading it. Copying costs one memcpy of the clip
    /// (tens of milliseconds) and removes the race entirely.
    /// </para>
    /// </summary>
    public IReadOnlyList<ClipPacket> Snapshot(long nowTicks, int windowSeconds)
    {
        lock (_gate)
        {
            if (_packets.Count == 0) return [];

            var target = nowTicks - Clock.FromSeconds(windowSeconds);
            var all = _packets.ToArray();

            var start = -1;
            for (var i = 0; i < all.Length; i++)
            {
                if (!all[i].IsKeyframe) continue;
                if (all[i].QpcTicks <= target) start = i;   // newest IDR at or before the target
                else if (start < 0) { start = i; break; }   // buffer is younger than requested
            }

            if (start < 0)
            {
                Log.Warn("Save requested but the buffer contains no keyframe yet.");
                return [];
            }

            var clip = new List<ClipPacket>(all.Length - start);
            for (var i = start; i < all.Length; i++)
            {
                clip.Add(new ClipPacket(
                    all[i].Span.ToArray(),
                    all[i].FrameIndex,
                    all[i].QpcTicks,
                    all[i].IsKeyframe));
            }

            return clip;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_packets.Count > 0) _packets.Dequeue().Return();
            _bytes = 0;
        }
    }
}
