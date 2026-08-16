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
/// The small always-on-top badge that shows the buffer is armed, and flashes when a clip is saved.
/// <para>
/// Two things make this behave rather than becoming a nuisance: it is click-through, so it never
/// swallows a click meant for the game underneath; and it is excluded from capture, so it does not
/// end up burned into every clip the app records.
/// </para>
/// </summary>
public partial class IndicatorWindow : Window
{
    private static readonly IBrush ArmedBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
    private static readonly IBrush SavedBrush = new SolidColorBrush(Color.FromRgb(0x46, 0xA7, 0x58));

    private readonly DispatcherTimer _revert;
    private OverlayCorner _corner = OverlayCorner.TopRight;
    private string _restingText = "REC";
    private IBrush _restingBrush = ArmedBrush;

    public IndicatorWindow()
    {
        InitializeComponent();

        _revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _revert.Tick += (_, _) =>
        {
            _revert.Stop();
            Dot.Fill = _restingBrush;
            Label.Text = _restingText;
        };

        Opened += OnOpened;
        SizeChanged += (_, _) => Reposition();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var handle = new HWND(TopLevel.GetTopLevel(this)!.TryGetPlatformHandle()!.Handle);

        // Click-through and never focusable, so it cannot steal input from a fullscreen game.
        const int GwlExstyle = -20;
        var style = (uint)PInvoke.GetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlExstyle);
        style |= (uint)(WINDOW_EX_STYLE.WS_EX_TRANSPARENT
                        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                        | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                        | WINDOW_EX_STYLE.WS_EX_LAYERED);
        PInvoke.SetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlExstyle, (nint)style);

        // Without this the indicator appears in every recorded clip, which is exactly the kind of
        // detail that is invisible until someone watches a replay and sees it.
        if (!PInvoke.SetWindowDisplayAffinity(handle, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            Log.Warn("Could not exclude the indicator from capture; it may appear in saved clips.");

        Reposition();
    }

    private void Reposition()
    {
        // Matches the WPF version's SystemParameters.WorkArea, which is likewise always the primary
        // monitor's work area regardless of which monitor the indicator ends up on.
        var area = Screens.Primary!.WorkingArea;
        const double margin = 16;
        var scaling = Screens.Primary!.Scaling;

        var left = _corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.X / scaling + margin
            : area.X / scaling + area.Width / scaling - Bounds.Width - margin;

        var top = _corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Y / scaling + margin
            : area.Y / scaling + area.Height / scaling - Bounds.Height - margin;

        Position = new PixelPoint((int)left, (int)top);
    }

    public void Apply(OverlayCorner corner)
    {
        _corner = corner;
        Reposition();
    }

    /// <summary>Shows the buffer as armed, with how much history is currently retained.</summary>
    public void ShowArmed(double secondsBuffered)
    {
        _restingBrush = ArmedBrush;
        _restingText = $"REC {secondsBuffered:0}s";

        if (_revert.IsEnabled) return;
        Dot.Fill = _restingBrush;
        Label.Text = _restingText;
    }

    public void ShowIdle(string reason)
    {
        _restingBrush = IdleBrush;
        _restingText = reason;

        if (_revert.IsEnabled) return;
        Dot.Fill = _restingBrush;
        Label.Text = _restingText;
    }

    /// <summary>Flashes green so the save is confirmed without the user leaving the game.</summary>
    public void FlashSaved(string message)
    {
        Dot.Fill = SavedBrush;
        Label.Text = message;

        var pulse = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(220),
            IterationCount = new IterationCount(4),
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.45) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } },
            },
        };

        _ = pulse.RunAsync(Root);

        _revert.Stop();
        _revert.Start();
    }
}
