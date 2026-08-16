namespace ReplayCapture.Core.Capture;

/// <summary>
/// One display's frame source: whatever backend is producing frames, <see cref="DisplayRecorder{TFrame}"/>
/// only ever needs the newest one latched in a handle it can read at its own cadence. Generic over
/// that handle's native type — a D3D11 texture for the two Windows backends below, a VAAPI surface
/// for a future Linux backend — so the pipeline above this interface never needs a platform check.
/// <para>
/// Two Windows implementations exist — <see cref="DxgiDisplayCaptureSource"/> (raw
/// <c>IDXGIOutputDuplication</c>) and <see cref="WgcDisplayCaptureSource"/>
/// (Windows.Graphics.Capture) — selected per <see cref="Config.AppConfig.CaptureBackend"/>. See
/// that property's doc comment and the README's capture-backend section for why one is the default
/// and when the other is worth picking instead.
/// </para>
/// </summary>
public interface IDisplayCaptureSource<TFrame> : IDisposable
{
    DisplayInfo Display { get; }

    /// <summary>
    /// True once the backend could not recover capture on its own — e.g. the display was unplugged,
    /// powered off, or (WGC only) the system tore down the capture item across sleep/resume. The
    /// pipeline cannot restart capture on a dead source, so this is surfaced for the owner to rebuild.
    /// </summary>
    bool IsClosed { get; }

    /// <summary>Current content size. Changes when the user alters resolution or rotates a display.</summary>
    FrameSize ContentSize { get; }

    /// <summary>Raised when the display's size changed and the pipeline needs rebuilding.</summary>
    event Action<FrameSize>? ContentSizeChanged;

    /// <summary>Total frames the backend has delivered. Compare with encoded frames to see duplicate ratio.</summary>
    long FramesArrived { get; }

    /// <summary>
    /// Hands the caller the latest captured frame. Returns false until the very first frame lands —
    /// on a completely static display that can take a moment, because nothing has changed to send.
    /// </summary>
    bool TryGetLatest(out TFrame frame, out long qpcTicks);

    /// <summary>Forces capture to be torn down and re-acquired at the given size.</summary>
    void Recreate(FrameSize size);
}
