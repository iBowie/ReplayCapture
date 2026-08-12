using System.IO; // WPF's implicit usings omit System.IO (System.Windows.Shapes.Path collides).
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Input;

namespace ReplayCapture.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>The config the user accepted, or null if they cancelled.</summary>
    public AppConfig? Result { get; private set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(config);
        DataContext = _viewModel;
    }

    /// <summary>
    /// Captures a shortcut as it is pressed rather than asking the user to type its name. Modifier
    /// keys alone are ignored so the box does not fill with "Ctrl" while a combination is half done.
    /// </summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());

        var candidate = string.Join("+", parts);
        if (HotkeyBinding.TryParse(candidate, out var binding, out _))
        {
            _viewModel.Hotkey = binding.Display;
            _viewModel.ValidationError = null;
        }
        else
        {
            _viewModel.ValidationError = $"{candidate} is not usable as a global hotkey.";
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where replays are saved",
            InitialDirectory = Directory.Exists(_viewModel.OutputDirectory) ? _viewModel.OutputDirectory : null,
        };

        if (dialog.ShowDialog(this) == true) _viewModel.OutputDirectory = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var config = _viewModel.TryBuild();
        if (config is null) return;   // ValidationError is bound and already visible

        Result = config;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
