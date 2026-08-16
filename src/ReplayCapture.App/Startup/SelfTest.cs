using Avalonia.Threading;
using ReplayCapture.App.Overlay;
using ReplayCapture.App.Tray;
using ReplayCapture.App.Views;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.App.Startup;

/// <summary>
/// Headless UI smoke test, run with <c>--selftest</c>.
/// <para>
/// Avalonia, like WPF, defers binding/template problems to runtime: this exercises every window
/// and the tray menu build so a broken binding path or missing resource fails a build rather than
/// only showing up the first time a user opens it.
/// </para>
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();

        Check(failures, "config round-trip", () =>
        {
            var store = new ConfigStore();
            var config = store.Load();
            if (config.AudioTracks.Count == 0) throw new InvalidOperationException("no audio tracks");
        });

        var config = new ConfigStore().Load();

        TrayController? tray = null;
        Check(failures, "tray icon", () =>
        {
            tray = new TrayController(config, startupTaskInstalled: false);
            tray.SetState(BufferState.Armed, "selftest");
        });

        Check(failures, "tray context menu", () => tray!.OpenMenuForDiagnostics());

        IndicatorWindow? indicator = null;
        Check(failures, "indicator window", () =>
        {
            indicator = new IndicatorWindow();
            indicator.Show();
            indicator.Apply(OverlayCorner.TopRight);
            indicator.ShowArmed(42);
            indicator.FlashSaved("saved 60s");
            indicator.ShowIdle("idle");
        });

        Check(failures, "settings window", () =>
        {
            var settings = new SettingsWindow(config);
            settings.Show();
            settings.UpdateLayout();
            settings.Close();
        });

        Check(failures, "settings validation rejects bad input", () =>
        {
            var viewModel = new SettingsViewModel(config) { BufferSeconds = 2 };
            if (viewModel.TryBuild() is not null)
                throw new InvalidOperationException("a 2-second buffer should have been rejected");

            viewModel.BufferSeconds = 60;
            viewModel.Hotkey = "NotAKey";
            if (viewModel.TryBuild() is not null)
                throw new InvalidOperationException("an invalid hotkey should have been rejected");

            viewModel.Hotkey = "Alt+F10";
            if (viewModel.TryBuild() is null)
                throw new InvalidOperationException($"valid settings were rejected: {viewModel.ValidationError}");
        });

        indicator?.Close();
        tray?.Dispose();

        foreach (var failure in failures) Console.Error.WriteLine($"  FAIL  {failure}");
        Console.WriteLine(failures.Count == 0
            ? "  selftest PASSED"
            : $"  selftest FAILED ({failures.Count} problem(s))");

        return failures.Count == 0 ? 0 : 1;
    }

    private static void Check(List<string> failures, string what, Action action)
    {
        try
        {
            action();

            // Lets deferred layout and template work actually happen before the next check —
            // otherwise a failure would surface against the wrong step.
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"  ok    {what}");
        }
        catch (Exception ex)
        {
            Log.Error($"selftest: {what}", ex);
            failures.Add($"{what}: {ex.GetBaseException().Message}");
        }
    }
}
