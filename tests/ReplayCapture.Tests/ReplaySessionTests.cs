using ReplayCapture.Core;
using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Config;

namespace ReplayCapture.Tests;

public class ReplaySessionTests
{
    [Theory]
    [InlineData(@"\\.\DISPLAY1", "1")]
    [InlineData(@"\\.\DISPLAY2", "2")]
    [InlineData(@"\\.\DISPLAY10", "10")]
    public void ScreenIndexOf_extracts_the_windows_display_number(string deviceName, string expected)
    {
        Assert.Equal(expected, ReplaySession.ScreenIndexOf(deviceName));
    }

    [Fact]
    public void ScreenIndexOf_falls_back_to_the_device_name_when_it_has_no_digits()
    {
        Assert.Equal("displaynoindex", ReplaySession.ScreenIndexOf(@"\\.\DISPLAYNOINDEX"));
    }

    private static readonly DisplayInfo Display = new()
    {
        DeviceName = @"\\.\DISPLAY1",
        MonitorId = "monitor-1",
        MonitorHandle = 1,
        AdapterDescription = "Fake Adapter",
        Left = 0,
        Top = 0,
        Width = 2560,
        Height = 1440,
        RefreshHz = 60,
        IsPrimary = true,
    };

    [Fact]
    public void ResolveFixedEncodeSize_is_null_when_neither_dimension_is_configured()
    {
        var config = new DisplayConfig { MonitorId = Display.MonitorId };

        Assert.Null(ReplaySession.ResolveFixedEncodeSize(Display, config));
    }

    [Fact]
    public void ResolveFixedEncodeSize_uses_both_dimensions_when_both_are_configured()
    {
        var config = new DisplayConfig { MonitorId = Display.MonitorId, CaptureWidth = 1920, CaptureHeight = 1080 };

        Assert.Equal(new FrameSize(1920, 1080), ReplaySession.ResolveFixedEncodeSize(Display, config));
    }

    [Theory]
    [MemberData(nameof(PartialConfigs))]
    public void ResolveFixedEncodeSize_ignores_a_lone_dimension_rather_than_guessing_the_other(DisplayConfig config)
    {
        Assert.Null(ReplaySession.ResolveFixedEncodeSize(Display, config));
    }

    public static IEnumerable<object[]> PartialConfigs()
    {
        yield return [new DisplayConfig { MonitorId = Display.MonitorId, CaptureWidth = 1920 }];
        yield return [new DisplayConfig { MonitorId = Display.MonitorId, CaptureHeight = 1080 }];
    }

    private static DisplayInfo MakeDisplay(string monitorId) => Display with { MonitorId = monitorId };

    [Fact]
    public void Reconcile_leaves_an_unrelated_display_untouched_when_one_is_removed_and_another_added()
    {
        // "A" was removed, "B" is new, "C" was never touched — C must appear in neither list.
        var selected = new[] { MakeDisplay("B"), MakeDisplay("C") };
        var current = new[] { "A", "C" };

        var (toDetach, toAttach) = ReplaySession.Reconcile(selected, current);

        Assert.Equal(["A"], toDetach);
        Assert.Equal(["B"], toAttach.Select(d => d.MonitorId));
    }

    [Fact]
    public void Reconcile_does_not_confuse_a_new_display_that_reused_a_removed_ones_gdi_slot()
    {
        // Windows can hand \\.\DISPLAY1 to a brand new physical monitor after the old one is
        // unplugged. Matching by MonitorId (not DeviceName) means the new one is still "new".
        var removed = Display with { MonitorId = "old-monitor", DeviceName = @"\\.\DISPLAY1" };
        var replacement = Display with { MonitorId = "new-monitor", DeviceName = @"\\.\DISPLAY1" };

        var (toDetach, toAttach) = ReplaySession.Reconcile([replacement], [removed.MonitorId]);

        Assert.Equal([removed.MonitorId], toDetach);
        Assert.Equal([replacement.MonitorId], toAttach.Select(d => d.MonitorId));
    }

    [Fact]
    public void Reconcile_is_a_noop_when_the_selected_set_matches_whats_recorded()
    {
        var selected = new[] { MakeDisplay("A"), MakeDisplay("B") };

        var (toDetach, toAttach) = ReplaySession.Reconcile(selected, ["A", "B"]);

        Assert.Empty(toDetach);
        Assert.Empty(toAttach);
    }
}
