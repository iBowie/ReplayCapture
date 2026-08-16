using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayCapture.App.Native;

/// <summary>
/// A message-only HWND (<c>HWND_MESSAGE</c>): never visible, never in the taskbar or Alt+Tab, but a
/// real window that still pumps through the process's Win32 message loop. This is the vehicle both
/// the global hotkey (<c>WM_HOTKEY</c>) and the tray icon (its <c>Shell_NotifyIcon</c> callback
/// message) need — Avalonia has no cross-platform equivalent, by design, since neither concept exists
/// outside Win32.
/// </summary>
internal sealed unsafe class MessageOnlyWindow : IDisposable
{
    private static readonly HWND HwndMessage = new(-3);

    private readonly WNDPROC _wndProc;
    private readonly string _className;
    private readonly UnownedModuleHandle _moduleHandle;
    private bool _disposed;

    public HWND Handle { get; }

    /// <summary>Raised for every message this window receives. Return null to fall through to DefWindowProc.</summary>
    public event Func<uint, WPARAM, LPARAM, LRESULT?>? MessageReceived;

    public MessageOnlyWindow(string className)
    {
        _className = className;
        _wndProc = WndProc;

        // Marshal.GetHINSTANCE, not PInvoke.GetModuleHandle: the latter wraps the result in a
        // FreeLibrarySafeHandle that calls FreeLibrary on release, which is wrong for a handle
        // GetModuleHandle never took a LoadLibrary reference for in the first place. Wrapped in our
        // own no-op SafeHandle since CreateWindowEx/UnregisterClass's generated overloads want one.
        _moduleHandle = new UnownedModuleHandle(Marshal.GetHINSTANCE(typeof(MessageOnlyWindow).Module));

        fixed (char* classNamePtr = className)
        {
            var wndClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = (HINSTANCE)_moduleHandle.DangerousGetHandle(),
                lpszClassName = classNamePtr,
            };

            if (PInvoke.RegisterClassEx(in wndClass) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx failed for '{className}': {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        Handle = PInvoke.CreateWindowEx(
            0, className, className, 0,
            0, 0, 0, 0,
            HwndMessage, default, _moduleHandle, null);

        if (Handle.IsNull)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed for '{className}': {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam) =>
        MessageReceived?.Invoke(msg, wParam, lParam) ?? PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!Handle.IsNull) PInvoke.DestroyWindow(Handle);
        PInvoke.UnregisterClass(_className, _moduleHandle);
    }

    /// <summary>A SafeHandle around a module handle we don't own and must never free (see ctor).</summary>
    private sealed class UnownedModuleHandle(nint handle) : SafeHandle(handle, ownsHandle: false)
    {
        public override bool IsInvalid => false;
        protected override bool ReleaseHandle() => true;
    }
}
