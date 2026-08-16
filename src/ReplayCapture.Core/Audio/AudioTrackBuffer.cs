using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// One audio track's rolling window, addressed by absolute position on the shared QPC timeline
/// rather than by arrival order.
/// <para>
/// Addressing by timeline instead of by queue solves three problems at once:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Silence.</b> Neither device loopback nor per-process loopback emits anything while the
///     application is quiet — WASAPI simply stops handing out buffers. A queue would compact those
///     gaps away and the track would slide out of sync with video. Here an unwritten region is
///     silence by construction.
///   </item>
///   <item>
///     <b>Mixing.</b> Several sources can share a track (this is what makes "Desktop + Mic" a real
///     mix rather than a special case) — each simply accumulates at its own timeline position.
///   </item>
///   <item>
///     <b>Alignment.</b> Every track and every display derive their positions from the same clock,
///     so the saved files line up on a timeline with no correction.
///   </item>
/// </list>
/// </summary>
public sealed class AudioTrackBuffer
{
    private readonly Lock _gate = new();
    private readonly float[] _samples;
    private readonly long _capacityFrames;
    private readonly long _epochQpc;

    /// <summary>Absolute frame index up to which the ring holds valid (possibly silent) audio.</summary>
    private long _clearedUpToFrame;

    private long _framesAccumulated;
    private long _framesDroppedTooOld;

    public string Name { get; }
    public double Gain { get; set; }

    public long FramesAccumulated => Interlocked.Read(ref _framesAccumulated);
    public long FramesDroppedTooOld => Interlocked.Read(ref _framesDroppedTooOld);

    /// <param name="epochQpc">Shared origin for every track and display. Must be identical across them.</param>
    /// <param name="windowSeconds">Retention window; a couple of seconds of slack is added.</param>
    public AudioTrackBuffer(string name, long epochQpc, int windowSeconds, double gain = 1.0)
    {
        Name = name;
        Gain = gain;
        _epochQpc = epochQpc;
        _capacityFrames = (long)AudioFormat.SampleRate * (windowSeconds + 2);
        _samples = new float[_capacityFrames * AudioFormat.Channels];
    }

    /// <summary>Bytes this track occupies in memory.</summary>
    public long Bytes => (long)_samples.Length * sizeof(float);

    public long QpcToFrame(long qpcTicks) => AudioFormat.TicksToFrames(qpcTicks - _epochQpc);

    public long FrameToQpc(long frame) => _epochQpc + AudioFormat.FramesToTicks(frame);

    /// <summary>
    /// Mixes interleaved stereo samples into the track at the position implied by
    /// <paramref name="qpcTicks"/>. Additive, so multiple sources on one track sum naturally.
    /// </summary>
    public void Accumulate(long qpcTicks, ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty) return;

        var frames = interleaved.Length / AudioFormat.Channels;
        var startFrame = QpcToFrame(qpcTicks);

