using ReplayCapture.Core.Diagnostics;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace ReplayCapture.Core.Capture;

public static class DisplayEnumerator
{
    /// <summary>
    /// Enumerates every attached output across every adapter. Enumerating per-adapter matters on
    /// machines with a virtual display driver alongside the real GPU — those outputs hang off a
    /// different adapter and would be missed by a single-adapter walk.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var displays = new List<DisplayInfo>();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out var adapter).Failure) break;

            using (adapter)
            {
                var adapterName = adapter.Description1.Description;

                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (adapter.EnumOutputs(outputIndex, out var output).Failure) break;

                    using (output)
                    {
                        var description = output.Description;
                        if (!description.AttachedToDesktop) continue;

                        var rect = description.DesktopCoordinates;
                        var deviceName = description.DeviceName;

                        displays.Add(new DisplayInfo
                        {
                            DeviceName = deviceName,
                            MonitorHandle = description.Monitor,
                            AdapterDescription = adapterName,
                            Left = rect.Left,
                            Top = rect.Top,
                            Width = rect.Right - rect.Left,
                            Height = rect.Bottom - rect.Top,
                            RefreshHz = QueryRefreshHz(deviceName),
                            IsPrimary = rect is { Left: 0, Top: 0 },
                        });
                    }
                }
            }
        }

        foreach (var display in displays) Log.Info($"Display: {display}");
        if (displays.Count == 0) Log.Warn("No displays attached to the desktop were found.");

        return displays;
    }

    /// <summary>Current refresh rate for a GDI device name, falling back to 60 Hz.</summary>
    private static unsafe int QueryRefreshHz(string deviceName)
    {
        try
        {
            var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
            if (PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
                && mode.dmDisplayFrequency > 1)
            {
                return (int)mode.dmDisplayFrequency;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the refresh rate for {deviceName}: {ex.Message}");
        }

        return 60;
    }
}
