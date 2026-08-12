using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayCapture.App.Input;

/// <summary>
/// Describes whatever window currently has focus. Used to prove — in the log and in the M0 toast —
/// that the hotkey fired while an *elevated* window was in front, which is the whole point of
/// running the app elevated.
/// </summary>
internal static class ForegroundWindowInfo
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static string Describe()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "(no foreground window)";

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);

            GetWindowThreadProcessId(hwnd, out var pid);
            string exe;
            try
            {
                exe = Process.GetProcessById((int)pid).ProcessName + ".exe";
            }
            catch
            {
                // Access denied reading a higher-integrity process is itself the interesting signal.
                exe = $"pid {pid} (not readable — likely elevated)";
            }

            var text = title.ToString();
            return text.Length > 0 ? $"{exe}: \"{text}\"" : exe;
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.Message})";
        }
    }
}
