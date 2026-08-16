namespace ReplayCapture.Core.Capture;

/// <summary>One attached display, as both the OS and the capture pipeline see it.</summary>
public sealed record DisplayInfo
{
    /// <summary>GDI device name, e.g. <c>\\.\DISPLAY1</c>. Used as the stable key in config.</summary>
    public required string DeviceName { get; init; }

    /// <summary>HMONITOR, needed to build the capture item.</summary>
    public required IntPtr MonitorHandle { get; init; }

    /// <summary>Adapter that drives this output. Two displays can live on different GPUs.</summary>
    public required string AdapterDescription { get; init; }

    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Refresh rate in Hz, rounded. Drives the default capture frame rate.</summary>
    public required int RefreshHz { get; init; }

    public required bool IsPrimary { get; init; }

    /// <summary>Human-readable label for the UI and for output filenames.</summary>
    public string Label => $"{DeviceName.Replace(@"\\.\", "")} ({Width}x{Height} @ {RefreshHz}Hz)";

    public override string ToString() =>
        $"{DeviceName} {Width}x{Height}@{RefreshHz}Hz at ({Left},{Top}) on {AdapterDescription}" +
        (IsPrimary ? " [primary]" : "");
}
