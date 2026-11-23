using ReplayCapture.Core.Audio.Interop;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// The shared capture loop behind both endpoint capture and per-process loopback.
/// <para>
/// Both source kinds differ only in how their <c>IAudioClient</c> is obtained; once running, the
/// draining, silence handling and timestamping are identical, and that logic is subtle enough that
/// having two copies would guarantee they drift apart.
/// </para>
/// </summary>
internal sealed class WasapiCaptureLoop : IDisposable
{
    private const uint BufferflagsDataDiscontinuity = 0x1;
    private const uint BufferflagsSilent = 0x2;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _captureClient;
    private readonly AudioResampler _resampler;
    private readonly Action<long, ReadOnlyMemory<float>> _onSamples;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly float[] _scratch;
    private readonly string _name;

    /// <summary>
    /// How far the running frame count may drift from the device's own reported position before
    /// being corrected — the audio equivalent of <see cref="Timing.FramePacer.MaxDriftTicks"/>.
    /// <para>
    /// Comfortably above the few-frames jitter an ordinary continuous packet's timestamp shows (see
    /// <see cref="ResolveFrameStart"/> — that noise is what this loop is built to ignore), but far
    /// below anything audible. WASAPI's discontinuity flag fires for a buffer over/underrun, never
    /// for the audio hardware's sample clock simply running at a slightly different rate than the
    /// nominal <see cref="AudioFormat.SampleRate"/> this loop assumes for every continuous packet —
    /// two independent oscillators essentially never agree to the part-per-million. Without this,
    /// that mismatch accumulates for as long as the endpoint stays glitch-free, which on a
    /// generously-buffered source can be the whole session, and the saved clip's audio drifts
    /// further from the shared QPC timeline the longer the app has been armed.
    /// </para>
    /// </summary>
    internal const long MaxDriftFrames = AudioFormat.SampleRate * 40 / 1000; // 40 ms

    private long _framesCaptured;
    private long _discontinuities;
    private long _driftResyncs;
    private long _nextFrame = long.MinValue;
    private bool _disposed;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    /// <summary>
    /// Times this loop has re-anchored to the device's reported position outside of a flagged
    /// discontinuity, because the running frame count had drifted more than
    /// <see cref="MaxDriftFrames"/> from it. Zero on a device whose clock genuinely matches the
    /// nominal rate; a climbing count over a long session means it does not, and this is what keeps
    /// that mismatch from growing without bound.
    /// </summary>
    public long DriftResyncs => Interlocked.Read(ref _driftResyncs);

    /// <summary>
    /// Picks the frame position a packet should be written at: right after the previous packet
    /// unless WASAPI itself flagged this one as discontinuous.
    /// <para>
    /// The device position WASAPI attaches to each packet is precise enough to place the very first
    /// packet, but is not a reliable clock to re-derive position from on every subsequent one — on a
    /// render-loopback endpoint mixing several concurrently active clients it jitters by a handful of
    /// frames in both directions, without any samples actually having been gained or lost. Comparing
    /// timestamps to detect overlaps or gaps therefore chases noise: trimming a "duplicate" that was
    /// actually new data drops real audio (an audible silent tick), and keeping an "overlap" that was
    /// actually just jitter double-sums real audio (an audible amplitude spike) — both sound like the
    /// exact click this exists to remove. WASAPI's own contract for packet continuity is the
    /// discontinuity flag, not the timestamp: absent that flag, it is asserting the packet picks up
    /// exactly where the last one left off, so that assertion — not the
    /// jittery timestamp — is what should place it. The timestamp is trusted again only when the flag
    /// says the stream really did skip, which is when a resync (and the silence that fills the real
    /// gap) is actually correct.
    /// </para>
    /// </summary>
    internal static long ResolveFrameStart(long nextFrame, long reportedFrameStart, bool discontinuous) =>
        discontinuous || nextFrame == long.MinValue ? reportedFrameStart : nextFrame;

    /// <summary>
    /// True when the running frame count has drifted more than <paramref name="maxDriftFrames"/>
    /// from the device's own reported position on an otherwise-continuous packet. Pure and
    /// side-effect-free so the threshold can be verified without a real device; see
    /// <see cref="MaxDriftFrames"/> for why this check exists alongside
    /// <see cref="ResolveFrameStart"/> rather than folded into it.
    /// </summary>
    internal static bool HasDrifted(long nextFrame, long reportedFrameStart, long maxDriftFrames) =>
        nextFrame != long.MinValue && Math.Abs(reportedFrameStart - nextFrame) > maxDriftFrames;

