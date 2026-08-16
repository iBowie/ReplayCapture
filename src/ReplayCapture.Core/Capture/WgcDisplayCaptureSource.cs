using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Timing;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ReplayCapture.Core.Capture;

/// <summary>
/// Captures one display through Windows.Graphics.Capture and keeps the most recent frame latched
/// in a texture the encoder can read at its own cadence. The event-driven alternative to
/// <see cref="DxgiDisplayCaptureSource"/> — see <see cref="Config.CaptureBackend"/> for when to
/// pick this one instead of the default.
/// <para>
/// WGC only delivers a frame when the display content actually <i>changes</i>, so frames arrive at
/// an irregular rate. This class deliberately does not try to fix that — it just holds the newest
/// frame. <see cref="FramePacer"/> is what turns the irregular arrivals into constant frame rate.
/// </para>
/// <para>
/// The frame-arrived callback costs nothing while the screen is idle, unlike
/// <see cref="DxgiDisplayCaptureSource"/>'s dedicated polling thread per display. The trade-off,
/// measured on this hardware, is a hard delivery cap well below native refresh on a high-refresh
/// panel (~50 frames/sec on a 200Hz display, regardless of frame-pool buffer count or GPU load) —
/// see <see cref="Config.CaptureBackend"/> for the numbers and when that cap does or doesn't matter.
/// </para>
/// </summary>
public sealed class WgcDisplayCaptureSource : IDisplayCaptureSource
{
    private readonly D3DContext _d3d;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly Lock _latchGate = new();

    private ID3D11Texture2D? _latch;
    private long _latchQpc;
    private long _framesArrived;
    private bool _disposed;
    private volatile bool _closed;

    public DisplayInfo Display { get; }

    /// <summary>
    /// True once Windows has torn down the capture item on us — e.g. a display power-off or a
    /// sleep/resume cycle invalidating the session. The pipeline cannot restart capture on a closed
    /// item, so this is surfaced for the owner to rebuild instead.
    /// </summary>
    public bool IsClosed => _closed;

    /// <summary>Current content size. Changes when the user alters resolution or rotates a display.</summary>
    public SizeInt32 ContentSize { get; private set; }

    /// <summary>Raised when the display's size changed and the pipeline needs rebuilding.</summary>
    public event Action<SizeInt32>? ContentSizeChanged;

    /// <summary>Total frames WGC has delivered. Compare with encoded frames to see duplicate ratio.</summary>
    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    public WgcDisplayCaptureSource(D3DContext d3d, DisplayInfo display)
    {
        _d3d = d3d;
        Display = display;

        _item = Direct3DInterop.CreateItemForMonitor(display.MonitorHandle);
        ContentSize = _item.Size;

        // CreateFreeThreaded, not Create: the latter needs a DispatcherQueue on the calling thread
        // and would deliver frames on the UI thread, which is the last place capture should run.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3d.WinRTDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: ContentSize);

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        TryDisableCaptureBorder();

        _item.Closed += (_, _) =>
        {
            // Seen when a display is unplugged, powered off, or the system sleeps and resumes —
            // Windows tears the capture item down rather than keeping it alive across the change.
            _closed = true;
            Log.Warn($"Capture item for {display.DeviceName} was closed by the system.");
        };

        _session.StartCapture();
        Log.Info($"Capture started for {display.DeviceName} at {ContentSize.Width}x{ContentSize.Height}.");
    }

    /// <summary>
    /// Removes the yellow "recording" border. The setter throws on builds or configurations where
    /// the app is not permitted to hide it, and a border is far better than a dead capture.
    /// </summary>
    private void TryDisableCaptureBorder()
    {
        try
        {
            if (GraphicsCaptureSession.IsSupported()) _session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the capture border for {Display.DeviceName}: {ex.Message}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            if (frame.ContentSize.Width != ContentSize.Width || frame.ContentSize.Height != ContentSize.Height)
            {
                var newSize = frame.ContentSize;
                Log.Info($"{Display.DeviceName} resized to {newSize.Width}x{newSize.Height}.");
                ContentSize = newSize;
                ContentSizeChanged?.Invoke(newSize);
                return;
            }

            using var source = Direct3DInterop.GetTexture(frame.Surface);

            lock (_latchGate)
            {
                EnsureLatch(source);
                // The pool recycles its surfaces as soon as the frame is disposed, so the pixels
                // have to be copied out rather than referenced.
                _d3d.ImmediateContext.CopyResource(_latch!, source);
                _latchQpc = frame.SystemRelativeTime.Ticks;
            }

            Interlocked.Increment(ref _framesArrived);
        }
        catch (Exception ex)
        {
            Log.Error($"Frame handling failed for {Display.DeviceName}", ex);
        }
    }

    private void EnsureLatch(ID3D11Texture2D source)
    {
        var sourceDescription = source.Description;
        if (_latch is not null &&
            _latch.Description.Width == sourceDescription.Width &&
            _latch.Description.Height == sourceDescription.Height)
        {
            return;
        }

        _latch?.Dispose();
        _latch = _d3d.Device.CreateTexture2D(new Texture2DDescription
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
    }

    /// <summary>
    /// Hands the caller the latest captured frame. Returns false until the very first frame lands —
    /// on a completely static display that can take a moment, because WGC has nothing to send.
    /// </summary>
    public bool TryGetLatest(out ID3D11Texture2D texture, out long qpcTicks)
    {
        lock (_latchGate)
        {
            texture = _latch!;
            qpcTicks = _latchQpc;
            return _latch is not null;
        }
    }

    /// <summary>Rebuilds the frame pool after a resolution change.</summary>
    public void Recreate(SizeInt32 size)
    {
        ContentSize = size;
        _framePool.Recreate(_d3d.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        Log.Info($"Frame pool for {Display.DeviceName} recreated at {size.Width}x{size.Height}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _framePool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _framePool.Dispose();

        lock (_latchGate)
        {
            _latch?.Dispose();
            _latch = null;
        }

        Log.Info($"Capture stopped for {Display.DeviceName} after {FramesArrived} frames.");
    }
}
