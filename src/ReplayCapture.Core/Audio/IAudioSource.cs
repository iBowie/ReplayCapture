namespace ReplayCapture.Core.Audio;

/// <summary>
/// Anything that can feed samples into a track — a WASAPI endpoint or per-process capture on
/// Windows today, a PipeWire stream on a future Linux backend.
/// </summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }
    event Action<long, ReadOnlyMemory<float>>? SamplesReady;
    void Start();
}
