using System.Windows;
using System.Windows.Threading;
using ReplayCapture.App.Overlay;
using ReplayCapture.App.Tray;
using ReplayCapture.App.Views;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.App.Startup;

/// <summary>
/// Headless UI smoke test, run with <c>--selftest</c>.
/// <para>
/// WPF defers almost everything to runtime: a XAML typo, a bad binding path or a missing resource
/// compiles cleanly and only fails when the control is first realised. This exercises every window
/// and — importantly — opens the tray context menu, because realising its templates is exactly what
/// crashed the app the first time it was run for real.
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

        // The regression that matters: this is the code path that threw
        // "Cannot find non-neutral culture related to 'en-us'".
        Check(failures, "tray context menu templates", () => tray!.OpenMenuForDiagnostics());

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
            // Force a full layout pass so every tab's templates and bindings are realised.
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

            // Let the dispatcher run so deferred layout and template work actually happens before
            // the next check — otherwise a failure would surface against the wrong step.
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Console.WriteLine($"  ok    {what}");
        }
        catch (Exception ex)
        {
            Log.Error($"selftest: {what}", ex);
            failures.Add($"{what}: {ex.GetBaseException().Message}");
        }
    }
}
