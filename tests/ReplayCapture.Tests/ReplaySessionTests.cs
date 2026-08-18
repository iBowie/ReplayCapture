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
        var config = new DisplayConfig { DeviceName = Display.DeviceName };

        Assert.Null(ReplaySession.ResolveFixedEncodeSize(Display, config));
    }

    [Fact]
    public void ResolveFixedEncodeSize_uses_both_dimensions_when_both_are_configured()
    {
        var config = new DisplayConfig { DeviceName = Display.DeviceName, CaptureWidth = 1920, CaptureHeight = 1080 };

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
        yield return [new DisplayConfig { DeviceName = Display.DeviceName, CaptureWidth = 1920 }];
        yield return [new DisplayConfig { DeviceName = Display.DeviceName, CaptureHeight = 1080 }];
    }
}
