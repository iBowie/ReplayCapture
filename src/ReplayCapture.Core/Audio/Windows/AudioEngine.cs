using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Muxing;
using ReplayCapture.Core.Timing;
using Windows.Win32.Media.Audio;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Owns every audio track and every capture source, and keeps them all on one timeline.
/// <para>
/// Sources are opened once and fanned out: "Desktop + Mic" and "Desktop" both name the default
/// playback endpoint, but that endpoint is captured a single time and its samples accumulated into
/// both tracks. Opening the same loopback twice would double the cost for no benefit.
/// </para>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>How often silent tracks are nudged forward so "now" stays well defined.</summary>
    private static readonly TimeSpan SilenceAdvanceInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How often the running-process set is re-evaluated. Two seconds is well inside the buffer
    /// window, so a game that launches is fully captured long before anyone presses save.
    /// </summary>
    private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromSeconds(2);

    private readonly List<AudioTrackBuffer> _tracks = [];
    private readonly List<IAudioSource> _sources = [];
    private readonly List<ProcessTrackBinding> _bindings = [];

    // Keyed so one physical endpoint backs however many tracks reference it, and so a "default
    // device" source can be found and reopened in place when the default changes underneath it.
    private readonly Dictionary<string, IAudioSource> _sourcesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioSourceSpec> _sourceSpecsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AudioTrackBuffer>> _sourceTargetsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sourceLock = new();

    private readonly Timer _silenceAdvance;
    private readonly Timer _processPoll;
    private readonly DefaultDeviceWatcher _deviceWatcher;
    private readonly int _windowSeconds;
    private bool _disposed;

    /// <summary>Shared timeline origin. Every track and every video stream is measured from here.</summary>
    public long EpochQpc { get; }

    public IReadOnlyList<AudioTrackBuffer> Tracks => _tracks;

    public long TotalBytes => _tracks.Sum(t => t.Bytes);

    public AudioEngine(AppConfig config, long epochQpc)
    {
        EpochQpc = epochQpc;
        _windowSeconds = config.BufferSeconds;

        BuildTracks(config);

        _silenceAdvance = new Timer(_ => AdvanceSilence(), null,
            SilenceAdvanceInterval, SilenceAdvanceInterval);

        _processPoll = new Timer(_ => RefreshProcessBindings(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _deviceWatcher = new DefaultDeviceWatcher();
        _deviceWatcher.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    private void BuildTracks(AppConfig config)
    {
        foreach (var trackConfig in config.AudioTracks.Where(t => t.Enabled))
        {
            var track = new AudioTrackBuffer(trackConfig.Name, EpochQpc, _windowSeconds, trackConfig.Gain);
            _tracks.Add(track);

            var specs = ParseSources(trackConfig).ToList();

            // Process rules are handled as a set, not one at a time: a track's includes and
            // excludes only mean anything when evaluated together.
            var binding = new ProcessTrackBinding(track, specs, config.ProcessGroups);
            if (binding.HasRules) _bindings.Add(binding);

            foreach (var spec in specs.Where(s => s.Kind is not AudioSourceKind.Process and not AudioSourceKind.Group))
            {
                var key = $"{spec.Kind}:{spec.EndpointId ?? "default"}";
                if (!_sourcesByKey.TryGetValue(key, out var source))
                {
                    source = TryOpenSource(spec, trackConfig.Name);
                    if (source is null) continue;
                    _sourcesByKey[key] = source;
                    _sourceSpecsByKey[key] = spec;
                    _sourceTargetsByKey[key] = [];
                    _sources.Add(source);
                }

                _sourceTargetsByKey[key].Add(track);
                Subscribe(source, track);
            }
        }

        Log.Info($"Audio engine: {_tracks.Count} track(s), {_sources.Count} endpoint source(s), " +
                 $"{_bindings.Count} process-rule track(s), {TotalBytes / (1024 * 1024)} MB of ring buffers.");
    }

    private static void Subscribe(IAudioSource source, AudioTrackBuffer target) =>
        source.SamplesReady += (qpc, samples) => target.Accumulate(qpc, samples.Span);

    /// <summary>
    /// Reopens whichever cached source was resolved against "the default device" for the role that
    /// just changed, so the tracks it feeds pick up the new default instead of quietly listening to
    /// an endpoint that stopped receiving anything. See <see cref="DefaultDeviceWatcher"/>.
    /// </summary>
    private void OnDefaultDeviceChanged(EDataFlow flow)
    {
        var key = flow == EDataFlow.eRender ? "RenderLoopback:default" : "Capture:default";

        lock (_sourceLock)
        {
            if (_disposed) return;
            if (!_sourcesByKey.TryGetValue(key, out var oldSource)) return;

            var spec = _sourceSpecsByKey[key];
            var targets = _sourceTargetsByKey[key];

            var newSource = TryOpenSource(spec, "default device change");
            if (newSource is null)
            {
                Log.Warn($"Could not reopen '{key}' after a default device change; it keeps listening to the previous endpoint.");
                return;
            }

            foreach (var target in targets) Subscribe(newSource, target);

            try
            {
                newSource.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not start reopened audio source '{newSource.Name}'", ex);
            }

            _sourcesByKey[key] = newSource;
            var index = _sources.IndexOf(oldSource);
            if (index >= 0) _sources[index] = newSource;

            oldSource.Dispose();

            Log.Info($"Default {(flow == EDataFlow.eRender ? "playback" : "recording")} device changed; '{key}' reopened against it.");
        }
    }

    private static IEnumerable<AudioSourceSpec> ParseSources(AudioTrackConfig track)
    {
        foreach (var raw in track.Sources)
        {
            if (AudioSourceSpec.TryParse(raw, out var spec, out var error)) yield return spec;
            else Log.Warn($"Track '{track.Name}': ignoring source '{raw}' ({error}).");
        }
    }

    private static IAudioSource? TryOpenSource(AudioSourceSpec spec, string trackName)
    {
        try
        {
            return spec switch
            {
                { Kind: AudioSourceKind.RenderLoopback, EndpointId: null } =>
                    AudioDeviceEnumerator.CreateDefaultRenderLoopback(),
                { Kind: AudioSourceKind.Capture, EndpointId: null } =>
                    AudioDeviceEnumerator.CreateDefaultCapture(),
                { Kind: AudioSourceKind.RenderLoopback } =>
                    AudioDeviceEnumerator.CreateForEndpoint(spec.EndpointId!, loopback: true, spec.Raw),
                { Kind: AudioSourceKind.Capture } =>
                    AudioDeviceEnumerator.CreateForEndpoint(spec.EndpointId!, loopback: false, spec.Raw),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // A missing microphone must never stop the rest of the buffer from running.
            Log.Warn($"Track '{trackName}': could not open '{spec.Raw}' ({ex.Message}). Track continues silent.");
            return null;
        }
    }

    public void Start()
    {
        foreach (var source in _sources)
        {
            try
            {
                source.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not start audio source '{source.Name}'", ex);
            }
        }

        // Attach immediately to whatever is already making sound, then keep watching.
        RefreshProcessBindings();
        _processPoll.Change(ProcessPollInterval, ProcessPollInterval);
    }

    /// <summary>Re-evaluates every process rule against the processes currently holding audio sessions.</summary>
    private void RefreshProcessBindings()
    {
        if (_disposed || _bindings.Count == 0) return;

        try
        {
            var sessions = AudioSessionMonitor.ListActiveSessions();
            foreach (var binding in _bindings) binding.Refresh(sessions);
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing process audio bindings failed", ex);
        }
    }

    /// <summary>Per-track summary of which processes are currently attached, for the UI and probe.</summary>
    public IEnumerable<(string Track, IEnumerable<string> Processes)> DescribeProcessBindings() =>
        _bindings.Select(b => (b.TrackName, b.AttachedNames));

    /// <summary>
    /// Moves every track's silence frontier up to now. Without this a track whose sources are all
    /// quiet would have no defined present, and a save would read stale ring contents.
    /// </summary>
    private void AdvanceSilence()
    {
        if (_disposed) return;

        // Stay slightly behind the clock so a source's samples are never overtaken by the frontier
        // before they arrive.
        var frontier = Clock.Now - Clock.FromMilliseconds(200);
        foreach (var track in _tracks) track.AdvanceTo(frontier);
    }

    /// <summary>Declares one PCM stream per track on the writer, in configuration order.</summary>
    public int[] AddStreamsTo(MovWriter writer) =>
        [.. _tracks.Select(track => writer.AddPcmAudioStream(track.Name, AudioFormat.SampleRate, AudioFormat.Channels))];

    /// <summary>
    /// Writes <paramref name="durationSeconds"/> of every track starting at
    /// <paramref name="startQpc"/> — the same origin the video clip uses, which is what makes the
    /// tracks land in sync.
    /// </summary>
    public void WriteInto(MovWriter writer, int[] streamIndices, long startQpc, double durationSeconds)
    {
        const int chunkFrames = AudioFormat.SampleRate / 10;   // 100 ms per packet

        var totalFrames = (int)(durationSeconds * AudioFormat.SampleRate);
        var buffer = new short[chunkFrames * AudioFormat.Channels];
        var bytes = new byte[buffer.Length * sizeof(short)];

        for (var i = 0; i < _tracks.Count; i++)
        {
            var track = _tracks[i];
            var stream = streamIndices[i];

            for (var offset = 0; offset < totalFrames; offset += chunkFrames)
            {
                var frames = Math.Min(chunkFrames, totalFrames - offset);
                var chunkQpc = startQpc + AudioFormat.FramesToTicks(offset);

                track.ReadPcm16(chunkQpc, frames, buffer);
                Buffer.BlockCopy(buffer, 0, bytes, 0, frames * AudioFormat.BytesPerOutputFrame);

                writer.WritePacket(
                    stream,
                    bytes.AsSpan(0, frames * AudioFormat.BytesPerOutputFrame),
                    timestamp: offset,
                    duration: frames,
                    isKeyframe: true);   // every PCM packet is independently decodable
            }
        }
    }

    public void Dispose()
    {
        lock (_sourceLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _deviceWatcher.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _deviceWatcher.Dispose();
        _processPoll.Dispose();
        _silenceAdvance.Dispose();
        foreach (var binding in _bindings) binding.Dispose();
        foreach (var source in _sources) source.Dispose();
        foreach (var track in _tracks) track.LogStatistics();
    }
}
