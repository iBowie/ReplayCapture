using System.ComponentModel;
using System.Windows.Interop;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Input;
using Windows.Win32;

namespace ReplayCapture.App.Input;

/// <summary>
/// Owns the process-wide <c>RegisterHotKey</c> registration on a message-only window.
/// <para>
/// The window is deliberately message-only (<c>HWND_MESSAGE</c>): it never appears on screen, in
/// the taskbar, or in Alt+Tab, but it still receives <c>WM_HOTKEY</c>.
/// </para>
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB0B;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _registered;

    /// <summary>Raised on the UI thread when the bound hotkey fires.</summary>
    public event Action? Pressed;

    public HotkeyBinding? Current { get; private set; }

    /// <summary>
    /// Binds <paramref name="binding"/>, replacing any previous registration.
    /// Returns false (without throwing) when another process already owns the combination —
    /// the caller is expected to surface that, because a silently dead hotkey is the worst outcome.
    /// </summary>
    public bool TryBind(HotkeyBinding binding, out string? error)
    {
        error = null;
        EnsureWindow();
        Unbind();

        if (!PInvoke.RegisterHotKey(
                new Windows.Win32.Foundation.HWND(_source!.Handle),
                HotkeyId,
                (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)binding.Modifiers,
                binding.VirtualKey))
        {
            var win32 = new Win32Exception();
            // ERROR_HOTKEY_ALREADY_REGISTERED
            error = win32.NativeErrorCode == 1409
                ? $"{binding.Display} is already claimed by another application."
                : $"Could not register {binding.Display}: {win32.Message}";
            Log.Error($"RegisterHotKey failed for {binding.Display}: {error}");
            return false;
        }

        _registered = true;
        Current = binding;
        Log.Info($"Hotkey {binding.Display} registered (elevated: {ElevationInfo.IsElevated}).");
        return true;
    }

    private void EnsureWindow()
    {
        if (_source is not null) return;

        _source = new HwndSource(new HwndSourceParameters("ReplayCapture.HotkeySink")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

        handled = true;
        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the window procedure.
            Log.Error("Hotkey handler threw", ex);
        }

        return IntPtr.Zero;
    }

    private void Unbind()
    {
        if (!_registered || _source is null) return;
        PInvoke.UnregisterHotKey(new Windows.Win32.Foundation.HWND(_source.Handle), HotkeyId);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unbind();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
