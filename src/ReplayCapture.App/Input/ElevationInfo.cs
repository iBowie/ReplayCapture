using System.Security.Principal;

namespace ReplayCapture.App.Input;

public static class ElevationInfo
{
    /// <summary>
    /// Whether this process is running with an elevated token. When false, the global hotkey will
    /// register successfully but silently fail to fire while an elevated window has focus, which is
    /// exactly the failure mode the app must warn about rather than hide.
    /// </summary>
    public static bool IsElevated { get; } = DetectElevation();

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
