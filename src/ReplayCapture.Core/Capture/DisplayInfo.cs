namespace ReplayCapture.Core.Capture;

/// <summary>One attached display, as both the OS and the capture pipeline see it.</summary>
public sealed record DisplayInfo
{
    /// <summary>
    /// GDI device name, e.g. <c>\\.\DISPLAY1</c>. An enumeration-order ordinal Windows reassigns
    /// on a topology change — good for <c>EnumDisplaySettingsW</c>-style calls and output
    /// filenames, but not for identity. See <see cref="MonitorId"/> for that.
    /// </summary>
    public required string DeviceName { get; init; }

    /// <summary>
    /// Stable PnP device interface path for the physical monitor (e.g.
    /// <c>\\?\DISPLAY#GSM7765#5&amp;1a2b3c4d&amp;0&amp;UID4353#{...}</c>), resolved via
    /// <c>EnumDisplayDevicesW</c> with <c>EDD_GET_DEVICE_INTERFACE_NAME</c>. Encodes the monitor's
    /// EDID vendor/model plus a location-derived instance id, so it survives a hot-plug that
    /// reorders <see cref="DeviceName"/>. This is the identity key used in config and by
    /// <see cref="ReplayCapture.Core.ReplaySession"/>'s attach/detach reconciliation. Falls back to
    /// <see cref="DeviceName"/> when the interface path can't be resolved (e.g. some virtual
    /// displays), in which case identity reverts to the ordinal's usual instability.
    /// </summary>
    public required string MonitorId { get; init; }

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
