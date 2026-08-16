namespace ReplayCapture.Core.Capture.Linux;

/// <summary>
/// The <c>TFrame</c> handle a Linux <see cref="IDisplayCaptureSource{TFrame}"/> hands the encoder —
/// a VAAPI surface, the Linux analog of the Windows backends' <c>ID3D11Texture2D</c>.
/// <para>
/// <b>Draft, unverified:</b> whether this is a zero-copy DMA-BUF-imported surface or a CPU-uploaded
/// one is exactly the open question <see cref="DmaBufImporter"/> exists to answer once it can be
/// tested against a real compositor and GPU driver — see that class's remarks. This struct's shape
/// is deliberately minimal so that decision doesn't require changing the type, only
/// <see cref="PipeWireStream"/>'s internals.
/// </para>
/// </summary>
public readonly record struct VaapiFrame(nuint SurfaceId, int Width, int Height);
