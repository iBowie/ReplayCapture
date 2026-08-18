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
    private readonly object _saveGate = new();
    private readonly HashSet<string> _capturedDeviceNames;
    private readonly Timer _watchdog;

    private AppConfig _config;
    private int _recoverySignalled;
    private bool _disposed;

    /// <summary>
    /// Raised when the session can no longer heal itself and must be rebuilt from scratch — a lost
    /// GPU or a change in which displays exist. Fires at most once per session; the owner is
    /// expected to dispose this session and construct a new one.
    /// </summary>
    public event Action<string>? RecoveryRequired;

    public IReadOnlyList<IDisplayRecorder> Recorders => _recorders;
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
        _capturedDeviceNames = [.. selected.Select(d => d.DeviceName)];

        // Divide the cap across the displays actually being captured, not the ones named in config.
        // Config commonly lists none at all (first run means "capture everything attached"), and
        // dividing by that zero meant every display got the *full* cap — so two screens quietly
        // used twice the configured ceiling.
        var perDisplayBytes = (long)config.MaxRingMemoryMegabytes * 1024 * 1024 / selected.Count;

        foreach (var display in selected)
        {
            var displayConfig = config.Displays.FirstOrDefault(d =>
                                    string.Equals(d.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase))
                                ?? new DisplayConfig { DeviceName = display.DeviceName };

            var framesPerSecond = displayConfig.Fps ?? display.RefreshHz;
            var captureBackend = config.CaptureBackend;
            var videoEncoderBackend = config.VideoEncoderBackend;
            var bitrateMbps = displayConfig.BitrateMbps;
            var fixedEncodeSize = ResolveFixedEncodeSize(display, displayConfig);

            _recorders.Add(new DisplayRecorder<D3DTexture2D>(
                display, framesPerSecond, config.BufferSeconds, perDisplayBytes,
                captureFactory: () => DisplayCaptureSourceFactory.Create(captureBackend, _d3d, display),
                encoderFactory: (width, height) =>
                    VideoEncoderFactory.Create(videoEncoderBackend, _d3d, width, height, framesPerSecond, bitrateMbps),
                fixedEncodeSize: fixedEncodeSize));
        }

        _audio = new AudioEngine(config, EpochQpc);

        _watchdog = new Timer(_ => CheckHealth(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Watches for the two failures the pipeline cannot recover from in place: the GPU going away
    /// (driver update, TDR, hardware reset) and the set of displays changing.
    /// </summary>
    private void CheckHealth()
    {
        if (_disposed) return;

        try
        {
            if (_d3d.IsDeviceLost)
            {
                SignalRecovery("the GPU was reset or its driver was updated");
                return;
            }

            var closed = _recorders.Where(r => r.IsCaptureClosed).ToList();
            if (closed.Count > 0)
            {
                // Windows.Graphics.Capture tears an item down rather than reviving it — seen when a
                // display is unplugged, powered off, or the system sleeps and resumes. There is no
                // in-place recreation for a closed item, so the whole session must be rebuilt.
                SignalRecovery($"the capture surface closed for {Describe(closed.Select(r => r.Display.DeviceName).ToList())}");
                return;
            }

            // A resolution change is handled inside the recorder; only displays appearing or
            // disappearing need the session rebuilt, because that changes how many files a save
            // produces and how the memory cap is divided.
            var attached = DisplayEnumerator.Enumerate()
                .Select(d => d.DeviceName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var expected = SelectDisplays(DisplayEnumerator.Enumerate(), _config)
                .Select(d => d.DeviceName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!expected.SetEquals(_capturedDeviceNames))
            {
                var added = expected.Except(_capturedDeviceNames).ToList();
                var removed = _capturedDeviceNames.Except(attached).ToList();
                SignalRecovery(
                    $"the displays changed (added: {Describe(added)}, removed: {Describe(removed)})");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Session health check failed", ex);
        }

        static string Describe(List<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);
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
            .Select(d => d.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = attached.Where(d => enabled.Contains(d.DeviceName)).ToList();
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
    public long BufferedBytes => _recorders.Sum(r => r.BufferedBytes) + _audio.TotalBytes;

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
        foreach (var recorder in _recorders) recorder.Dispose();
        _audio.Dispose();
        _d3d.Dispose();
    }
}
