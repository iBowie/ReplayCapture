using ReplayCapture.Core.Buffering;
using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Encoders;
using ReplayCapture.Core.Timing;

namespace ReplayCapture.Core;

/// <summary>
/// One display's complete always-on pipeline: capture, pace, encode, buffer — and, on demand,
/// write the buffered window out as a <c>.mov</c>.
/// <para>
/// Generic over the backend's native frame handle (<typeparamref name="TFrame"/> — a D3D11 texture
/// for the Windows backends, a VAAPI surface for a future Linux one), so this orchestration —
/// pacing, rebuild-on-resize, ring buffering — is written once and shared by every platform rather
/// than duplicated per backend. The capture source and encoder themselves are supplied as factories
/// rather than built from a concrete GPU context and backend enum, which is what keeps this class
/// from needing to know anything platform-specific at all.
/// </para>
/// <para>
/// Each display gets its own instance. On Windows they share a <see cref="D3DContext"/> but nothing
/// else, so displays of different resolutions and refresh rates run independently, and on Blackwell
/// the two NVENC engines encode them in parallel.
/// </para>
/// </summary>
public sealed class DisplayRecorder<TFrame> : IDisplayRecorder
{
    private readonly IDisplayCaptureSource<TFrame> _capture;
    private readonly Func<int, int, IVideoEncoder<TFrame>> _encoderFactory;
    private readonly PacketRingBuffer _ring;
    private readonly FramePacer _pacer;
    private readonly Lock _encoderGate = new();

    private IVideoEncoder<TFrame> _encoder;

    /// <summary>Set when the display changed size; the pacer rebuilds the encoder on its next tick.</summary>
    private volatile bool _rebuildRequested;

    private long _duplicateFrames;
    private long _rebuilds;
    private bool _disposed;

    public DisplayInfo Display { get; }
    public int FramesPerSecond { get; }

    public long FramesEncoded => _encoder.FramesEncoded;
    public long FramesArrived => _capture.FramesArrived;

    /// <summary>True once the backend has closed this display's capture and it cannot be resumed in place.</summary>
    public bool IsCaptureClosed => _capture.IsClosed;

    /// <summary>Frames the pacer had to invent because the screen had not changed.</summary>
    public long DuplicateFrames => Interlocked.Read(ref _duplicateFrames);

    public double SecondsBuffered => _ring.SecondsBuffered;
    public long BufferedBytes => _ring.Bytes;
    public long LateTicks => _pacer.LateTicks;

    /// <summary>
    /// Frames skipped to keep this display's schedule from drifting behind real time. A nonzero,
    /// climbing count means the configured frame rate is not sustainable under the current load.
    /// </summary>
    public long FramesSkippedForDrift => _pacer.FramesSkippedForDrift;

    /// <summary>How many times the encoder had to be rebuilt after a resolution change.</summary>
    public long Rebuilds => Interlocked.Read(ref _rebuilds);