        lock (_gate)
        {
            // Anything older than the ring can hold has already been overwritten; writing it would
            // corrupt current audio.
            if (startFrame + frames <= _clearedUpToFrame - _capacityFrames)
            {
                Interlocked.Add(ref _framesDroppedTooOld, frames);
                return;
            }

            EnsureCleared(startFrame + frames);

            for (var i = 0; i < frames; i++)
            {
                var frame = startFrame + i;
                if (frame < _clearedUpToFrame - _capacityFrames) continue;

                var offset = (int)Modulo(frame, _capacityFrames) * AudioFormat.Channels;
                var source = i * AudioFormat.Channels;

                for (var channel = 0; channel < AudioFormat.Channels; channel++)
                    _samples[offset + channel] += interleaved[source + channel];
            }

            Interlocked.Add(ref _framesAccumulated, frames);
        }
    }

    /// <summary>
    /// Advances the silence frontier to <paramref name="qpcTicks"/> without writing anything, so a
    /// track whose sources are all quiet still has a well-defined present.
    /// </summary>
    public void AdvanceTo(long qpcTicks)
    {
        lock (_gate)
        {
            EnsureCleared(QpcToFrame(qpcTicks));
        }
    }

    /// <summary>Zeroes the ring from the current frontier up to <paramref name="targetFrame"/>.</summary>
    private void EnsureCleared(long targetFrame)
    {
        if (targetFrame <= _clearedUpToFrame) return;

        var toClear = targetFrame - _clearedUpToFrame;
        if (toClear >= _capacityFrames)
        {
            // Jumped further than the ring holds; nothing survivable remains.
            Array.Clear(_samples);
            _clearedUpToFrame = targetFrame;
            return;
        }

        var frame = _clearedUpToFrame;
        while (frame < targetFrame)
        {
            var position = Modulo(frame, _capacityFrames);
            var run = Math.Min(targetFrame - frame, _capacityFrames - position);
            Array.Clear(_samples, (int)(position * AudioFormat.Channels), (int)(run * AudioFormat.Channels));
            frame += run;
        }

        _clearedUpToFrame = targetFrame;
    }

    /// <summary>
    /// Reads <paramref name="frameCount"/> frames starting at <paramref name="startQpc"/> into
    /// <paramref name="destination"/> as interleaved 16-bit PCM. Regions never written, or already
    /// aged out, come back as silence rather than as stale audio.
    /// </summary>
    public void ReadPcm16(long startQpc, int frameCount, Span<short> destination)
    {
        var startFrame = QpcToFrame(startQpc);

        lock (_gate)
        {
            var oldestValid = _clearedUpToFrame - _capacityFrames;

            for (var i = 0; i < frameCount; i++)
            {
                var frame = startFrame + i;
                var target = i * AudioFormat.Channels;

                if (frame < oldestValid || frame >= _clearedUpToFrame)
                {
                    for (var channel = 0; channel < AudioFormat.Channels; channel++)
                        destination[target + channel] = 0;
                    continue;
                }

                var offset = (int)Modulo(frame, _capacityFrames) * AudioFormat.Channels;
                for (var channel = 0; channel < AudioFormat.Channels; channel++)
                    destination[target + channel] = ToPcm16(_samples[offset + channel] * Gain);
            }
        }
    }

    /// <summary>How far below full scale the soft knee in <see cref="ToPcm16"/> starts, linear.</summary>
    private const double SoftKnee = 0.891; // ~-1 dBFS

    /// <summary>
    /// Converts to 16-bit with a soft knee above <see cref="SoftKnee"/> instead of a hard clamp.
    /// <para>
    /// Endpoint loopback taps the audio engine's internal float mix bus, which is not itself clamped
    /// to unity — Windows lets it run hot when several apps (or just system sounds layered on top of
    /// one) are audible at once, and a single process's own output rarely does this alone. A hard
    /// clamp turned every one of those overs into an audible digital click; everything below the
    /// knee is untouched (identical output to the old scale-and-truncate), and only the rare sample
    /// that would have clipped gets rounded off smoothly instead, asymptotically approaching but
    /// never reaching the numeric limit.
    /// </para>
    /// </summary>
    private static short ToPcm16(double sample)
    {
        var magnitude = Math.Abs(sample);
        var limited = magnitude <= SoftKnee
            ? sample
            : Math.CopySign(SoftKnee + (1.0 - SoftKnee) * Math.Tanh((magnitude - SoftKnee) / (1.0 - SoftKnee)), sample);

        var scaled = limited * short.MaxValue;
        return scaled switch
        {
            >= short.MaxValue => short.MaxValue,
            <= short.MinValue => short.MinValue,
            _ => (short)scaled,
        };
    }

    private static long Modulo(long value, long modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    public void LogStatistics()
    {
        if (FramesDroppedTooOld > 0)
            Log.Warn($"Track '{Name}' dropped {FramesDroppedTooOld} frames that arrived too late.");
    }
}
