using System.Diagnostics;
using ReplayCapture.App.Native;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

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

/// <summary>
/// Notification-area icon, its menu, and the armed/idle indication.
/// <para>
/// Hand-rolled directly against <c>Shell_NotifyIcon</c> rather than a wrapper library: no Avalonia
/// tray package exposes balloon/toast notifications (Avalonia's own built-in <c>TrayIcon</c> control
/// has icon + menu but no notification API at all), and <see cref="Notify"/> is real, load-bearing
/// UX — the only feedback the user gets for a saved clip, a hotkey conflict, or a failed session
/// start while a fullscreen game has focus.
/// </para>
/// </summary>
public sealed class TrayController : IDisposable
{
    private const uint TrayCallbackMessage = PInvoke.WM_APP + 1;
    private const uint IconId = 1;

    // Menu command ids. The state row (id 0, MF_GRAYED) is never clickable.
    private const int CmdSave = 1;
    private const int CmdOpenFolder = 2;
    private const int CmdOpenLogs = 3;
    private const int CmdSettings = 4;
    private const int CmdStartup = 5;
    private const int CmdExit = 6;

    private readonly MessageOnlyWindow _window;
    private readonly HICON _idleIcon;
    private readonly HICON _armedIcon;
    private readonly HICON _savingIcon;
    private readonly string _outputDirectory;

    private HICON _currentIcon;
    private string _stateHeader = "Starting…";
    private string _tooltip = "ReplayCapture";
    private bool _saveEnabled;
    private bool _startupChecked;
    private bool _disposed;

    public event Action? SaveRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? StartWithWindowsToggled;

    public TrayController(AppConfig config, bool startupTaskInstalled)
    {
        _outputDirectory = config.OutputDirectory;
        _startupChecked = startupTaskInstalled;

        _idleIcon = LoadTrayIcon("idle");
        _armedIcon = LoadTrayIcon("armed");
        _savingIcon = LoadTrayIcon("saving");
        _currentIcon = _idleIcon;

        _window = new MessageOnlyWindow("ReplayCapture.TraySink");
        _window.MessageReceived += OnMessage;

        AddIcon();
    }

