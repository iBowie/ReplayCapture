namespace ReplayCapture.Core.Capture;

/// <summary>
/// Plain width/height pair for <see cref="IDisplayCaptureSource{TFrame}"/>. Deliberately not the
/// WinRT <c>Windows.Graphics.SizeInt32</c> type the Windows backends themselves traffic in — that
/// projection only exists on a Windows-versioned TFM, and this interface needs to compile on a
/// bare (Linux) one too. The Windows backends convert to/from it at their own boundary.
/// </summary>
public readonly record struct FrameSize(int Width, int Height);
