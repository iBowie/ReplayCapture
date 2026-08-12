using System.Diagnostics;
using System.IO; // WPF's implicit usings omit System.IO (System.Windows.Shapes.Path collides).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.App.Tray;

public enum BufferState
{
    /// <summary>Not capturing — either stopped by the user or failed to start.</summary>
    Idle,

    /// <summary>Ring buffer running; a save is possible at any moment.</summary>
    Armed,

    /// <summary>Flushing a clip to disk. Buffering continues throughout.</summary>
    Saving,
}

/// <summary>Notification-area icon, its menu, and the armed/idle indication.</summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _saveItem;
    private readonly MenuItem _stateItem;
    private readonly MenuItem _startupItem;

    private static readonly BitmapImage IdleIcon = LoadIcon("idle");
    private static readonly BitmapImage ArmedIcon = LoadIcon("armed");
    private static readonly BitmapImage SavingIcon = LoadIcon("saving");

    public event Action? SaveRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? StartWithWindowsToggled;

    public TrayController(AppConfig config, bool startupTaskInstalled)
    {
        _stateItem = new MenuItem { Header = "Starting…", IsEnabled = false };
        _saveItem = new MenuItem { Header = "Save replay now" };
        _saveItem.Click += (_, _) => SaveRequested?.Invoke();

        var openFolder = new MenuItem { Header = "Open replay folder" };
        openFolder.Click += (_, _) => OpenFolder(config.OutputDirectory);

        var openLogs = new MenuItem { Header = "Open log folder" };
        openLogs.Click += (_, _) => OpenFolder(Log.DirectoryPath);

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        _startupItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = startupTaskInstalled,
        };
        _startupItem.Click += (_, _) => StartWithWindowsToggled?.Invoke(_startupItem.IsChecked);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(_stateItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_saveItem);
        menu.Items.Add(openFolder);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(_startupItem);
        menu.Items.Add(openLogs);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            IconSource = IdleIcon,
            ToolTipText = "ReplayCapture",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => SettingsRequested?.Invoke();
        _icon.ForceCreate();
    }

    public void SetState(BufferState state, string detail)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _icon.IconSource = state switch
            {
                BufferState.Armed => ArmedIcon,
                BufferState.Saving => SavingIcon,
                _ => IdleIcon,
            };

            var header = state switch
            {
                BufferState.Armed => "Buffering",
                BufferState.Saving => "Saving replay…",
                _ => "Idle",
            };

            _stateItem.Header = $"{header} — {detail}";
            _icon.ToolTipText = $"ReplayCapture · {header}\n{detail}";
            _saveItem.IsEnabled = state == BufferState.Armed;
        });
    }

    public void SetStartupChecked(bool value) =>
        Application.Current.Dispatcher.Invoke(() => _startupItem.IsChecked = value);

    public void Notify(string title, string message, bool isError = false)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                _icon.ShowNotification(
                    title,
                    message,
                    isError ? NotificationIcon.Error : NotificationIcon.Info);
            }
            catch (Exception ex)
            {
                // Toasts are suppressible by Focus Assist and by policy; never let that be fatal.
                Log.Warn($"Could not show notification '{title}': {ex.Message}");
            }
        });
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open '{path}'", ex);
        }
    }

    /// <summary>
    /// Opens the context menu programmatically. Used by <c>--selftest</c>: realising the menu's
    /// WPF templates is what surfaced the InvariantGlobalization crash, so it is worth exercising
    /// on every build rather than waiting for a user to right-click.
    /// </summary>
    internal void OpenMenuForDiagnostics()
    {
        if (_icon.ContextMenu is { } menu) menu.IsOpen = true;
    }

    private static BitmapImage LoadIcon(string name) =>
        new(new Uri($"pack://application:,,,/Assets/{name}.ico", UriKind.Absolute));

    public void Dispose() => _icon.Dispose();
}
