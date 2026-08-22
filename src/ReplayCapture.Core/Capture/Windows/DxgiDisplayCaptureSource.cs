using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Timing;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ReplayCapture.Core.Capture;

/// <summary>
/// Captures one display through raw <see cref="IDXGIOutputDuplication"/> and keeps the most recent
/// frame (cursor already composited in) latched in a texture the encoder can read at its own
/// cadence. The default backend — see <see cref="Config.CaptureBackend"/> for when to pick
/// <see cref="WgcDisplayCaptureSource"/> instead.
/// <para>
/// Desktop Duplication was adopted after Windows.Graphics.Capture was measured capping delivery at
/// ~50 frames/sec on a 200Hz display — regardless of frame-pool buffer count or GPU load — while raw
/// Desktop Duplication on the same device, same monitor, hits ~207/s. The cap lives inside WGC's own
/// frame-pool/compositor layer, not in anything this pipeline controls, so Desktop Duplication is
/// what actually delivers the display's real refresh rate. The trade-off is this class's polling
/// thread, one per display, spinning continuously even when nothing on screen is changing — WGC's
/// frame-arrived event costs nothing at idle. See <see cref="Config.CaptureBackend"/> for the full
/// comparison.
/// </para>
/// <para>
/// Content only <i>changes</i> at an irregular rate, so frames still arrive irregularly. This class
/// deliberately does not try to fix that — it just holds the newest frame.
/// <see cref="FramePacer"/> is what turns the irregular arrivals into constant frame rate.
/// </para>
/// <para>
/// <b><see cref="Run"/> polls <c>AcquireNextFrame</c> with a zero timeout, never a blocking one.</b>
/// This is the fix for a second, nastier problem the WGC-to-Duplication swap exposed: with two
/// displays' capture threads and two encoder threads all sharing one <see cref="D3DContext"/> device
/// (driver-serialized via <c>SetMultithreadProtected</c>), giving <c>AcquireNextFrame</c> even an 8ms
/// timeout was enough to starve the encoder — measured encoding as few as ~230 of an expected 1200
/// frames in 20s at a 60fps target, with the pacer skipping hundreds of frame indices per run to
/// resync. The blocking wait appears to hold something at the device level for its whole duration,
/// not just for the specific resources it touches. Polling with a zero timeout and sleeping on a
/// plain <see cref="Thread.Sleep(int)"/> — which touches no D3D11 object at all — when nothing is
/// ready eliminated it completely: 100% frame-rate accuracy, 0 late ticks, 0 drift-skips on both
/// displays under real load. Double-buffering the latch (below) and moving cursor compositing off a
/// CPU <c>Map</c> onto a GPU blend (<see cref="CursorOverlay"/>) were both tried first and both
/// helped somewhat, but neither came close on its own — this was the actual fix.
/// </para>
/// <para>
/// The latch is still double-buffered rather than a single texture, though: a single shared texture,
/// written by this class up to ~200 times/sec and read by the encoder's <c>VideoProcessorBlt</c> on a
/// different thread, forces the GPU to serialize the two — a write can't start until any in-flight
/// read of that same resource has finished, and vice versa. Alternating between two textures means a
/// write into one can never overlap a read of the other.
/// </para>
/// </summary>
public sealed class DxgiDisplayCaptureSource : IDisplayCaptureSource<ID3D11Texture2D>
{
    private const int LatchBufferCount = 2;

    /// <summary>
    /// How long a continuous run of <c>DXGI_ERROR_ACCESS_LOST</c> is tolerated before the capture is
    /// declared dead. Sized for a UAC prompt or the lock screen sitting on the secure desktop for as
    /// long as a user takes to respond to it, not for the sub-second contention
    /// <see cref="AcquireDuplication"/>'s own retry budget targets — giving up any sooner than this
    /// tears the whole recorder down (see <see cref="ReplaySession.CheckHealth"/>) and discards its
    /// ring buffer for something that resolves itself the moment the prompt closes.
    /// </summary>
    private static readonly TimeSpan AccessLostGiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly D3DContext _d3d;
    private readonly Lock _duplicationGate = new();
    private readonly Lock _latchGate = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CursorOverlay _cursorOverlay;
    private readonly ID3D11Texture2D?[] _latchBuffers = new ID3D11Texture2D?[LatchBufferCount];

