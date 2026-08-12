using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    private static readonly Brush ArmedBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));
    private static readonly Brush SavedBrush = new SolidColorBrush(Color.FromRgb(0x46, 0xA7, 0x58));

    private readonly DispatcherTimer _revert;
    private OverlayCorner _corner = OverlayCorner.TopRight;
    private string _restingText = "REC";
    private Brush _restingBrush = ArmedBrush;

    public IndicatorWindow()
    {
        InitializeComponent();

        ArmedBrush.Freeze();
        IdleBrush.Freeze();
        SavedBrush.Freeze();

        _revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _revert.Tick += (_, _) =>
        {
            _revert.Stop();
            Dot.Fill = _restingBrush;
            Label.Text = _restingText;
        };

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new HWND(new WindowInteropHelper(this).Handle);

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
        var area = SystemParameters.WorkArea;
        const double margin = 16;

        Left = _corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.Left + margin
            : area.Right - ActualWidth - margin;

        Top = _corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Top + margin
            : area.Bottom - ActualHeight - margin;
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

        var pulse = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(220))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
        };
        Root.BeginAnimation(OpacityProperty, pulse);

        _revert.Stop();
        _revert.Start();
    }
}
