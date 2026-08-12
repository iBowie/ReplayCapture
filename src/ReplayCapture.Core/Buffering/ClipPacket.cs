namespace ReplayCapture.Core.Buffering;

/// <summary>
/// A packet detached from the ring buffer for writing. Owns its bytes outright, so the ring is free
/// to keep recycling its pooled buffers while a save is in flight.
/// </summary>
public readonly record struct ClipPacket(byte[] Data, long FrameIndex, long QpcTicks, bool IsKeyframe);
