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
/// pacing, resize handling, ring buffering — is written once and shared by every platform rather
/// than duplicated per backend. The capture source and encoder themselves are supplied as factories
/// rather than built from a concrete GPU context and backend enum, which is what keeps this class
/// from needing to know anything platform-specific at all.
/// </para>
/// <para>
/// Each display gets its own instance. On Windows they share a <see cref="D3DContext"/> but nothing
/// else, so displays of different resolutions and refresh rates run independently, and on Blackwell
/// the two NVENC engines encode them in parallel.
/// </para>
/// <para>
/// <b>The encode resolution is fixed for the recorder's whole lifetime</b> — either whatever the
/// display's native size was when this instance was constructed, or a caller-supplied override (see
/// <c>fixedEncodeSize</c> on the constructor — <see cref="Config.DisplayConfig.CaptureWidth"/> and
/// <see cref="Config.DisplayConfig.CaptureHeight"/> at the config level). A later native resolution
/// change (the monitor mode itself changing, or a re-acquire after capture access was lost landing
/// at a different size) is absorbed by <see cref="IVideoEncoder{TFrame}.NotifySourceResized"/>,
/// which scales into that fixed size on the GPU rather than tearing the encoder down. That is what
/// lets a resolution change survive without discarding the ring buffer, at the cost of the saved
/// clip being a scaled — not native-resolution — copy of whatever the display was running at the
/// time.
/// </para>
/// <para>
/// <b>A display with nothing to offer still produces continuous output.</b> When
/// <see cref="IDisplayCaptureSource{TFrame}.TryGetLatest"/> has no real frame — none has ever
/// arrived yet, or the display is temporarily unavailable — <see cref="OnTick"/> submits
/// <see cref="IDisplayCaptureSource{TFrame}.BlackFrame"/> instead of skipping the tick, so the saved
/// clip never has a silent gap; see <see cref="BlankFrames"/>.
/// </para>
/// </summary>
public sealed class DisplayRecorder<TFrame> : IDisplayRecorder
{
    private readonly IDisplayCaptureSource<TFrame> _capture;
    private readonly PacketRingBuffer _ring;
    private readonly FramePacer _pacer;
    private readonly Lock _encoderGate = new();

    private IVideoEncoder<TFrame> _encoder;

    /// <summary>Set when the display changed size; the pacer applies it on its next tick.</summary>
    private volatile bool _resizeRequested;

    private long _duplicateFrames;
    private long _blankFrames;
    private long _resizes;
    private bool _disposed;

    /// <summary>
    /// QPC tick of the last tick that had a real frame, seeded from the very first tick's
    /// <c>scheduledQpc</c> whether or not that first tick was real — so a display dead from the
    /// start starts its blank countdown at recorder start, not at some arbitrary sentinel.
    /// <see cref="long.MinValue"/> means no tick has landed yet at all.
    /// </summary>
    private long _lastRealFrameQpc = long.MinValue;

    public DisplayInfo Display { get; }
    public int FramesPerSecond { get; }

    public long FramesEncoded => _encoder.FramesEncoded;
    public long FramesArrived => _capture.FramesArrived;

    /// <summary>True once the backend has closed this display's capture and it cannot be resumed in place.</summary>
    public bool IsCaptureClosed => _capture.IsClosed;

    /// <summary>Frames the pacer had to invent because the screen had not changed.</summary>
    public long DuplicateFrames => Interlocked.Read(ref _duplicateFrames);

    /// <summary>
    /// Frames encoded as solid black because the display had nothing to offer — no frame has ever
    /// arrived yet, or it was temporarily unavailable. A nonzero count once recording is well under
    /// way means the display went away for a while; the log has when and why.
    /// </summary>
    public long BlankFrames => Interlocked.Read(ref _blankFrames);

    public double SecondsBuffered => _ring.SecondsBuffered;
    public long BufferedBytes => _ring.Bytes;
    public long LateTicks => _pacer.LateTicks;

    /// <summary>
    /// Frames skipped to keep this display's schedule from drifting behind real time. A nonzero,
    /// climbing count means the configured frame rate is not sustainable under the current load.
    /// </summary>
    public long FramesSkippedForDrift => _pacer.FramesSkippedForDrift;

    /// <summary>How many times the capture side has been re-provisioned for a new native size.</summary>
    public long Resizes => Interlocked.Read(ref _resizes);

    /// <inheritdoc/>
    public bool HasExceededBlankTimeout(long nowTicks, int timeoutSeconds)
    {
        if (timeoutSeconds <= 0) return false;

        var lastReal = Interlocked.Read(ref _lastRealFrameQpc);
        if (lastReal == long.MinValue) return false;

        return Clock.ToSeconds(nowTicks - lastReal) >= timeoutSeconds;
    }