    public void SetState(BufferState state, string detail)
    {
        _currentIcon = state switch
        {
            BufferState.Armed => _armedIcon,
            BufferState.Saving => _savingIcon,
            _ => _idleIcon,
        };

        var header = state switch
        {
            BufferState.Armed => "Buffering",
            BufferState.Saving => "Saving replay…",
            _ => "Idle",
        };

        _stateHeader = $"{header} — {detail}";
        _tooltip = $"ReplayCapture · {header}\n{detail}";
        _saveEnabled = state == BufferState.Armed;

        var data = NewNotifyIconData(NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data))
            Log.Warn("Shell_NotifyIcon(NIM_MODIFY) failed while updating the tray icon.");
    }

    public void SetStartupChecked(bool value) => _startupChecked = value;

    public void Notify(string title, string message, bool isError = false)
    {
        try
        {
            var data = NewNotifyIconData(NOTIFY_ICON_DATA_FLAGS.NIF_INFO);
            SetFixedString(data.szInfoTitle.AsSpan(), title);
            SetFixedString(data.szInfo.AsSpan(), message);
            data.dwInfoFlags = isError
                ? NOTIFY_ICON_INFOTIP_FLAGS.NIIF_ERROR
                : NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO;

            if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data))
                Log.Warn($"Could not show notification '{title}': Shell_NotifyIcon(NIM_MODIFY) failed.");
        }
        catch (Exception ex)
        {
            // Toasts are suppressible by Focus Assist and by policy; never let that be fatal.
            Log.Warn($"Could not show notification '{title}': {ex.Message}");
        }
    }

    /// <summary>
    /// Builds and immediately tears down the context menu without displaying it. Used by
    /// <c>--selftest</c> to exercise menu construction on every build.
    /// </summary>
    internal void OpenMenuForDiagnostics()
    {
        using var menu = BuildMenu();
    }

    private void AddIcon()
    {
        var data = NewNotifyIconData(
            NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP);

        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
            Log.Error("Shell_NotifyIcon(NIM_ADD) failed; the tray icon will not appear.");
    }

    private NOTIFYICONDATAW NewNotifyIconData(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _currentIcon,
        };
        SetFixedString(data.szTip.AsSpan(), _tooltip);
        return data;
    }

    private static void SetFixedString(Span<char> buffer, string value)
    {
        var length = Math.Min(value.Length, buffer.Length - 1);
        value.AsSpan(0, length).CopyTo(buffer);
        buffer[length] = '\0';
    }

    private static HICON LoadTrayIcon(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"{name}.ico");
        using var handle = PInvoke.LoadImage(
            null, path, GDI_IMAGE_TYPE.IMAGE_ICON, 0, 0, IMAGE_FLAGS.LR_LOADFROMFILE);

        if (handle.IsInvalid)
            throw new InvalidOperationException($"Could not load tray icon '{path}'.");

        // The icon handle must outlive this SafeHandle (it is owned by the tray icon for the whole
        // process lifetime, released explicitly via DestroyIcon in Dispose), so take ownership of the
        // raw handle rather than letting the SafeHandle release it when this method returns.
        var raw = handle.DangerousGetHandle();
        handle.SetHandleAsInvalid();
        return new HICON(raw);
    }

    private LRESULT? OnMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var mouseMessage = (uint)lParam.Value;
            if (mouseMessage == PInvoke.WM_LBUTTONUP) SettingsRequested?.Invoke();
            else if (mouseMessage is PInvoke.WM_RBUTTONUP or PInvoke.WM_CONTEXTMENU) ShowMenu();
            return new LRESULT(0);
        }

        if (msg == PInvoke.WM_COMMAND)
        {
            HandleCommand((int)(wParam.Value & 0xFFFF));
            return new LRESULT(0);
        }

        return null;
    }

    private void ShowMenu()
    {
        PInvoke.GetCursorPos(out var cursor);

        using var menu = BuildMenu();

        // The classic SetForegroundWindow-before / PostMessage(WM_NULL)-after dance: without it the
        // popup menu does not reliably dismiss itself when the user clicks elsewhere.
        PInvoke.SetForegroundWindow(_window.Handle);
        PInvoke.TrackPopupMenuEx(
            menu,
            (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
            cursor.X, cursor.Y, _window.Handle, null);
        PInvoke.PostMessage(_window.Handle, PInvoke.WM_NULL, 0, 0);
    }

    private DestroyMenuSafeHandle BuildMenu()
    {
        var menu = PInvoke.CreatePopupMenu_SafeHandle();

        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, 0, _stateHeader);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
        PInvoke.AppendMenu(
            menu,
            MENU_ITEM_FLAGS.MF_STRING | (_saveEnabled ? 0 : MENU_ITEM_FLAGS.MF_GRAYED),
            CmdSave, "Save replay now");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdOpenFolder, "Open replay folder");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdSettings, "Settings…");
        PInvoke.AppendMenu(
            menu,
            MENU_ITEM_FLAGS.MF_STRING | (_startupChecked ? MENU_ITEM_FLAGS.MF_CHECKED : 0),
            CmdStartup, "Start with Windows");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdOpenLogs, "Open log folder");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdExit, "Exit");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, 0, $"ReplayCapture v{VersionInfo.Display}");

        return menu;
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case CmdSave: SaveRequested?.Invoke(); break;
            case CmdOpenFolder: OpenFolder(_outputDirectory); break;
            case CmdOpenLogs: OpenFolder(Log.DirectoryPath); break;
            case CmdSettings: SettingsRequested?.Invoke(); break;
            case CmdStartup:
                _startupChecked = !_startupChecked;
                StartWithWindowsToggled?.Invoke(_startupChecked);
                break;
            case CmdExit: ExitRequested?.Invoke(); break;
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = NewNotifyIconData(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);

        PInvoke.DestroyIcon(_idleIcon);
        PInvoke.DestroyIcon(_armedIcon);
        PInvoke.DestroyIcon(_savingIcon);
        _window.Dispose();
    }
}
