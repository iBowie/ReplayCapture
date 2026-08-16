using ReplayCapture.Core.Capture.Linux.Interop;

namespace ReplayCapture.Core.Capture.Linux;

/// <summary>
/// One dequeued PipeWire buffer, decoded from the native <c>spa_buffer</c>/<c>spa_data</c> structs
/// (see <see cref="PipeWireInterop"/>'s draft-layout warnings) into the handful of fields
/// <see cref="DmaBufImporter"/> actually needs. Only the first <c>spa_data</c> plane is captured —
/// correct for a single-plane packed format like the BGRx <see cref="Interop.SpaPodBuilder"/>
/// currently requests; a real negotiation that ends up with a planar format (e.g. NV12 offered
/// directly by the compositor) would need every plane, not just one.
/// </summary>
public readonly record struct PipeWireBuffer(
    SpaDataType Type,
    int Fd,
    uint Offset,
    uint Size,
    int Stride,
    nint MappedPointer,
    int Width,
    int Height);
