using ReplayCapture.Core;

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
}
