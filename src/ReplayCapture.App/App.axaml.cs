using System.Media;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Win32;
using ReplayCapture.App.Input;
using ReplayCapture.App.Overlay;
using ReplayCapture.App.Startup;
using ReplayCapture.App.Tray;
using ReplayCapture.App.Views;
using ReplayCapture.Core;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayCapture.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\ReplayCapture.SingleInstance";

    /// <summary>
    /// How long to wait after a resume-from-sleep notification before rebuilding capture. Drivers
    /// for the GPU and audio endpoints are not necessarily ready the instant Windows reports resume,
    /// and probing them too early just reproduces the failure we are trying to recover from.
    /// </summary>
    private static readonly TimeSpan ResumeSettleDelay = TimeSpan.FromSeconds(3);

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Mutex? _singleInstance;
    private ConfigStore _configStore = null!;
    private AppConfig _config = null!;
    private TrayController? _tray;
    private GlobalHotkeyService? _hotkeys;
    private IndicatorWindow? _indicator;
    private DispatcherTimer? _statusTimer;

    private ReplaySession? _session;
    private string? _sessionError;
    private int _saveInProgress;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _desktop = desktop;

            if (desktop.Args?.Contains("--selftest", StringComparer.OrdinalIgnoreCase) == true)
            {
                desktop.Shutdown(SelfTest.Run());
            }
            else
            {
                Start(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // A second elevated copy would fight the first for the hotkey registration. No Avalonia
            // window exists yet at this point, so this goes through raw Win32 rather than a dialog.
            PInvoke.MessageBox(
                default(HWND),
                "ReplayCapture is already running. Look for it in the notification area.",
                "ReplayCapture",
                MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
            desktop.Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled domain exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"ReplayCapture starting — elevated: {ElevationInfo.IsElevated}, " +
                 $"args: [{string.Join(' ', desktop.Args ?? [])}]");

        _configStore = new ConfigStore();
        _config = _configStore.Load();

        _tray = new TrayController(_config, StartupTaskInstaller.IsInstalled());
        _tray.SaveRequested += OnSaveRequested;
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.ExitRequested += () => desktop.Shutdown();
        _tray.StartWithWindowsToggled += OnStartWithWindowsToggled;

        BindHotkey();
        SyncStartupTask();
        WarnIfNotElevated();
        CreateIndicator();

        // The app must never keep the machine awake or the display on, and must come back cleanly
        // once it sleeps: SystemEvents fires this on its own hidden-window thread, so resume is
        // never missed even though the pacer and capture threads are themselves frozen for the
        // duration of the sleep.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        desktop.ShutdownRequested += (_, _) => OnShutdown();

        StartSession();
    }

    // ---------------------------------------------------------------- pipeline

    /// <summary>
    /// Builds and arms the capture session off the UI thread — creating the D3D device, opening
    /// audio endpoints and starting encoders takes long enough to freeze the tray menu otherwise.
    /// </summary>
    private void StartSession()
    {
        var config = _config;
        _tray?.SetState(BufferState.Idle, "starting…");

        Task.Run(() =>
        {
            try
            {
                var session = new ReplaySession(config);
                session.RecoveryRequired += OnRecoveryRequired;
                session.Start();
                return (Session: session, Error: (string?)null);
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the capture session", ex);
                return (Session: (ReplaySession?)null, Error: ex.Message);
            }
        }).ContinueWith(task =>
        {
            _session = task.Result.Session;
            _sessionError = task.Result.Error;

            if (_sessionError is not null)
            {
                _tray?.Notify("Capture could not start", _sessionError, isError: true);
                _tray?.SetState(BufferState.Idle, _sessionError);
                _indicator?.ShowIdle("capture off");
            }
            else
            {
                Log.Info("Capture session armed.");
            }

            UpdateStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StopSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;

        // Disposal joins capture and encode threads, so keep it off the UI thread.
        Task.Run(() =>
        {
            try { session.Dispose(); }
            catch (Exception ex) { Log.Error("Failed to stop the capture session cleanly", ex); }
        });
    }

    private void RestartSession()
    {
        StopSession();
        StartSession();
    }

    /// <summary>
    /// The session detected something it cannot heal in place — a lost GPU or a changed set of
    /// displays. Rebuild it rather than leaving a dead buffer that still looks armed.
    /// </summary>
    private void OnRecoveryRequired(string reason) => Dispatcher.UIThread.InvokeAsync(() => RebuildCapture(reason));

    /// <summary>
    /// The system woke from sleep. GPU device loss and closed capture items are already caught by
    /// the watchdog, but WASAPI endpoints invalidated by sleep are not — a dead audio client just
    /// spins on the same error forever rather than reporting itself as lost. Rebuilding unconditionally
    /// on resume replaces every endpoint and capture item with fresh ones instead of waiting to see
    /// which parts of the pipeline sleep actually broke.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;

        Log.Info("System resumed from sleep; capture will rebuild shortly.");
        Task.Delay(ResumeSettleDelay).ContinueWith(
            _ => Dispatcher.UIThread.InvokeAsync(() => RebuildCapture("the system resumed from sleep")),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Tears the session down and starts a fresh one. Deferred rather than forced when a save is
    /// in flight, because that save is reading ring buffers the rebuild would dispose out from
    /// under it; the watchdog keeps re-checking, so a deferred rebuild is not lost, only delayed.
    /// </summary>
    private void RebuildCapture(string reason)
    {
        if (_saveInProgress != 0)
        {
            Log.Info($"Deferring capture rebuild ({reason}) until the current save completes.");
            return;
        }

        Log.Warn($"Rebuilding capture: {reason}.");
        _tray?.Notify("Capture restarted", $"{char.ToUpperInvariant(reason[0])}{reason[1..]}.");
        _indicator?.ShowIdle("restarting…");
        RestartSession();
    }

    private void UpdateStatus()
    {
        var session = _session;
        if (session is null)
        {
            _tray?.SetState(BufferState.Idle, _sessionError ?? "not capturing");
            return;
        }

        var seconds = session.Recorders.Count > 0 ? session.Recorders.Max(r => r.SecondsBuffered) : 0;
        var megabytes = (session.Recorders.Sum(r => r.BufferedBytes) + session.Audio.TotalBytes) / (1024 * 1024);

        var detail = $"{seconds:0}s buffered · {session.Recorders.Count} display(s) · " +
                     $"{session.Audio.Tracks.Count} track(s) · {megabytes} MB";

        if (_saveInProgress == 0) _tray?.SetState(BufferState.Armed, detail);
        _indicator?.ShowArmed(seconds);
    }

    // ---------------------------------------------------------------- saving

    private void OnSaveRequested()
    {
        var session = _session;
        if (session is null)
        {
            _tray?.Notify("Nothing to save", _sessionError ?? "Capture is not running.", isError: true);
            return;
        }

        // Holding the hotkey, or hammering it, must not start overlapping writes.
        if (Interlocked.CompareExchange(ref _saveInProgress, 1, 0) != 0)
        {
            Log.Info("Save ignored: a save is already in progress.");
            return;
        }

        Log.Info($"Save triggered (foreground: {ForegroundWindowInfo.Describe()}).");
        _tray?.SetState(BufferState.Saving, "writing clip…");

        Task.Run(() => session.Save()).ContinueWith(task =>
        {
            Interlocked.Exchange(ref _saveInProgress, 0);

            if (task.IsFaulted)
            {
                var message = task.Exception?.GetBaseException().Message ?? "unknown error";
                Log.Error("Save failed", task.Exception);
                _tray?.Notify("Save failed", message, isError: true);
                _indicator?.FlashSaved("save failed");
                return;
            }

            ReportSaveResults(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ReportSaveResults(IReadOnlyList<SaveResult> results)
    {
        var written = results.Where(r => r.Success).ToList();

        if (written.Count == 0)
        {
            var reason = results.FirstOrDefault().Error ?? "nothing was buffered yet";
            _tray?.Notify("Nothing saved", reason, isError: true);
            _indicator?.FlashSaved("nothing to save");
            return;
        }

        var duration = written.Max(r => r.DurationSeconds);
        var megabytes = written.Sum(r => r.Bytes) / (1024 * 1024);
        var folder = Path.GetDirectoryName(written[0].Path) ?? written[0].Path;

        _tray?.Notify(
            $"Replay saved — {duration:0}s",
            $"{written.Count} file(s), {megabytes} MB{Environment.NewLine}{folder}");

        _indicator?.FlashSaved($"saved {duration:0}s");

        if (_config.PlaySoundOnSave)
        {
            // The overlay cannot draw over a fullscreen-exclusive game, so the sound is the only
            // confirmation the user gets in exactly the case they most need one.
            try { SystemSounds.Asterisk.Play(); }
            catch (Exception ex) { Log.Warn($"Could not play the save sound: {ex.Message}"); }
        }

        UpdateStatus();
    }

    // ---------------------------------------------------------------- settings

    private void OnSettingsRequested()
    {
        var existing = _desktop?.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(_config);
        window.Saved += ApplyConfig;
        window.Show();
    }

    private void ApplyConfig(AppConfig updated)
    {
        var previous = _config;
        _config = updated;
        _configStore.Save(updated);

        if (!string.Equals(previous.Hotkey, updated.Hotkey, StringComparison.OrdinalIgnoreCase))
            BindHotkey();

        if (previous.StartWithWindows != updated.StartWithWindows) SyncStartupTask();

        ApplyIndicatorSettings();

        // Only these actually change the shape of the pipeline; everything else applies live, and
        // restarting needlessly would empty the buffer for no reason.
        var pipelineChanged =
            previous.BufferSeconds != updated.BufferSeconds
            || previous.MaxRingMemoryMegabytes != updated.MaxRingMemoryMegabytes
            || previous.CaptureBackend != updated.CaptureBackend
            || previous.VideoEncoderBackend != updated.VideoEncoderBackend
            || !previous.Displays.SequenceEqual(updated.Displays)
            || !previous.AudioTracks.SequenceEqual(updated.AudioTracks);

        if (pipelineChanged)
        {
            Log.Info("Configuration changed in a way that requires restarting capture.");
            _tray?.Notify("Settings applied", "Restarting capture; the buffer will fill again shortly.");
            RestartSession();
        }
        else
        {
            _session?.UpdateConfig(updated);
            _tray?.Notify("Settings applied", "Changes took effect without interrupting the buffer.");
        }
    }

    // ---------------------------------------------------------------- indicator

    private void CreateIndicator()
    {
        _indicator = new IndicatorWindow();
        ApplyIndicatorSettings();
    }

    private void ApplyIndicatorSettings()
    {
        if (_indicator is null) return;

        _indicator.Apply(_config.OverlayCorner);

        if (_config.ShowOverlayIndicator) _indicator.Show();
        else _indicator.Hide();
    }

    // ---------------------------------------------------------------- hotkey & startup

    private void BindHotkey()
    {
        if (!HotkeyBinding.TryParse(_config.Hotkey, out var binding, out var parseError))
        {
            Log.Warn($"Config hotkey '{_config.Hotkey}' is invalid ({parseError}); using Alt+F10.");
            binding = HotkeyBinding.Default;
        }

        if (_hotkeys is null)
        {
            _hotkeys = new GlobalHotkeyService();
            _hotkeys.Pressed += OnSaveRequested;
        }

        if (!_hotkeys.TryBind(binding, out var bindError))
        {
            // A dead hotkey is the single most confusing failure this app can have, so it is
            // surfaced loudly rather than left in the log.
            _tray!.Notify("Hotkey unavailable", $"{bindError} Change it in Settings.", isError: true);
        }
    }

    private void SyncStartupTask()
    {
        var installed = StartupTaskInstaller.IsInstalled();

        // A task pointing at a stale executable is worse than none: it looks enabled and launches
        // the wrong build. Re-register it against the copy that is actually running.
        if (installed && _config.StartWithWindows && StartupTaskInstaller.IsInstalledForDifferentExecutable())
        {
            var previous = StartupTaskInstaller.GetRegisteredCommand();
            if (StartupTaskInstaller.Install(out _))
            {
                Log.Info($"Startup task re-pointed from '{previous}' to '{Environment.ProcessPath}'.");
                _tray?.Notify("Startup entry updated",
                    "The logon task was pointing at an older build and now launches this one.");
            }

            _tray?.SetStartupChecked(true);
            return;
        }

        if (_config.StartWithWindows == installed)
        {
            _tray?.SetStartupChecked(installed);
            return;
        }

        if (_config.StartWithWindows)
        {
            if (StartupTaskInstaller.Install(out var error)) _tray!.SetStartupChecked(true);
            else _tray!.Notify("Could not enable startup", error ?? "Unknown error", isError: true);
        }
        else
        {
            StartupTaskInstaller.Uninstall(out _);
            _tray!.SetStartupChecked(false);
        }
    }

    private void WarnIfNotElevated()
    {
        if (ElevationInfo.IsElevated) return;

        Log.Warn("Running unelevated — the hotkey will not fire while an elevated window has focus.");
        _tray!.Notify(
            "Running without administrator rights",
            $"{_config.Hotkey} will be ignored while an elevated app (Task Manager, an anti-cheat " +
            "game, an admin console) has focus. Restart ReplayCapture as administrator.",
            isError: true);
    }

    private void OnStartWithWindowsToggled(bool enabled)
    {
        var ok = enabled
            ? StartupTaskInstaller.Install(out var error)
            : StartupTaskInstaller.Uninstall(out error);

        if (ok)
        {
            _config = _config with { StartWithWindows = enabled };
            _configStore.Save(_config);
        }
        else
        {
            _tray!.Notify("Startup change failed", error ?? "Unknown error", isError: true);
            _tray.SetStartupChecked(!enabled);
        }
    }

    private void OnShutdown()
    {
        Log.Info("ReplayCapture shutting down.");

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _statusTimer?.Stop();
        _hotkeys?.Dispose();
        _session?.Dispose();
        _indicator?.Close();
        _tray?.Dispose();
        _singleInstance?.Dispose();
    }
}
