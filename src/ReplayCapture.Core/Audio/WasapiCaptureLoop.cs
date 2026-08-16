using ReplayCapture.Core.Diagnostics;
using Windows.Win32.Media.Audio;

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

    private long _framesCaptured;
    private long _discontinuities;
    private long _nextFrame = long.MinValue;
    private bool _disposed;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

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

            byte* data;
            ulong devicePosition;
            ulong qpcPosition;

            _captureClient.GetBuffer(&data, out var framesAvailable, out var flags, &devicePosition, &qpcPosition);

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
                var frameStart = ResolveFrameStart(_nextFrame, reportedFrameStart, discontinuous);

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
    }
}
