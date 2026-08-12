using System.Diagnostics;
using System.IO; // WPF's implicit usings omit System.IO (System.Windows.Shapes.Path collides).
using System.Security.Principal;
using System.Text;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.App.Startup;

/// <summary>
/// Registers ReplayCapture to start at logon through Task Scheduler.
/// <para>
/// A Run-key or Startup-folder entry cannot work here: the app requires an elevated token (see
/// app.manifest), so either route would throw a UAC prompt on every boot. A scheduled task with
/// <c>RunLevel=HighestAvailable</c> starts elevated silently.
/// </para>
/// </summary>
public static class StartupTaskInstaller
{
    public const string TaskName = "ReplayCapture";

    public static bool IsInstalled()
    {
        var (exit, stdout, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return exit == 0 && stdout.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a task exists but launches a different executable than the one running now.
    /// <para>
    /// This is easy to hit in practice: enable startup from a Debug build, later ship a Release
    /// build, and the task silently keeps launching the old path forever. Existence alone is not
    /// enough to call the task correct.
    /// </para>
    /// </summary>
    public static bool IsInstalledForDifferentExecutable()
    {
        if (!IsInstalled()) return false;

        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current)) return false;

        var registered = GetRegisteredCommand();
        if (registered is null) return false;

        return !string.Equals(
            Path.GetFullPath(registered), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The executable path the installed task currently launches, if it can be read.</summary>
    public static string? GetRegisteredCommand()
    {
        var (exit, stdout, _) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (exit != 0) return null;

        var start = stdout.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
        var end = stdout.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end <= start) return null;

        return stdout[(start + "<Command>".Length)..end].Trim().Trim('"');
    }

    public static bool Install(out string? error)
    {
        error = null;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            error = "Could not determine the executable path.";
            return false;
        }

        string xmlPath = Path.Combine(Path.GetTempPath(), $"replaycapture-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks /XML requires UTF-16; UTF-8 is rejected with a parse error.
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);

            var (exit, _, stderr) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (exit != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? $"schtasks exited {exit}" : stderr.Trim();
                Log.Error($"Failed to install startup task: {error}");
                return false;
            }

            Log.Info($"Startup task '{TaskName}' installed for {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("Failed to install startup task", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* temp file */ }
        }
    }

    public static bool Uninstall(out string? error)
    {
        error = null;
        var (exit, _, stderr) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (exit == 0)
        {
            Log.Info($"Startup task '{TaskName}' removed.");
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr) ? $"schtasks exited {exit}" : stderr.Trim();
        return false;
    }

    private static string BuildTaskXml(string exePath)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? WindowsIdentity.GetCurrent().Name;

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts ReplayCapture at logon with the elevated token its global hotkey requires.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{userSid}</UserId>
              <Delay>PT20S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{userSid}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <!-- A replay buffer is useless if Windows stops it the moment you unplug. -->
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <!-- Never time the buffer out; it is meant to run for the whole session. -->
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{exePath}"</Command>
              <Arguments>--from-startup-task</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