    /// <param name="captureFactory">Builds the capture source. Called once, at construction time.</param>
    /// <param name="encoderFactory">
    /// Builds the encoder for a given size. Called at construction time and again on every rebuild,
    /// so it must capture whatever else the backend needs (a GPU context, bitrate, ...) itself.
    /// </param>
    public DisplayRecorder(
        DisplayInfo display, int framesPerSecond, int bufferSeconds, long memoryLimitBytes,
        Func<IDisplayCaptureSource<TFrame>> captureFactory, Func<int, int, IVideoEncoder<TFrame>> encoderFactory)
    {
        _encoderFactory = encoderFactory;
        Display = display;
        FramesPerSecond = framesPerSecond;

        _capture = captureFactory();
        _capture.ContentSizeChanged += OnContentSizeChanged;

        var size = _capture.ContentSize;

        _encoder = encoderFactory(size.Width, size.Height);
        _ring = new PacketRingBuffer(bufferSeconds, memoryLimitBytes);
        _encoder.PacketReady += OnPacketReady;

        _pacer = new FramePacer(FramesPerSecond, OnTick, display.DeviceName.Replace(@"\\.\", ""));
    }

    public void RequestRebuild() => _rebuildRequested = true;

    private void OnContentSizeChanged(Windows.Graphics.SizeInt32 size)
    {
        // Raised on a capture callback thread. Only flag it here — rebuilding the encoder from this
        // thread would race the pacer mid-encode.
        Log.Info($"{Display.DeviceName} changed to {size.Width}x{size.Height}; encoder will rebuild.");
        _rebuildRequested = true;
    }

    /// <summary>
    /// Rebuilds the capture pool and encoder at the display's new size.
    /// <para>
    /// The ring buffer is discarded rather than kept. A single <c>.mov</c> track cannot change
    /// resolution partway through, and the H.264 parameter sets are dimension-specific, so mixing
    /// packets from before and after a resolution change would produce a file no decoder can read.
    /// Losing the buffered history is the correct trade against writing a corrupt clip.
    /// </para>
    /// </summary>
    private void Rebuild()
    {
        lock (_encoderGate)
        {
            var size = _capture.ContentSize;

            try
            {
                _capture.Recreate(size);

                _encoder.PacketReady -= OnPacketReady;
                _encoder.Dispose();

                _encoder = _encoderFactory(size.Width, size.Height);
                _encoder.PacketReady += OnPacketReady;

                _ring.Clear();
                _lastCapturedQpc = -1;

                Interlocked.Increment(ref _rebuilds);
                Log.Info($"{Display.DeviceName}: encoder rebuilt at {size.Width}x{size.Height}; buffer reset.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to rebuild the pipeline for {Display.DeviceName}", ex);
                throw;
            }
        }
    }

    public void Start()
    {
        _pacer.Start();
        Log.Info($"Recorder started for {Display.DeviceName} at {FramesPerSecond} fps.");
    }

    private void OnTick(long frameIndex, long scheduledQpc)
    {
        if (_disposed) return;

        if (_rebuildRequested)
        {
            _rebuildRequested = false;
            try
            {
                Rebuild();
            }
            catch
            {
                // Rebuild logged the cause. Leave the flag clear so the pacer keeps ticking rather
                // than spinning on a failure that will not resolve itself.
                return;
            }
        }

        if (!_capture.TryGetLatest(out var frame, out var capturedQpc))
        {
            // Nothing captured yet. Emitting a black frame here would put a black flash in the
            // clip, so the grid simply starts when the first real frame lands.
            return;
        }

        if (capturedQpc == _lastCapturedQpc) Interlocked.Increment(ref _duplicateFrames);
        _lastCapturedQpc = capturedQpc;

        try
        {
            lock (_encoderGate)
            {
                _encoder.Encode(frame, frameIndex, scheduledQpc);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Encode failed for {Display.DeviceName}", ex);
        }
    }

    private long _lastCapturedQpc = -1;

    private void OnPacketReady(ReadOnlySpan<byte> data, long frameIndex, long qpcTicks, bool isKeyframe) =>
        _ring.Add(EncodedPacket.Rent(data, frameIndex, qpcTicks, isKeyframe));

    public int Width => _encoder.Width;
    public int Height => _encoder.Height;
    public byte[] ExtraData => _encoder.ExtraData;

    /// <summary>
    /// Takes the buffered window without writing anything. Capture and encoding continue
    /// throughout, so triggering a save never creates a gap in the buffer.
    /// </summary>
    public IReadOnlyList<ClipPacket> Snapshot(long nowTicks, int seconds) => _ring.Snapshot(nowTicks, seconds);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pacer.Dispose();
        _capture.ContentSizeChanged -= OnContentSizeChanged;

        lock (_encoderGate)
        {
            _encoder.PacketReady -= OnPacketReady;
            _encoder.Dispose();
        }

        _capture.Dispose();
        _ring.Clear();
    }
}
