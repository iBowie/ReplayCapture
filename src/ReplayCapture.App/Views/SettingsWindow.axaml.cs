using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Input;

namespace ReplayCapture.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>Raised when the user accepts the changes.</summary>
    public event Action<AppConfig>? Saved;

    /// <summary>Raised when the user dismisses the window without saving.</summary>
    public event Action? Cancelled;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(config);
        DataContext = _viewModel;
        Title = $"ReplayCapture settings — v{VersionInfo.Display}";
    }

    /// <summary>
    /// Captures a shortcut as it is pressed rather than asking the user to type its name. Modifier
    /// keys alone are ignored so the box does not fill with "Ctrl" while a combination is half done.
    /// </summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
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

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)!.StorageProvider;

        IStorageFolder? startLocation = null;
        if (Directory.Exists(_viewModel.OutputDirectory))
            startLocation = await storageProvider.TryGetFolderFromPathAsync(_viewModel.OutputDirectory);

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where replays are saved",
            SuggestedStartLocation = startLocation,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _viewModel.OutputDirectory = path;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var config = _viewModel.TryBuild();
        if (config is null) return;   // ValidationError is bound and already visible

        Saved?.Invoke(config);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke();
        Close();
    }
}
