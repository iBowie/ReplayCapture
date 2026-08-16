namespace ReplayCapture.Core.Capture.Linux;

/// <summary>
/// Turns the DMA-BUF file descriptor(s) PipeWire hands back for a captured frame into a
/// <see cref="VaapiFrame"/> the encoder can consume — ideally by importing them directly as a
/// <c>VASurfaceID</c> (<c>vaCreateSurfaces</c> with <c>VASurfaceAttribExternalBuffers</c>), so the
/// captured buffer never leaves the GPU.
/// <para>
/// <b>Not implemented — blocked on two things this session cannot verify:</b> a real
/// <c>VaapiContext</c> (the Linux support plan's Phase 2, not written yet — this class needs its
/// <c>VADisplay</c> handle to import into) and a real compositor + GPU driver to answer the plan's
/// flagged highest-risk question: whether the PipeWire node actually offers a DMA-BUF-backed SPA
/// buffer (<c>SPA_DATA_DmaBuf</c>) with a DRM format/modifier VAAPI can import without a copy, or
/// only shared-memory buffers (<c>SPA_DATA_MemFd</c>) — Mesa vs. proprietary-NVIDIA driver behavior
/// is known to differ here. <see cref="PipeWireStream"/> reports which buffer type the negotiated
/// format actually uses; do not assume DMA-BUF until that's been observed on the target hardware.
/// </para>
/// </summary>
public static class DmaBufImporter
{
    /// <summary>
    /// Placeholder for the real import path. Once a <c>VaapiContext</c> exists: for a DMA-BUF buffer,
    /// call <c>vaCreateSurfaces</c> with the fd/stride/modifier from <see cref="PipeWireBuffer"/>; for
    /// a MemFd buffer, upload the mapped pixels into a VAAPI surface instead (loses zero-copy, keeps
    /// the same <see cref="VaapiFrame"/> shape — see the plan's fallback note).
    /// </summary>
    public static VaapiFrame Import(PipeWireBuffer buffer) =>
        throw new NotImplementedException(
            "DMA-BUF -> VAAPI surface import needs a real VaapiContext (Linux support plan Phase 2) " +
            "and a real GPU/driver/compositor to validate the DMA-BUF-vs-MemFd buffer-type question " +
            "against. Not safe to guess at from a Windows session.");
}