    /// <summary>
    /// Null whenever capture has just lost access and not yet re-acquired - see the AccessLost
    /// handling in <see cref="Run"/> for why disposing this eagerly, rather than holding onto the
    /// dead interface while trying to acquire a replacement, is required for the replacement to have
    /// any chance of succeeding.
    /// </summary>
    private IDXGIOutputDuplication? _duplication;
    private int _latchWriteIndex;
    private int _latchReadIndex = -1;
    private long _latchQpc;
    private long _framesArrived;
    private bool _disposed;
    private volatile bool _closed;

    /// <summary>
    /// QPC tick of the first <c>AccessLost</c> in the current unbroken run of them; <see cref="long.MinValue"/>
    /// while capture is healthy. Only ever touched from the capture thread inside <see cref="_duplicationGate"/>.
    /// </summary>
    private long _accessLostSinceQpc = long.MinValue;

    // Cursor shape buffer. Only ever touched from the capture thread, inside _duplicationGate, so it
    // needs no lock of its own.
    private byte[]? _cursorShapeBuffer;

    // Last known pointer state. Desktop Duplication only guarantees PointerPosition is valid on the
    // frame where LastMouseUpdateTime is nonzero; on a frame delivered purely by a desktop content
    // change (the common case once the cursor stops moving), Visible comes back false instead of the
    // real "still there, just not moving" state, so it must be cached rather than read fresh.
    private bool _cursorVisible;
    private int _cursorLeft;
    private int _cursorTop;

    public DisplayInfo Display { get; }

    /// <summary>
    /// True once the duplication could not be re-acquired after repeated attempts — e.g. the display
    /// was unplugged or powered off. The pipeline cannot restart capture on a dead output, so this is
    /// surfaced for the owner to rebuild instead.
    /// </summary>
    public bool IsClosed => _closed;

    /// <summary>Current content size. Changes when the user alters resolution or rotates a display.</summary>
    public FrameSize ContentSize { get; private set; }

    /// <summary>Raised when the display's size changed and the pipeline needs rebuilding.</summary>
    public event Action<FrameSize>? ContentSizeChanged;

    /// <summary>Total frames Desktop Duplication has delivered. Compare with encoded frames to see duplicate ratio.</summary>
    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    public DxgiDisplayCaptureSource(D3DContext d3d, DisplayInfo display)
    {
        _d3d = d3d;
        Display = display;
        _cursorOverlay = new CursorOverlay(d3d);

        _duplication = AcquireDuplication();
        var desc = _duplication.Description;
        ContentSize = new FrameSize((int)desc.ModeDescription.Width, (int)desc.ModeDescription.Height);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"dxgidup-{display.DeviceName}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        Log.Info($"Capture started for {display.DeviceName} at {ContentSize.Width}x{ContentSize.Height}.");
    }

