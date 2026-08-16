using System.ComponentModel;
using ReplayCapture.App.Native;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ReplayCapture.App.Input;

/// <summary>
/// Owns the process-wide <c>RegisterHotKey</c> registration on a message-only window.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB0B;

    private MessageOnlyWindow? _window;
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
                _window!.Handle,
                HotkeyId,
                (HOT_KEY_MODIFIERS)binding.Modifiers,
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
        if (_window is not null) return;

        _window = new MessageOnlyWindow("ReplayCapture.HotkeySink");
        _window.MessageReceived += OnMessage;
    }

    private LRESULT? OnMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg != WmHotkey || (int)wParam.Value != HotkeyId) return null;

        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the window procedure.
            Log.Error("Hotkey handler threw", ex);
        }

        return new LRESULT(0);
    }

    private void Unbind()
    {
        if (!_registered || _window is null) return;
        PInvoke.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unbind();
        _window?.Dispose();
        _window = null;
    }
}
