using ReplayCapture.Core.Audio;
using ReplayCapture.Core.Buffering;
using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Encoders;
using ReplayCapture.Core.Muxing;
using ReplayCapture.Core.Timing;

using D3DTexture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ReplayCapture.Core;

public readonly record struct SaveResult(
    bool Success,
    string Path,
    double DurationSeconds,
    long Bytes,
    string? Error);

/// <summary>
/// The whole always-on buffer: every display recorder plus the audio engine, sharing one D3D device
/// and one clock, and saving them together as a set of aligned <c>.mov</c> files.
/// </summary>
public sealed class ReplaySession : IDisposable
{
    /// <summary>How often the GPU and display topology are checked.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);

    private readonly D3DContext _d3d;
    private readonly List<IDisplayRecorder> _recorders = [];
    private readonly AudioEngine _audio;

    /// <summary>
    /// Guards <see cref="_recorders"/> against concurrent mutation. Reused as the same gate
    /// <see cref="Save"/> already held (rather than a second lock), so a live attach/detach can never
    /// race a save that is mid-snapshot over the list — one simply waits for the other.
    /// </summary>
    private readonly object _saveGate = new();

    private readonly Timer _watchdog;

    /// <summary>
    /// Ring-buffer bytes handed to each display's recorder, fixed once from the displays selected at
    /// construction time. A display attached later (see <see cref="AttachRecorder"/>) reuses this
    /// same figure rather than shrinking every other display's already-fixed-size buffer.
    /// </summary>
    private readonly long _perDisplayBytes;

    private AppConfig _config;
    private int _recoverySignalled;
    private int _healthCheckRunning;
    private bool _disposed;

    /// <summary>
    /// Raised when the session can no longer heal itself and must be rebuilt from scratch — only a
    /// lost GPU, since that invalidates every recorder's GPU resources at once. Fires at most once
    /// per session; the owner is expected to dispose this session and construct a new one.
    /// </summary>
    public event Action<string>? RecoveryRequired;

    /// <summary>
    /// Raised whenever one display's recorder is attached or detached without disturbing any other
    /// display — a capture surface closing, a display leaving or joining the selected set, or a
    /// display exceeding the blank-frame timeout. Purely informational (e.g. for a tray
    /// notification); the session has already healed itself by the time this fires.
    /// </summary>
    public event Action<string>? DisplayTopologyChanged;

    public IReadOnlyList<IDisplayRecorder> Recorders => SnapshotRecorders();
    public AudioEngine Audio => _audio;

    /// <summary>Shared timeline origin for audio and video alike.</summary>
    public long EpochQpc { get; }