    /// <param name="captureFactory">Builds the capture source. Called once, at construction time.</param>
    /// <param name="encoderFactory">
    /// Builds the encoder for a given size. Called once, at construction time — the encode size is
    /// fixed for this recorder's lifetime from then on; see the class remarks.
    /// </param>
    /// <param name="fixedEncodeSize">
    /// Overrides the encode size instead of picking it up from the display's native size at
    /// construction — e.g. a user-configured target resolution. <c>null</c> (the default) keeps the
    /// existing behaviour of pinning to whatever the display's native size is at startup.
    /// </param>
    public DisplayRecorder(
        DisplayInfo display, int framesPerSecond, int bufferSeconds, long memoryLimitBytes,
        Func<IDisplayCaptureSource<TFrame>> captureFactory, Func<int, int, IVideoEncoder<TFrame>> encoderFactory,
        FrameSize? fixedEncodeSize = null)
    {
        Display = display;
        FramesPerSecond = framesPerSecond;

        _capture = captureFactory();
        _capture.ContentSizeChanged += OnContentSizeChanged;

        var nativeSize = _capture.ContentSize;
        var encodeSize = fixedEncodeSize ?? nativeSize;

        _encoder = encoderFactory(encodeSize.Width, encodeSize.Height);

        // The configured encode size may not match what the display is actually running right now —
        // tell the encoder up front rather than waiting for a resolution change that may never come.
        if (encodeSize != nativeSize) _encoder.NotifySourceResized(nativeSize.Width, nativeSize.Height);

        _ring = new PacketRingBuffer(bufferSeconds, memoryLimitBytes);
        _encoder.PacketReady += OnPacketReady;

        _pacer = new FramePacer(FramesPerSecond, OnTick, display.DeviceName.Replace(@"\\.\", ""));
    }

    /// <summary>
    /// Forces the capture side to be re-provisioned for its current native size on the next tick.
    /// Normally triggered by <see cref="OnContentSizeChanged"/>; also exposed so the path can be
    /// exercised deliberately rather than only when someone happens to change their display settings.
    /// </summary>
    public void RequestResize() => _resizeRequested = true;

    /// <summary>Exposed for tests: whether a resize is pending application on the next tick.</summary>
    internal bool ResizeRequested => _resizeRequested;

    private void OnContentSizeChanged(FrameSize size)
    {
        // Raised on a capture callback thread. Only flag it here — touching the capture/encoder from
        // this thread would race the pacer mid-encode.
        Log.Info($"{Display.DeviceName} changed to {size.Width}x{size.Height}; capture will adapt, " +
                 $"encoding stays at {Width}x{Height}.");
        _resizeRequested = true;
    }

    /// <summary>
    /// Re-provisions capture for its current native size and tells the encoder to scale into its
    /// fixed output size accordingly.
    /// <para>
    /// Neither the encoder nor the ring buffer is touched: the encode resolution never changes after
    /// construction (see the class remarks), so there is nothing dimension-specific that a resize
    /// could invalidate. That is the whole point — a display resolution change (or a capture
    /// re-acquire after access was lost that happens to land at a different size) no longer costs the
    /// buffered history the way tearing down the codec would.
    /// </para>
    /// </summary>
    /// <remarks>Internal rather than private so tests can drive it directly without a real pacer thread.</remarks>
    internal void ApplyResize()
    {
        lock (_encoderGate)
        {
            var requestedSize = _capture.ContentSize;

            try
            {
                _capture.Recreate(requestedSize);

                // Re-read rather than trust requestedSize: Recreate can block for a while (DXGI's
                // AcquireDuplication retries transient failures for up to ~1s), and if the display
                // changes size again during that wait, the capture settles at whatever it actually
                // is now, not at what we asked for. Notifying the encoder of the stale size would
                // desync it from the real capture size until the next resolution change happened to
                // paper over it.
                var settledSize = _capture.ContentSize;
                _encoder.NotifySourceResized(settledSize.Width, settledSize.Height);

                Interlocked.Increment(ref _resizes);
                Log.Info($"{Display.DeviceName}: capture adapted to {settledSize.Width}x{settledSize.Height}; " +
                         $"still encoding at {_encoder.Width}x{_encoder.Height}, buffer intact.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to adapt {Display.DeviceName} to its new size", ex);
                throw;
            }
        }
    }

    public void Start()
    {
        _pacer.Start();
        Log.Info($"Recorder started for {Display.DeviceName} at {FramesPerSecond} fps.");
    }

    /// <remarks>Internal rather than private so tests can drive a tick directly without a real pacer thread.</remarks>
    internal void OnTick(long frameIndex, long scheduledQpc)
    {
        if (_disposed) return;

        // Seed the blank-timeout baseline on this recorder's very first tick, whether or not that
        // tick turns out to be real — a display dead from the start should start its countdown at
        // recorder start, not sit exempt forever because _lastRealFrameQpc was never set.
        Interlocked.CompareExchange(ref _lastRealFrameQpc, scheduledQpc, long.MinValue);

        if (_resizeRequested)
        {
            _resizeRequested = false;
            try
            {
                ApplyResize();
            }
            catch
            {
                // ApplyResize logged the cause. Leave the flag clear so the pacer keeps ticking
                // rather than spinning on a failure that will not resolve itself.
                return;
            }
        }

        TFrame frame;

        if (_capture.TryGetLatest(out var capturedFrame, out var capturedQpc))
        {
            frame = capturedFrame;
            if (capturedQpc == _lastCapturedQpc) Interlocked.Increment(ref _duplicateFrames);
            _lastCapturedQpc = capturedQpc;
            Interlocked.Exchange(ref _lastRealFrameQpc, scheduledQpc);
        }
        else
        {
            // No real frame yet — before the first one ever lands, or because the display has
            // temporarily gone away (disconnected, asleep, mid-reacquire). Encoding solid black
            // instead of skipping the tick keeps the clip continuous rather than leaving a gap a
            // saved file has no way to represent.
            frame = _capture.BlackFrame;
            Interlocked.Increment(ref _blankFrames);
        }

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
