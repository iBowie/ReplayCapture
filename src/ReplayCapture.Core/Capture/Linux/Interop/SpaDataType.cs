namespace ReplayCapture.Core.Capture.Linux.Interop;

/// <summary>
/// <c>enum spa_data_type</c> (spa/buffer/buffer.h) — which union member of
/// <see cref="PipeWireInterop.SpaData"/> actually holds the buffer. <see cref="DmaBuf"/> is the
/// zero-copy case <see cref="DmaBufImporter"/> wants; <see cref="MemFd"/>/<see cref="MemPtr"/> mean a
/// CPU copy is unavoidable. DRAFT — verify the numeric values against the target PipeWire version.
/// </summary>
public enum SpaDataType : uint
{
    Invalid = 0,
    MemPtr = 1,
    MemFd = 2,
    DmaBuf = 3,
    MemId = 4,
}
