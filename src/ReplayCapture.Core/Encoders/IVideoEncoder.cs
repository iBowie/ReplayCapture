namespace ReplayCapture.Core.Encoders;

/// <summary>
/// Encodes captured frames into H.264. Generic over the captured frame's native handle type — a
/// D3D11 texture for the Windows backends, a VAAPI surface for a future Linux backend.
/// <see cref="DisplayRecorder{TFrame}"/> and <see cref="Muxing.MovWriter"/> talk to this contract
/// rather than a concrete encoder, so which engine is actually running — NVENC, AMD AMF, or
/// software x264, see <see cref="Config.VideoEncoderBackend"/> — is a construction-time choice, not
/// something either of them needs to know about.
/// </summary>
public interface IVideoEncoder<TFrame> : IDisposable
{
    int Width { get; }
    int Height { get; }
    int FramesPerSecond { get; }
    long TicksPerFrame { get; }

    /// <summary>
    /// SPS/PPS produced at open time, in avcC (length-prefixed) form. The MOV muxer needs these as
    /// stream extradata, because a clip is written long after the encoder started and cannot rely on
    /// in-band parameter sets.
    /// </summary>
    byte[] ExtraData { get; }

    long FramesEncoded { get; }
    long BytesProduced { get; }

    /// <summary>Raised for every packet the encoder emits, on the calling (pacer) thread.</summary>
    event Action<ReadOnlySpan<byte>, long, long, bool>? PacketReady;

    /// <summary>
    /// Encodes one frame from the capture latch at constant-rate index <paramref name="frameIndex"/>.
    /// The pacer calls this on every tick, resubmitting the previous texture when the display has
    /// not changed.
    /// </summary>
    void Encode(TFrame source, long frameIndex, long qpcTicks, bool forceKeyframe = false);

    /// <summary>Pushes any frames the encoder is still holding. Called before writing a clip.</summary>
    void Flush();
}
