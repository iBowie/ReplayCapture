using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Timing;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;

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

    private readonly D3DContext _d3d;
    private readonly Lock _duplicationGate = new();
    private readonly Lock _latchGate = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CursorOverlay _cursorOverlay;
    private readonly ID3D11Texture2D?[] _latchBuffers = new ID3D11Texture2D?[LatchBufferCount];

    private IDXGIOutputDuplication _duplication;
    private int _latchWriteIndex;
    private int _latchReadIndex = -1;
    private long _latchQpc;
    private long _framesArrived;
    private bool _disposed;
    private volatile bool _closed;

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
    public SizeInt32 ContentSize { get; private set; }

    /// <summary>Raised when the display's size changed and the pipeline needs rebuilding.</summary>
    public event Action<SizeInt32>? ContentSizeChanged;

    /// <summary>Total frames Desktop Duplication has delivered. Compare with encoded frames to see duplicate ratio.</summary>
    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    public DxgiDisplayCaptureSource(D3DContext d3d, DisplayInfo display)
    {
        _d3d = d3d;
        Display = display;
        _cursorOverlay = new CursorOverlay(d3d);

        _duplication = Duplicate();
        var desc = _duplication.Description;
        ContentSize = new SizeInt32 { Width = (int)desc.ModeDescription.Width, Height = (int)desc.ModeDescription.Height };

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
                // Zero timeout, never blocking - see the class remarks for why a blocking wait here
                // starved the encoder thread on the shared device even at a few milliseconds.
                var result = _duplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Seen on resolution/rotation changes, lock screen, secure-desktop UAC prompts,
                    // and a fullscreen-exclusive app grabbing the output. Only option is to tear down
                    // and re-acquire; there is no "resume in place".
                    if (!TryRecoverFromAccessLost())
                    {
                        _closed = true;
                        break;
                    }
                    continue;
                }

                if (result.Failure)
                {
                    Log.Warn($"AcquireNextFrame failed for {Display.DeviceName}: {result}");
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    ProcessFrame(frameInfo, desktopResource);
                }
                catch (Exception ex)
                {
                    Log.Error($"Frame handling failed for {Display.DeviceName}", ex);
                }
                finally
                {
                    desktopResource.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
        }

        Log.Info($"Capture stopped for {Display.DeviceName} after {FramesArrived} frames.");
    }

    /// <summary>Must be called with <see cref="_duplicationGate"/> already held.</summary>
    private bool TryRecoverFromAccessLost()
    {
        _duplication.Dispose();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                _duplication = Duplicate();
                var desc = _duplication.Description;
                var newSize = new SizeInt32 { Width = (int)desc.ModeDescription.Width, Height = (int)desc.ModeDescription.Height };

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
            catch (Exception ex)
            {
                Log.Warn($"Re-acquiring duplication for {Display.DeviceName} failed (attempt {attempt}/5): {ex.Message}");
                Thread.Sleep(200);
            }
        }

        Log.Warn($"Could not re-acquire duplication for {Display.DeviceName}; giving up.");
        return false;
    }

    /// <summary>Must be called with <see cref="_duplicationGate"/> already held.</summary>
    private void ProcessFrame(OutduplFrameInfo frameInfo, IDXGIResource desktopResource)
    {
        using var source = desktopResource.QueryInterface<ID3D11Texture2D>();

        UpdateCursorShapeIfChanged(frameInfo);
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
    private void UpdateCursorShapeIfChanged(OutduplFrameInfo frameInfo)
    {
        if (frameInfo.PointerShapeBufferSize == 0) return;

        var size = (int)frameInfo.PointerShapeBufferSize;
        if (_cursorShapeBuffer is null || _cursorShapeBuffer.Length < size)
            _cursorShapeBuffer = new byte[size];

        unsafe
        {
            fixed (byte* p = _cursorShapeBuffer)
            {
                var result = _duplication.GetFramePointerShape((uint)size, (IntPtr)p, out _, out var shapeInfo);
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

    /// <summary>Forces the duplication to be torn down and re-acquired at the given size.</summary>
    public void Recreate(SizeInt32 size)
    {
        lock (_duplicationGate)
        {
            _duplication.Dispose();
            _duplication = Duplicate();
            var desc = _duplication.Description;
            ContentSize = new SizeInt32 { Width = (int)desc.ModeDescription.Width, Height = (int)desc.ModeDescription.Height };
        }

        Log.Info($"Duplication for {Display.DeviceName} recreated at {ContentSize.Width}x{ContentSize.Height}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));

        lock (_duplicationGate) _duplication.Dispose();

        lock (_latchGate)
        {
            for (var i = 0; i < _latchBuffers.Length; i++)
            {
                _latchBuffers[i]?.Dispose();
                _latchBuffers[i] = null;
            }
        }

        _cursorOverlay.Dispose();

        Log.Info($"Capture stopped for {Display.DeviceName} after {FramesArrived} frames.");
    }
}
