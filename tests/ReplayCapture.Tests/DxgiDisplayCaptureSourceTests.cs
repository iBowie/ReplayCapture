using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Timing;

namespace ReplayCapture.Tests;

/// <summary>
/// Covers <see cref="DxgiDisplayCaptureSource.ShouldGiveUpOnAccessLost"/> — the give-up policy for a
/// continuous run of <c>DXGI_ERROR_ACCESS_LOST</c> (lock screen, UAC's secure desktop, a
/// fullscreen-exclusive app). A UAC prompt can hold the secure desktop for as long as the user takes
/// to respond to it, so this must stay patient well past a single retry attempt; giving up too early
/// tears the whole recorder down and discards its ring buffer for something that would have resolved
/// itself. The rest of <see cref="DxgiDisplayCaptureSource"/> needs a real DXGI device and isn't
/// unit-testable, which is why this policy was pulled out as a pure function.
/// </summary>
public class DxgiDisplayCaptureSourceTests
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    [Fact]
    public void Healthy_capture_never_gives_up()
    {
        Assert.False(DxgiDisplayCaptureSource.ShouldGiveUpOnAccessLost(
            accessLostSinceQpc: long.MinValue, nowQpc: Clock.FromSeconds(1_000), GiveUpAfter));
    }

    [Fact]
    public void A_brief_access_loss_short_of_the_budget_is_tolerated()
    {
        var since = Clock.FromSeconds(100);
        var now = since + Clock.FromSeconds(59);

        Assert.False(DxgiDisplayCaptureSource.ShouldGiveUpOnAccessLost(since, now, GiveUpAfter));
    }

    [Fact]
    public void Access_loss_lasting_the_full_budget_gives_up()
    {
        var since = Clock.FromSeconds(100);
        var now = since + Clock.FromSeconds(60);

        Assert.True(DxgiDisplayCaptureSource.ShouldGiveUpOnAccessLost(since, now, GiveUpAfter));
    }

    [Fact]
    public void Recovering_then_losing_access_again_restarts_the_countdown()
    {
        // Simulates Run(): a successful TryRecoverFromAccessLost resets the marker to long.MinValue,
        // so a fresh AccessLost run gets its own full budget rather than inheriting the old clock.
        var firstLossSince = Clock.FromSeconds(0);
        var recoveredAt = firstLossSince + Clock.FromSeconds(59);
        Assert.False(DxgiDisplayCaptureSource.ShouldGiveUpOnAccessLost(firstLossSince, recoveredAt, GiveUpAfter));

        var secondLossSince = recoveredAt + Clock.FromSeconds(1);
        var stillWithinNewBudget = secondLossSince + Clock.FromSeconds(59);

        Assert.False(DxgiDisplayCaptureSource.ShouldGiveUpOnAccessLost(secondLossSince, stillWithinNewBudget, GiveUpAfter));
    }
}
