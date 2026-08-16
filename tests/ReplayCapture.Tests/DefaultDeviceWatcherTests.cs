using ReplayCapture.Core.Audio;
using Windows.Win32.Media.Audio;
using InteropEDataFlow = ReplayCapture.Core.Audio.Interop.EDataFlow;
using InteropERole = ReplayCapture.Core.Audio.Interop.ERole;

namespace ReplayCapture.Tests;

public class DefaultDeviceWatcherTests
{
    // Windows calls OnDefaultDeviceChanged once per role (eConsole, eMultimedia, eCommunications)
    // every time the user switches a default device. AudioEngine resolves render loopback against
    // eConsole and the mic against eCommunications, so the other role notifications must be
    // filtered out — otherwise a single device switch would trigger redundant, wasteful reopens
    // instead of exactly one.
    //
    // OnDefaultDeviceChanged takes ReplayCapture.Core.Audio.Interop's local mirror enums, not
    // Windows.Win32.Media.Audio's — see WasapiInterop.cs's file comment for why the interop layer
    // can't use CsWin32's own enum types directly. DefaultDeviceChanged (the public event) still
    // surfaces Windows.Win32.Media.Audio.EDataFlow, unaffected.
    [Fact]
    public void Render_change_fires_only_for_console_role()
    {
        using var watcher = new DefaultDeviceWatcher();
        var raisedFlows = new List<EDataFlow>();
        watcher.DefaultDeviceChanged += flow => raisedFlows.Add(flow);

        watcher.OnDefaultDeviceChanged(InteropEDataFlow.eRender, InteropERole.eMultimedia, default);
        watcher.OnDefaultDeviceChanged(InteropEDataFlow.eRender, InteropERole.eCommunications, default);
        Assert.Empty(raisedFlows);

        watcher.OnDefaultDeviceChanged(InteropEDataFlow.eRender, InteropERole.eConsole, default);
        Assert.Equal([EDataFlow.eRender], raisedFlows);
    }

    [Fact]
    public void Capture_change_fires_only_for_communications_role()
    {
        using var watcher = new DefaultDeviceWatcher();
        var raisedFlows = new List<EDataFlow>();
        watcher.DefaultDeviceChanged += flow => raisedFlows.Add(flow);

        watcher.OnDefaultDeviceChanged(InteropEDataFlow.eCapture, InteropERole.eConsole, default);
        watcher.OnDefaultDeviceChanged(InteropEDataFlow.eCapture, InteropERole.eMultimedia, default);
        Assert.Empty(raisedFlows);

        watcher.OnDefaultDeviceChanged(InteropEDataFlow.eCapture, InteropERole.eCommunications, default);
        Assert.Equal([EDataFlow.eCapture], raisedFlows);
    }
}