    /// <summary>Finds this display's DXGI output and duplicates it against the shared device.</summary>
    private IDXGIOutputDuplication Duplicate()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out var adapter).Failure) break;

            using (adapter)
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (adapter.EnumOutputs(outputIndex, out var output).Failure) break;

                    using (output)
                    {
                        if (output.Description.DeviceName != Display.DeviceName) continue;

                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        return output1.DuplicateOutput(_d3d.Device);
                    }
                }
            }
        }

        throw new InvalidOperationException($"No DXGI output found for {Display.DeviceName}.");
    }

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            lock (_duplicationGate)
            {
                if (_duplication is null)
                {
                    // Re-acquiring after AccessLost disposed the old interface - see there for why
                    // that disposal has to happen before this is even attempted.
                    var recovered = TryReacquireDuplication();

                    if (!recovered && ShouldGiveUpOnAccessLost(_accessLostSinceQpc, Clock.Now, AccessLostGiveUpAfter))
                    {
                        Log.Warn($"{Display.DeviceName}: could not recover from access loss after " +
                                 $"{AccessLostGiveUpAfter.TotalSeconds:0}s; giving up.");
                        _closed = true;
                        break;
                    }

                    // Paced unconditionally, including a "successful" reacquire: DuplicateOutput
                    // touches the hardware cursor plane, so hammering it with no delay between
                    // attempts during a flap (reacquire succeeds, then the very next AcquireNextFrame
                    // is access-lost again) spins the CPU and visibly flickers the real mouse cursor,
                    // not just the one this class composites into the recording.
                    Thread.Sleep(250);
                    continue;
                }

                var duplication = _duplication;

                // Zero timeout, never blocking - see the class remarks for why a blocking wait here
                // starved the encoder thread on the shared device even at a few milliseconds.
                var result = duplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (result.Failure)
                {
                    // DXGI_ERROR_ACCESS_LOST is the documented code for resolution/rotation changes,
                    // lock screen, secure-desktop UAC prompts, and a fullscreen-exclusive app grabbing
                    // the output - but a secure-desktop transition has also been observed (RTX 50-series,
                    // driver-dependent) to surface as plain DXGI_ERROR_INVALID_CALL instead. Both, and
                    // anything else AcquireNextFrame can fail with past WaitTimeout, get the same
                    // treatment: there is no "resume in place", only tear the interface down and
                    // re-acquire. Treating only the specific AccessLost code as recoverable and looping
                    // forever on anything else (the previous behaviour) is what turned this failure mode
                    // into a permanent freeze - AcquireNextFrame kept being called on the same dead
                    // interface, which can only ever fail the same way again.
                    Log.Warn($"AcquireNextFrame failed for {Display.DeviceName}: {result}");

                    // Dispose immediately: DXGI will not hand out a new duplication for this output
                    // while any interface object for it - even a dead one - is still alive, so holding
                    // onto this one while trying to acquire a replacement would make every future
                    // attempt fail forever. The branch above re-acquires on the next iteration.
                    //
                    // _accessLostSinceQpc is cleared only once a real frame is actually processed (see
                    // ProcessFrame), never merely because a reacquire succeeded - a flapping duplication
                    // (reacquire succeeds, immediately fails again) must not get its give-up countdown
                    // reset every time, or a sustained flap would never give up.
                    if (_accessLostSinceQpc == long.MinValue) _accessLostSinceQpc = Clock.Now;
                    duplication.Dispose();
                    _duplication = null;
                    continue;
                }

                try
                {
                    ProcessFrame(duplication, frameInfo, desktopResource);
                }
                catch (Exception ex)
                {
                    Log.Error($"Frame handling failed for {Display.DeviceName}", ex);
                }
                finally
                {
                    desktopResource.Dispose();
                    duplication.ReleaseFrame();
                }
            }
        }

        Log.Info($"Capture stopped for {Display.DeviceName} after {FramesArrived} frames.");
    }

    /// <summary>
    /// True once an unbroken run of <c>AccessLost</c> has lasted longer than <paramref name="giveUpAfter"/>.
    /// Pulled out as a pure function purely so the give-up policy is unit-testable without a real
    /// DXGI device.
    /// </summary>
    internal static bool ShouldGiveUpOnAccessLost(long accessLostSinceQpc, long nowQpc, TimeSpan giveUpAfter) =>
        accessLostSinceQpc != long.MinValue && Clock.ToSeconds(nowQpc - accessLostSinceQpc) >= giveUpAfter.TotalSeconds;

    /// <summary>
    /// Must be called with <see cref="_duplicationGate"/> already held and <see cref="_duplication"/>
    /// already null.
    /// </summary>
    private bool TryReacquireDuplication()
    {
        IDXGIOutputDuplication next;
        try
        {
            next = Duplicate();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not re-acquire duplication for {Display.DeviceName} yet: {ex.Message}");
            return false;
        }

        _duplication = next;

        var desc = next.Description;
        var newSize = new FrameSize((int)desc.ModeDescription.Width, (int)desc.ModeDescription.Height);

        if (newSize.Width != ContentSize.Width || newSize.Height != ContentSize.Height)
        {
            Log.Info($"{Display.DeviceName} resized to {newSize.Width}x{newSize.Height}.");
            ContentSize = newSize;
            ContentSizeChanged?.Invoke(newSize);
        }
        else
        {
            Log.Info($"{Display.DeviceName}: duplication re-acquired after access loss.");
        }

        return true;
    }

    /// <summary>
    /// Calls <see cref="Duplicate"/>, retrying transient failures — most commonly <c>E_ACCESSDENIED</c>
    /// from a secure desktop (lock screen, UAC, a logon that hasn't finished settling) or a
    /// fullscreen-exclusive app briefly holding the output — over a short, bounded window. Used by
    /// the constructor and <see cref="Recreate"/>, both of which need a single call to either succeed
    /// or definitively fail rather than being paced externally. <see cref="TryRecoverFromAccessLost"/>
    /// deliberately does *not* use this: its caller (<see cref="Run"/>) already retries in a loop over
    /// a much longer window (<see cref="AccessLostGiveUpAfter"/>) to survive a UAC prompt sitting on
    /// the secure desktop, and stacking this method's own multi-second retry underneath that would
    /// make each outer attempt block for that long even once the real problem has resolved.
    /// </summary>
    private IDXGIOutputDuplication AcquireDuplication()
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return Duplicate();
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn($"Acquiring duplication for {Display.DeviceName} failed (attempt {attempt}/5): {ex.Message}");
                if (attempt < 5) Thread.Sleep(200);
            }
        }

        throw lastError!;
    }

    /// <summary>Must be called with <see cref="_duplicationGate"/> already held.</summary>
    private void ProcessFrame(IDXGIOutputDuplication duplication, OutduplFrameInfo frameInfo, IDXGIResource desktopResource)
    {
        // A real frame only ever reaches here once AcquireNextFrame has actually succeeded, so this
        // is the one place safe to declare the access-lost episode over - see the AccessLost branch
        // in Run() for why a bare successful reacquire isn't enough on its own.
        _accessLostSinceQpc = long.MinValue;

        using var source = desktopResource.QueryInterface<ID3D11Texture2D>();

        UpdateCursorShapeIfChanged(duplication, frameInfo);
        if (frameInfo.LastMouseUpdateTime != 0)
        {
            _cursorVisible = frameInfo.PointerPosition.Visible;
            // PointerPosition is in virtual-desktop coordinates; translate to this output's own space.
            _cursorLeft = frameInfo.PointerPosition.Position.X - Display.Left;
            _cursorTop = frameInfo.PointerPosition.Position.Y - Display.Top;
        }

        // Written outside _latchGate: only this thread ever writes a latch buffer's contents, and it
        // never writes the one currently published as _latchReadIndex (see the class remarks), so
        // there is nothing here for a concurrent reader to race with.
        var writeIndex = _latchWriteIndex;
        var writeBuffer = EnsureLatch(writeIndex, source);
        _d3d.ImmediateContext.CopyResource(writeBuffer, source);

        if (_cursorVisible)
            _cursorOverlay.Draw(_d3d.ImmediateContext, writeBuffer, ContentSize.Width, ContentSize.Height, _cursorLeft, _cursorTop);

        lock (_latchGate)
        {
            _latchReadIndex = writeIndex;
            _latchQpc = Clock.Now;
        }

        _latchWriteIndex = (writeIndex + 1) % LatchBufferCount;

        Interlocked.Increment(ref _framesArrived);
    }

    /// <summary>
    /// Fetches new pointer shape data when Desktop Duplication reports it changed. Shape changes far
    /// less often than position, so this is skipped whenever <c>PointerShapeBufferSize</c> is zero.
    /// </summary>
    private void UpdateCursorShapeIfChanged(IDXGIOutputDuplication duplication, OutduplFrameInfo frameInfo)
    {
        if (frameInfo.PointerShapeBufferSize == 0) return;

        var size = (int)frameInfo.PointerShapeBufferSize;
        if (_cursorShapeBuffer is null || _cursorShapeBuffer.Length < size)
            _cursorShapeBuffer = new byte[size];

        unsafe
        {
            fixed (byte* p = _cursorShapeBuffer)
            {
                var result = duplication.GetFramePointerShape((uint)size, (IntPtr)p, out _, out var shapeInfo);
                if (result.Failure)
                {
                    Log.Warn($"GetFramePointerShape failed for {Display.DeviceName}: {result}");
                    return;
                }

                _cursorOverlay.UpdateShape(p, in shapeInfo);
            }
        }
    }

    /// <summary>Returns the latch buffer at <paramref name="index"/>, (re)creating it if it doesn't match <paramref name="source"/>'s size.</summary>
    private ID3D11Texture2D EnsureLatch(int index, ID3D11Texture2D source)
    {
        var sourceDescription = source.Description;
        var existing = _latchBuffers[index];
        if (existing is not null &&
            existing.Description.Width == sourceDescription.Width &&
            existing.Description.Height == sourceDescription.Height)
        {
            return existing;
        }

        existing?.Dispose();
        var created = _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = sourceDescription.Width,
            Height = sourceDescription.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _latchBuffers[index] = created;
        return created;
    }

    /// <summary>
    /// Hands the caller the latest captured frame. Returns false until the very first frame lands —
    /// on a completely static display that can take a moment, because nothing has changed to send.
    /// </summary>
    public bool TryGetLatest(out ID3D11Texture2D texture, out long qpcTicks)
    {
        lock (_latchGate)
        {
            if (_latchReadIndex < 0)
            {
                texture = null!;
                qpcTicks = 0;
                return false;
            }

            texture = _latchBuffers[_latchReadIndex]!;
            qpcTicks = _latchQpc;
            return true;
        }
    }

    private readonly Lock _blackFrameGate = new();
    private ID3D11Texture2D? _blackFrame;
    private int _blackFrameWidth;
    private int _blackFrameHeight;

    /// <summary>Solid black frame at the current <see cref="ContentSize"/>; see the interface doc comment.</summary>
    public ID3D11Texture2D BlackFrame
    {
        get
        {
            var size = ContentSize;

            lock (_blackFrameGate)
            {
                if (_blackFrame is null || _blackFrameWidth != size.Width || _blackFrameHeight != size.Height)
                {
                    _blackFrame?.Dispose();
                    _blackFrame = CreateBlackTexture(size.Width, size.Height);
                    _blackFrameWidth = size.Width;
                    _blackFrameHeight = size.Height;
                }

                return _blackFrame;
            }
        }
    }

    private ID3D11Texture2D CreateBlackTexture(int width, int height)
    {
        var texture = _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        using var rtv = _d3d.Device.CreateRenderTargetView(texture);
        _d3d.ImmediateContext.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));

        return texture;
    }

    /// <summary>Forces the duplication to be torn down and re-acquired.</summary>
    public void Recreate(FrameSize size)
    {
        lock (_duplicationGate)
        {
            // Acquire the replacement before disposing the current one: AcquireDuplication retries
            // transient failures but can still exhaust them, and if it does, leaving the working
            // duplication in place beats leaving this source with none at all. Unlike the AccessLost
            // path in Run(), _duplication here is still alive and healthy - just needs replacing for
            // the new size - so there is no dead interface blocking the new DuplicateOutput call.
            var next = AcquireDuplication();
            _duplication?.Dispose();
            _duplication = next;
            var desc = _duplication.Description;
            ContentSize = new FrameSize((int)desc.ModeDescription.Width, (int)desc.ModeDescription.Height);
        }

        Log.Info($"Duplication for {Display.DeviceName} recreated at {ContentSize.Width}x{ContentSize.Height}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));

        lock (_duplicationGate) _duplication?.Dispose();

        lock (_latchGate)
        {
            for (var i = 0; i < _latchBuffers.Length; i++)
            {
                _latchBuffers[i]?.Dispose();
                _latchBuffers[i] = null;
            }
        }

        lock (_blackFrameGate) _blackFrame?.Dispose();

        _cursorOverlay.Dispose();

        Log.Info($"Capture stopped for {Display.DeviceName} after {FramesArrived} frames.");
    }
}
