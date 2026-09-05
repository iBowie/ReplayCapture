using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayCapture.App.Overlay;

/// <summary>
/// The on-screen toast the app uses instead of a tray balloon.
/// <para>
/// A shell balloon (or a Windows toast) is an ordinary desktop window: it is composited into the
/// desktop like anything else, so every notification this app raises would be burned into whatever
/// is being recorded a moment later — "Replay saved — 60s" sitting in the corner of the *next*
/// clip. This window carries <c>WDA_EXCLUDEFROMCAPTURE</c>, exactly like
/// <see cref="IndicatorWindow"/>, so the user sees it and the recording never does. It is also
/// click-through and never activates, so it cannot steal a click or focus from a game.
/// </para>
/// <para>
/// One window shows one notification at a time; anything raised while a notification is up is
/// queued behind it rather than replacing it mid-read.
/// </para>
/// </summary>
public partial class NotificationWindow : Window
{
    private static readonly IBrush InfoAccent = new SolidColorBrush(Color.FromRgb(0x46, 0xA7, 0x58));
    private static readonly IBrush ErrorAccent = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(4.5);

    /// <summary>Errors stay up longer: they usually ask the user to go and do something.</summary>
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(260);

    /// <summary>
    /// Vertical room left for <see cref="IndicatorWindow"/>, which sits in the same corner: its
    /// badge height plus the same 16px margin it positions itself with.
    /// </summary>
    private const double IndicatorInset = 44;

    private const double EdgeMargin = 16;

    /// <summary>
    /// Deliberately shallow. A backlog of stale toasts is worse than a dropped one — by the time a
    /// fifth notification is waiting, the first is no longer news.
    /// </summary>
    private const int MaxQueued = 3;

    private readonly Queue<(string Title, string Message, bool IsError)> _pending = new();
    private readonly DispatcherTimer _dismiss;

    private OverlayCorner _corner = OverlayCorner.TopRight;
    private bool _indicatorVisible = true;
    private bool _showing;
    private bool _closed;

    public NotificationWindow()
    {
        InitializeComponent();

        _dismiss = new DispatcherTimer();
        _dismiss.Tick += (_, _) =>
        {
            _dismiss.Stop();
            _ = DismissAsync();
        };

        Opened += (_, _) =>
        {
            ApplyNativeStyles();
            Reposition();
        };
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Follows the indicator's corner, so the two never end up on opposite sides.</summary>
    public void Apply(OverlayCorner corner, bool indicatorVisible)
    {
        _corner = corner;
        _indicatorVisible = indicatorVisible;
        Reposition();
    }

    /// <summary>Queues a notification. Safe to call before the window has ever been shown.</summary>
    public void Post(string title, string message, bool isError = false)
    {
        if (_closed) return;
        if (_pending.Count >= MaxQueued) _pending.Dequeue();
        _pending.Enqueue((title, message, isError));

        if (!_showing) ShowNext();
    }

    private void ShowNext()
    {
        if (_closed || _showing || _pending.Count == 0) return;

        var (title, message, isError) = _pending.Dequeue();

        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(message);
        Accent.Background = isError ? ErrorAccent : InfoAccent;

        _showing = true;
        Root.Opacity = 0;
        Show();
        Reposition();

        _dismiss.Interval = isError ? ErrorDuration : InfoDuration;
        _dismiss.Start();

        _ = FadeAsync(0, 1, FadeIn);
    }

    private async Task DismissAsync()
    {
        await FadeAsync(1, 0, FadeOut);

        // Shutdown can close this window while a notification is still fading out.
        if (_closed) return;

        Hide();
        _showing = false;

        ShowNext();
    }

    private async Task FadeAsync(double from, double to, TimeSpan duration)
    {
        var fade = new Animation
        {
            Duration = duration,
            FillMode = FillMode.Forward,
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, to) } },
            },
        };

        try
        {
            await fade.RunAsync(Root);
        }
        catch (Exception ex)
        {
            Log.Warn($"Notification fade failed: {ex.Message}");
        }

        Root.Opacity = to;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _dismiss.Stop();
        _pending.Clear();
        base.OnClosed(e);
    }

    private void ApplyNativeStyles()
    {
        var handle = new HWND(TopLevel.GetTopLevel(this)!.TryGetPlatformHandle()!.Handle);

        const int GwlExstyle = -20;
        var style = (uint)PInvoke.GetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlExstyle);
        style |= (uint)(WINDOW_EX_STYLE.WS_EX_TRANSPARENT
                        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                        | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                        | WINDOW_EX_STYLE.WS_EX_LAYERED);
        PInvoke.SetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlExstyle, (nint)style);

        // The whole point of this window over a tray balloon: the user sees it, the recording
        // does not.
        if (!PInvoke.SetWindowDisplayAffinity(handle, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            Log.Warn("Could not exclude the notification overlay from capture; it may appear in saved clips.");
    }

    /// <summary>
    /// Sits under (or above) the indicator in the same corner of the primary display, positioned
    /// against the primary work area exactly as <see cref="IndicatorWindow"/> does.
    /// </summary>
    private void Reposition()
    {
        var screen = Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var inset = _indicatorVisible ? IndicatorInset : 0;

        var left = _corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.X / scaling + EdgeMargin
            : area.X / scaling + area.Width / scaling - Bounds.Width - EdgeMargin;

        var top = _corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Y / scaling + EdgeMargin + inset
            : area.Y / scaling + area.Height / scaling - Bounds.Height - EdgeMargin - inset;

        Position = new PixelPoint((int)left, (int)top);
    }
}