    public WasapiCaptureLoop(
        IAudioClient client,
        IAudioCaptureClient captureClient,
        AudioResampler resampler,
        string name,
        Action<long, ReadOnlyMemory<float>> onSamples)
    {
        _client = client;
        _captureClient = captureClient;
        _resampler = resampler;
        _name = name;
        _onSamples = onSamples;

        // Half a second of headroom at the canonical rate covers any plausible endpoint burst.
        _scratch = new float[AudioFormat.SampleRate / 2 * AudioFormat.Channels];

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"audio-{name}",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start()
    {
        _client.Start();
        _thread.Start();
    }

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                Drain();
            }
            catch (Exception ex)
            {
                Log.Error($"Audio capture failed for '{_name}'", ex);
                Thread.Sleep(200);
            }

            // Polling rather than event-driven. WASAPI's event simply stops firing on a loopback
            // endpoint while nothing is playing, and a 5 ms poll costs far less than that bug class.
            Thread.Sleep(5);
        }

        try { _client.Stop(); } catch { /* the endpoint or process may already be gone */ }
    }

    private unsafe void Drain()
    {
        while (true)
        {
            _captureClient.GetNextPacketSize(out var packetFrames);
            if (packetFrames == 0) return;

            ulong devicePosition;
            ulong qpcPosition;

            _captureClient.GetBuffer(out var dataPtr, out var framesAvailable, out var flags, &devicePosition, &qpcPosition);
            var data = (byte*)dataPtr;

            try
            {
                if (framesAvailable == 0) continue;

                var discontinuous = (flags & BufferflagsDataDiscontinuity) != 0;
                if (discontinuous)
                {
                    // Not fatal. Because tracks are addressed by timeline, a gap becomes silence in
                    // the right place rather than shifting everything that follows.
                    Interlocked.Increment(ref _discontinuities);
                }

                // Already QPC in 100 ns units, which is the pipeline's native unit.
                var qpcTicks = (long)qpcPosition;

                int produced;
                if ((flags & BufferflagsSilent) != 0)
                {
                    // Buffer contents are undefined when SILENT is set; the correct reading is
                    // digital silence, not whatever memory happened to hold.
                    produced = (int)Math.Min(framesAvailable, (uint)(_scratch.Length / AudioFormat.Channels));
                    Array.Clear(_scratch, 0, produced * AudioFormat.Channels);
                }
                else
                {
                    produced = _resampler.Convert(data, (int)framesAvailable, _scratch);
                }

                var reportedFrameStart = AudioFormat.TicksToFrames(qpcTicks);

                // A flagged discontinuity already forces a resync to the reported position; only
                // check for chronic drift outside of that, or every genuine gap would also count as
                // one of these.
                var driftResync = !discontinuous && HasDrifted(_nextFrame, reportedFrameStart, MaxDriftFrames);
                if (driftResync)
                {
                    var resyncs = Interlocked.Increment(ref _driftResyncs);
                    if (resyncs == 1 || resyncs % 20 == 0)
                    {
                        var driftMs = (reportedFrameStart - _nextFrame) * 1000.0 / AudioFormat.SampleRate;
                        Log.Warn($"Audio source '{_name}' drifted {driftMs:0.0} ms from its own device " +
                                 $"clock; resynced ({resyncs} total).");
                    }
                }

                var frameStart = ResolveFrameStart(_nextFrame, reportedFrameStart, discontinuous || driftResync);

                if (produced > 0)
                {
                    _onSamples(AudioFormat.FramesToTicks(frameStart), _scratch.AsMemory(0, produced * AudioFormat.Channels));
                    Interlocked.Add(ref _framesCaptured, produced);
                }

                _nextFrame = frameStart + produced;
            }
            finally
            {
                _captureClient.ReleaseBuffer(framesAvailable);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellation.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
        _resampler.Dispose();

        if (_discontinuities > 0)
            Log.Warn($"Audio source '{_name}' saw {_discontinuities} discontinuities.");
        if (_driftResyncs > 0)
            Log.Warn($"Audio source '{_name}' needed {_driftResyncs} clock-drift resync(s).");
    }
}
