using System.Buffers;

namespace ReplayCapture.Core.Buffering;

/// <summary>
/// One encoded video access unit sitting in the ring buffer.
/// <para>
/// The buffer holds <i>encoded</i> packets, never raw frames. Raw 1440p60 is roughly 10 GB per
/// minute; the same minute of H.264 at 50 Mbps is about 375 MB, which is what makes an always-on
/// 60-second buffer affordable.
/// </para>
/// <para>
/// Payload bytes are rented from <see cref="ArrayPool{T}"/> — at 60 fps a fresh allocation per
/// frame would hand the GC ~3,600 arrays a minute, per display, forever.
/// </para>
/// </summary>
public sealed class EncodedPacket
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    public byte[] Buffer { get; private set; }
    public int Length { get; private set; }

    /// <summary>Constant-rate frame index assigned by the pacer; doubles as the encoder PTS.</summary>
    public long FrameIndex { get; private set; }

    /// <summary>Wall-clock QPC time (100 ns ticks) this frame represents.</summary>
    public long QpcTicks { get; private set; }

    /// <summary>True for IDR frames, which are the only legal places to start a saved clip.</summary>
    public bool IsKeyframe { get; private set; }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    private EncodedPacket(byte[] buffer) => Buffer = buffer;

    public static EncodedPacket Rent(ReadOnlySpan<byte> data, long frameIndex, long qpcTicks, bool isKeyframe)
    {
        var buffer = Pool.Rent(data.Length);
        data.CopyTo(buffer);

        return new EncodedPacket(buffer)
        {
            Length = data.Length,
            FrameIndex = frameIndex,
            QpcTicks = qpcTicks,
            IsKeyframe = isKeyframe,
        };
    }

    /// <summary>Returns the payload to the pool. Called by the ring buffer as packets age out.</summary>
    public void Return()
    {
        if (Length == 0) return;
        Pool.Return(Buffer);
        Buffer = [];
        Length = 0;
    }
}