    public ReplaySession(AppConfig config)
    {
        _config = config;
        EpochQpc = Clock.Now;

        _d3d = new D3DContext();

        var attached = DisplayEnumerator.Enumerate();
        if (attached.Count == 0) throw new InvalidOperationException("No displays are attached.");

        var selected = SelectDisplays(attached, config).ToList();

        // Divide the cap across the displays actually being captured, not the ones named in config.
        // Config commonly lists none at all (first run means "capture everything attached"), and
        // dividing by that zero meant every display got the *full* cap — so two screens quietly
        // used twice the configured ceiling.
        _perDisplayBytes = (long)config.MaxRingMemoryMegabytes * 1024 * 1024 / selected.Count;

        foreach (var display in selected) _recorders.Add(BuildRecorder(display));

        _audio = new AudioEngine(config, EpochQpc);

        _watchdog = new Timer(_ => CheckHealth(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Builds one display's recorder from the current config. Shared by construction and a live attach.</summary>
    private DisplayRecorder<D3DTexture2D> BuildRecorder(DisplayInfo display)
    {
        var displayConfig = _config.Displays.FirstOrDefault(d =>
                                string.Equals(d.MonitorId, display.MonitorId, StringComparison.OrdinalIgnoreCase))
                            ?? new DisplayConfig { MonitorId = display.MonitorId };

        var framesPerSecond = displayConfig.Fps ?? display.RefreshHz;
        var captureBackend = _config.CaptureBackend;
        var videoEncoderBackend = _config.VideoEncoderBackend;
        var bitrateMbps = displayConfig.BitrateMbps;
        var fixedEncodeSize = ResolveFixedEncodeSize(display, displayConfig);

        return new DisplayRecorder<D3DTexture2D>(
            display, framesPerSecond, _config.BufferSeconds, _perDisplayBytes,
            captureFactory: () => DisplayCaptureSourceFactory.Create(captureBackend, _d3d, display),
            encoderFactory: (width, height) =>
                VideoEncoderFactory.Create(videoEncoderBackend, _d3d, width, height, framesPerSecond, bitrateMbps),
            fixedEncodeSize: fixedEncodeSize);
    }

    /// <summary>
    /// Watches for what the pipeline cannot heal per-display: the GPU going away (driver update,
    /// TDR, hardware reset), which invalidates every recorder's GPU resources at once and forces a
    /// full rebuild. Everything else — a capture surface closing, a display leaving or joining the
    /// selected set, or a display exceeding the configured blank-frame timeout — detaches or attaches
    /// just that one display's recorder, so every other display's buffer is left alone.
    /// </summary>
    private void CheckHealth()
    {
        if (_disposed) return;

        // Attach/detach can now take longer than the old pure set-comparison (building a recorder
        // opens a capture source and encoder), so guard against a slow tick still running when the
        // next one fires rather than letting them interleave.
        if (Interlocked.CompareExchange(ref _healthCheckRunning, 1, 0) != 0) return;

        try
        {
            if (_d3d.IsDeviceLost)
            {
                SignalRecovery("the GPU was reset or its driver was updated");
                return;
            }

            // Windows.Graphics.Capture tears an item down rather than reviving it — seen when a
            // display is unplugged, powered off, or the system sleeps and resumes. A persistently
            // black display (see HasExceededBlankTimeout) gets the same treatment: neither can be
            // healed in place, but neither requires touching any other display either.
            var now = Clock.Now;
            var timeoutSeconds = _config.BlankDisplayTimeoutSeconds;

            foreach (var recorder in SnapshotRecorders())
            {
                if (recorder.IsCaptureClosed)
                    DetachRecorder(recorder, "its capture surface closed");
                else if (recorder.HasExceededBlankTimeout(now, timeoutSeconds))
                    DetachRecorder(recorder, $"it showed nothing but black for over {timeoutSeconds}s");
            }

            // Reconcile the selected set against whoever is left recording: anything selected but not
            // currently recorded (a reconnect, or a genuinely new display) gets attached; anything
            // recorded but no longer selected (removed from Windows, or disabled/dropped from config)
            // gets detached.
            var attached = DisplayEnumerator.Enumerate();
            var selected = SelectDisplays(attached, _config).ToList();
            var currentMonitorIds = SnapshotRecorders().Select(r => r.Display.MonitorId).ToList();

            var (toDetachIds, toAttach) = Reconcile(selected, currentMonitorIds);

            foreach (var monitorId in toDetachIds)
            {
                var recorder = SnapshotRecorders().FirstOrDefault(r =>
                    string.Equals(r.Display.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
                if (recorder is not null) DetachRecorder(recorder, "it is no longer attached or selected by config");
            }

            foreach (var display in toAttach) AttachRecorder(display);
        }
        catch (Exception ex)
        {
            Log.Error("Session health check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _healthCheckRunning, 0);
        }
    }

    /// <summary>
    /// Pure reconciliation of the selected display set against whichever monitor ids are currently
    /// recorded: what should be detached (recorded but no longer selected) and what should be
    /// attached (selected but not yet recorded). Kept separate from <see cref="CheckHealth"/> so it
    /// can be unit-tested without a real <see cref="DisplayEnumerator"/> or GPU.
    /// </summary>
    internal static (IReadOnlyList<string> ToDetachMonitorIds, IReadOnlyList<DisplayInfo> ToAttach) Reconcile(
        IReadOnlyList<DisplayInfo> selected, IReadOnlyList<string> currentMonitorIds)
    {
        var selectedIds = selected.Select(d => d.MonitorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentIds = currentMonitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toDetach = currentMonitorIds.Where(id => !selectedIds.Contains(id)).ToList();
        var toAttach = selected.Where(d => !currentIds.Contains(d.MonitorId)).ToList();

        return (toDetach, toAttach);
    }

    /// <summary>
    /// Adds a recorder for a newly-selected display without touching any other display's buffer.
    /// Building the recorder (opens a capture source and encoder) happens outside the lock since it
    /// can block; only appending to the recorder list is guarded, so this can never race a
    /// <see cref="Save"/> that is mid-snapshot.
    /// </summary>
    private void AttachRecorder(DisplayInfo display)
    {
        IDisplayRecorder recorder;
        try
        {
            recorder = BuildRecorder(display);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not attach {display.DeviceName}", ex);
            return;
        }

        recorder.Start();
        lock (_saveGate) _recorders.Add(recorder);

        Log.Info($"Attached {display.DeviceName}.");
        DisplayTopologyChanged?.Invoke($"{display.Label} started capturing.");
    }

    /// <summary>
    /// Removes and disposes one display's recorder without touching any other display's buffer.
    /// Disposal (which can block — tearing down capture and the encoder) happens outside the lock,
    /// after the recorder has already been removed from the list so nothing else can observe or
    /// snapshot it mid-teardown.
    /// </summary>
    private void DetachRecorder(IDisplayRecorder recorder, string reason)
    {
        lock (_saveGate) _recorders.Remove(recorder);

        Log.Warn($"Detaching {recorder.Display.DeviceName}: {reason}.");
        DisplayTopologyChanged?.Invoke($"{recorder.Display.Label} stopped capturing: {reason}.");

        try { recorder.Dispose(); }
        catch (Exception ex) { Log.Error($"Failed to dispose the recorder for {recorder.Display.DeviceName}", ex); }
    }

    private List<IDisplayRecorder> SnapshotRecorders()
    {
        lock (_saveGate) return [.._recorders];
    }

    private void SignalRecovery(string reason)
    {
        // Fire once: the owner tears this session down in response, and a second notification would
        // race that teardown.
        if (Interlocked.Exchange(ref _recoverySignalled, 1) != 0) return;

        Log.Warn($"Capture must be rebuilt: {reason}.");
        RecoveryRequired?.Invoke(reason);
    }

    /// <summary>
    /// Displays named in config win; an empty config means "capture everything attached", which is
    /// what a first run should do rather than capturing nothing.
    /// </summary>
    private static IEnumerable<DisplayInfo> SelectDisplays(IReadOnlyList<DisplayInfo> attached, AppConfig config)
    {
        if (config.Displays.Count == 0) return attached;

        var enabled = config.Displays
            .Where(d => d.Enabled)
            .Select(d => d.MonitorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = attached.Where(d => enabled.Contains(d.MonitorId)).ToList();
        if (selected.Count > 0) return selected;

        Log.Warn("No configured display is currently attached; falling back to all attached displays.");
        return attached;
    }

    /// <summary>
    /// A user-configured resolution needs both <see cref="DisplayConfig.CaptureWidth"/> and
    /// <see cref="DisplayConfig.CaptureHeight"/> — a lone value can't describe a target size, so it
    /// is ignored (with a warning) in favour of auto-detecting from the display's native size.
    /// </summary>
    internal static FrameSize? ResolveFixedEncodeSize(DisplayInfo display, DisplayConfig displayConfig)
    {
        if (displayConfig.CaptureWidth is { } width && displayConfig.CaptureHeight is { } height)
            return new FrameSize(width, height);

        if (displayConfig.CaptureWidth is not null || displayConfig.CaptureHeight is not null)
        {
            Log.Warn($"{display.DeviceName}: CaptureWidth and CaptureHeight must both be set to override " +
                     "the capture resolution; ignoring the partial value and auto-detecting instead.");
        }

        return null;
    }

    public void Start()
    {
        _audio.Start();
        foreach (var recorder in _recorders) recorder.Start();
        _watchdog.Change(WatchdogInterval, WatchdogInterval);

        Log.Info($"Session armed: {_recorders.Count} display(s), {_audio.Tracks.Count} audio track(s).");
    }

    /// <summary>Total ring-buffer memory across video and audio, for the UI and the memory cap.</summary>
    public long BufferedBytes => SnapshotRecorders().Sum(r => r.BufferedBytes) + _audio.TotalBytes;

    /// <summary>
    /// Writes the buffered window: one <c>.mov</c> per display, each carrying every audio track.
    /// <para>
    /// All files share a single origin, so dropping them onto one timeline lines them up with no
    /// manual nudging: the earliest keyframe across displays becomes t0, each display's video is
    /// offset by however far its own first keyframe sits after that, and audio is read from the
    /// same t0.
    /// </para>
    /// </summary>
    public IReadOnlyList<SaveResult> Save()
    {
        lock (_saveGate)
        {
            var now = Clock.Now;
            var seconds = _config.BufferSeconds;

            var snapshots = _recorders
                .Select(recorder => (Recorder: recorder, Packets: recorder.Snapshot(now, seconds)))
                .Where(entry => entry.Packets.Count > 0)
                .ToList();

            if (snapshots.Count == 0)
            {
                Log.Warn("Save requested but no display has a keyframe yet.");
                return [new SaveResult(false, "", 0, 0, "the buffer has no keyframe yet")];
            }

            var originQpc = snapshots.Min(entry => entry.Packets[0].QpcTicks);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");

            var results = new List<SaveResult>();
            foreach (var (recorder, packets) in snapshots)
            {
                try
                {
                    results.Add(WriteOne(timestamp, recorder, packets, originQpc));
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to write the clip for {recorder.Display.DeviceName}", ex);
                    results.Add(new SaveResult(false, _config.OutputDirectory, 0, 0, ex.Message));
                }
            }

            return results;
        }
    }

    private SaveResult WriteOne(
        string timestamp, IDisplayRecorder recorder, IReadOnlyList<ClipPacket> packets, long originQpc)
    {
        var screenIndex = ScreenIndexOf(recorder.Display.DeviceName);
        var path = Path.Combine(_config.OutputDirectory, $"{timestamp}-{screenIndex}.mov");

        using var writer = new MovWriter(path);

        var video = writer.AddVideoStream(
            recorder.Width, recorder.Height, recorder.FramesPerSecond, recorder.ExtraData);
        var audioStreams = _audio.AddStreamsTo(writer);

        writer.WriteHeader();

        // Offset this display's video by however far its first keyframe lands after the shared
        // origin, so displays that started a fraction of a GOP apart still align.
        var startOffsetFrames = (long)Math.Round(
            Clock.ToSeconds(packets[0].QpcTicks - originQpc) * recorder.FramesPerSecond);

        var firstFrameIndex = packets[0].FrameIndex;
        foreach (var packet in packets)
        {
            writer.WritePacket(
                video,
                packet.Data,
                timestamp: startOffsetFrames + packet.FrameIndex - firstFrameIndex,
                duration: 1,
                packet.IsKeyframe);
        }

        var duration = (double)(startOffsetFrames + packets.Count) / recorder.FramesPerSecond;
        _audio.WriteInto(writer, audioStreams, originQpc, duration);

        writer.Finish();

        var bytes = new FileInfo(path).Length;
        Log.Info($"Saved {path}: {packets.Count} frames, {duration:0.00}s, " +
                 $"{_audio.Tracks.Count} audio track(s), {bytes / (1024 * 1024)} MB.");

        return new SaveResult(true, path, duration, bytes, null);
    }

    /// <summary>Windows' own display number, e.g. <c>\\.\DISPLAY1</c> -&gt; <c>1</c>.</summary>
    internal static string ScreenIndexOf(string deviceName)
    {
        var digits = new string(deviceName.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : deviceName.Replace(@"\\.\", "").ToLowerInvariant();
    }

    public void UpdateConfig(AppConfig config) => _config = config;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watchdog.Dispose();

        foreach (var recorder in SnapshotRecorders()) recorder.Dispose();
        _audio.Dispose();
        _d3d.Dispose();
    }
}
