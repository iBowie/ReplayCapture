using ReplayCapture.Core.Audio;
using Windows.Win32.Media.Audio;

namespace ReplayCapture.Tests;

public class DefaultDeviceWatcherTests
{
    // Windows calls OnDefaultDeviceChanged once per role (eConsole, eMultimedia, eCommunications)
    // every time the user switches a default device. AudioEngine resolves render loopback against
    // eConsole and the mic against eCommunications, so the other role notifications must be
    // filtered out — otherwise a single device switch would trigger redundant, wasteful reopens
    // instead of exactly one.
    [Fact]
    public void Render_change_fires_only_for_console_role()
    {
        using var watcher = new DefaultDeviceWatcher();
        var raisedFlows = new List<EDataFlow>();
        watcher.DefaultDeviceChanged += flow => raisedFlows.Add(flow);

        watcher.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eMultimedia, default);
        watcher.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eCommunications, default);
        Assert.Empty(raisedFlows);

        watcher.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eConsole, default);
        Assert.Equal([EDataFlow.eRender], raisedFlows);
    }

    [Fact]
    public void Capture_change_fires_only_for_communications_role()
    {
        using var watcher = new DefaultDeviceWatcher();
        var raisedFlows = new List<EDataFlow>();
        watcher.DefaultDeviceChanged += flow => raisedFlows.Add(flow);

        watcher.OnDefaultDeviceChanged(EDataFlow.eCapture, ERole.eConsole, default);
        watcher.OnDefaultDeviceChanged(EDataFlow.eCapture, ERole.eMultimedia, default);
        Assert.Empty(raisedFlows);

        watcher.OnDefaultDeviceChanged(EDataFlow.eCapture, ERole.eCommunications, default);
        Assert.Equal([EDataFlow.eCapture], raisedFlows);
    }
}
