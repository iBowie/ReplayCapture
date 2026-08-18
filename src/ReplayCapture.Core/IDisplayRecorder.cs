using ReplayCapture.Core.Buffering;
using ReplayCapture.Core.Capture;

namespace ReplayCapture.Core;

/// <summary>
/// The platform-agnostic surface <see cref="ReplaySession"/> and the CLI harness need from a
/// display's recorder. <see cref="DisplayRecorder{TFrame}"/> is generic over the backend's native
/// frame handle (a D3D11 texture today, a VAAPI surface on a future Linux backend), but nothing
/// above it ever needs to know which — this interface is exactly that seam.
/// </summary>
public interface IDisplayRecorder : IDisposable
{
    DisplayInfo Display { get; }
    int FramesPerSecond { get; }

    long FramesEncoded { get; }
    long FramesArrived { get; }

    /// <summary>True once the backend has closed this display's capture and it cannot be resumed in place.</summary>
    bool IsCaptureClosed { get; }

    /// <summary>Frames the pacer had to invent because the screen had not changed.</summary>
    long DuplicateFrames { get; }

    /// <summary>
    /// Frames encoded as solid black because the display had nothing to offer — no frame has ever
    /// arrived yet, or it was temporarily unavailable. A nonzero count once recording is well under
    /// way means the display went away for a while.
    /// </summary>
    long BlankFrames { get; }

    double SecondsBuffered { get; }
    long BufferedBytes { get; }
    long LateTicks { get; }

    /// <summary>
    /// Frames skipped to keep this display's schedule from drifting behind real time. A nonzero,
    /// climbing count means the configured frame rate is not sustainable under the current load.
    /// </summary>
    long FramesSkippedForDrift { get; }

    /// <summary>How many times the capture side has been re-provisioned for a new native size.</summary>
    long Resizes { get; }

    /// <summary>
    /// Fixed for this recorder's whole lifetime — set once at construction from the display's native
    /// size at the time, and never changed by a later resolution change. See
    /// <see cref="DisplayRecorder{TFrame}"/>'s class remarks for how a resolution change is absorbed
    /// without touching this.
    /// </summary>
    int Width { get; }
    int Height { get; }
    byte[] ExtraData { get; }

    /// <summary>
    /// Asks the pacer to re-provision capture for its current native size on its next tick. Normally
    /// triggered by a resolution change; also exposed so the path can be exercised deliberately
    /// rather than only when someone happens to change their display settings.
    /// </summary>
    void RequestResize();

    void Start();

    /// <summary>
    /// Takes the buffered window without writing anything. Capture and encoding continue
    /// throughout, so triggering a save never creates a gap in the buffer.
    /// </summary>
    IReadOnlyList<ClipPacket> Snapshot(long nowTicks, int seconds);
}
