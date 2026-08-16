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

    double SecondsBuffered { get; }
    long BufferedBytes { get; }
    long LateTicks { get; }

    /// <summary>
    /// Frames skipped to keep this display's schedule from drifting behind real time. A nonzero,
    /// climbing count means the configured frame rate is not sustainable under the current load.
    /// </summary>
    long FramesSkippedForDrift { get; }

    /// <summary>How many times the encoder had to be rebuilt after a resolution change.</summary>
    long Rebuilds { get; }

    int Width { get; }
    int Height { get; }
    byte[] ExtraData { get; }

    /// <summary>
    /// Asks the pacer to rebuild the capture pool and encoder on its next tick. Normally triggered
    /// by a resolution change; also exposed so the rebuild path can be exercised deliberately
    /// rather than only when someone happens to change their display settings.
    /// </summary>
    void RequestRebuild();

    void Start();

    /// <summary>
    /// Takes the buffered window without writing anything. Capture and encoding continue
    /// throughout, so triggering a save never creates a gap in the buffer.
    /// </summary>
    IReadOnlyList<ClipPacket> Snapshot(long nowTicks, int seconds);
}
