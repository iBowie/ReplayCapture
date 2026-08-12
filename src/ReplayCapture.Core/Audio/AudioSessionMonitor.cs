using System.Diagnostics;
using ReplayCapture.Core.Diagnostics;
using Windows.Win32.Media.Audio;

namespace ReplayCapture.Core.Audio;

/// <summary>A process that currently holds an audio session on the default playback device.</summary>
public readonly record struct AudioSessionInfo(uint ProcessId, string ExecutableName)
{
    public override string ToString() => $"{ExecutableName} (pid {ProcessId})";
}

/// <summary>
/// Lists the processes that actually have audio sessions open.
/// <para>
/// This is what makes a <c>proc:*</c> rule tractable. "Everything except Spotify and Discord"
/// cannot be expressed with the exclude mode of process loopback, which takes a single target, and
/// attaching an include-mode loopback client to every running process would mean hundreds of
/// clients. Only processes with an audio session can produce sound, and there are typically fewer
/// than ten, so the rule is evaluated against that set instead.
/// </para>
/// </summary>
public static class AudioSessionMonitor
{
    public static IReadOnlyList<AudioSessionInfo> ListActiveSessions()
    {
        var sessions = new List<AudioSessionInfo>();

        try
        {
            var manager = AudioDeviceEnumerator.OpenDefaultRenderSessionManager();
            var enumerator = manager.GetSessionEnumerator();
            enumerator.GetCount(out var count);

            var seen = new HashSet<uint>();

            for (var i = 0; i < count; i++)
            {
                try
                {
                    enumerator.GetSession(i, out var control);
                    if (control is not IAudioSessionControl2 control2) continue;

                    control2.GetProcessId(out var processId);

                    // pid 0 is the system mix; and one process commonly holds several sessions.
                    if (processId == 0 || !seen.Add(processId)) continue;

                    sessions.Add(new AudioSessionInfo(processId, ResolveExecutableName(processId)));
                }
                catch (Exception ex)
                {
                    Log.Debug($"Skipping an audio session that could not be read: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate audio sessions: {ex.Message}");
        }

        return sessions;
    }

    private static string ResolveExecutableName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName + ".exe";
        }
        catch
        {
            // The process may have exited between enumeration and here.
            return $"pid{processId}.exe";
        }
    }
}
