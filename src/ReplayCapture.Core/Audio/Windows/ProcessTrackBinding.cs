using ReplayCapture.Core.Config;
using ReplayCapture.Core.Diagnostics;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Keeps one track's process-loopback sources in step with what is actually running.
/// <para>
/// Rules are evaluated as "any include matches, and no exclude matches". Ordering in config is
/// irrelevant — exclusions always win — so <c>proc:*</c> plus a list of exclusions reads exactly as
/// a user would expect: everything that is not claimed by another track.
/// </para>
/// </summary>
internal sealed class ProcessTrackBinding : IDisposable
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AudioTrackBuffer _track;
    private readonly List<AudioSourceSpec> _includes;
    private readonly List<AudioSourceSpec> _excludes;
    private readonly Dictionary<uint, ProcessLoopbackSource> _attached = [];
    private readonly HashSet<uint> _failed = [];

    private bool _disposed;

    public string TrackName => _track.Name;
    public int AttachedCount => _attached.Count;
    public IEnumerable<string> AttachedNames => _attached.Values.Select(s => s.Name);

    public ProcessTrackBinding(
        AudioTrackBuffer track,
        IEnumerable<AudioSourceSpec> specs,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? processGroups = null)
    {
        _track = track;

        var processSpecs = specs
            .Where(s => s.Kind is AudioSourceKind.Process or AudioSourceKind.Group)
            .SelectMany(s => ExpandGroup(s, processGroups, track.Name))
            .ToList();
        _includes = processSpecs.Where(s => !s.IsExclusion).ToList();
        _excludes = processSpecs.Where(s => s.IsExclusion).ToList();
    }

    /// <summary>
    /// Turns a <c>group:</c> spec into one <c>proc:</c>-equivalent spec per member, preserving
    /// whether the group itself was an inclusion or an exclusion. <c>proc:</c> specs pass through
    /// unchanged.
    /// </summary>
    private static IEnumerable<AudioSourceSpec> ExpandGroup(
        AudioSourceSpec spec, IReadOnlyDictionary<string, IReadOnlyList<string>>? processGroups, string trackName)
    {
        if (spec.Kind != AudioSourceKind.Group) return [spec];

        if (!AudioSourceSpec.TryResolveGroup(spec.GroupName!, processGroups, out var members))
        {
            Log.Warn($"Track '{trackName}': unknown process group '{spec.GroupName}' in '{spec.Raw}'.");
            return [];
        }

        return members.Select(pattern => new AudioSourceSpec
        {
            Kind = AudioSourceKind.Process,
            ProcessPattern = pattern,
            IsExclusion = spec.IsExclusion,
            Raw = spec.Raw,
        });
    }

    public bool HasRules => _includes.Count > 0;

    public bool Matches(AudioSessionInfo session)
    {
        // Never capture our own audio: a save chime would otherwise land on the Game stem.
        if (session.ProcessId == OwnProcessId) return false;

        if (!_includes.Any(spec => spec.MatchesProcess(session.ExecutableName))) return false;
        return !_excludes.Any(spec => spec.MatchesProcess(session.ExecutableName));
    }

    /// <summary>Attaches to newly matching processes and drops sources whose process has gone.</summary>
    public void Refresh(IReadOnlyList<AudioSessionInfo> sessions)
    {
        if (_disposed || !HasRules) return;

        var wanted = sessions.Where(Matches).ToDictionary(s => s.ProcessId);

        foreach (var processId in _attached.Keys.Where(pid => !wanted.ContainsKey(pid)).ToList())
        {
            var source = _attached[processId];
            _attached.Remove(processId);
            source.Dispose();
            Log.Info($"Track '{TrackName}': detached from {source.Name} ({source.FramesCaptured} frames).");
        }

        // A process that failed once should not be retried on every poll.
        _failed.IntersectWith(wanted.Keys);

        foreach (var (processId, session) in wanted)
        {
            if (_attached.ContainsKey(processId) || _failed.Contains(processId)) continue;

            try
            {
                var source = new ProcessLoopbackSource(processId, session.ExecutableName);
                source.SamplesReady += OnSamples;
                source.Start();
                _attached[processId] = source;
                Log.Info($"Track '{TrackName}': attached to {session}.");
            }
            catch (Exception ex)
            {
                // Protected processes and some system services refuse loopback; that must not stop
                // the rest of the track from working.
                _failed.Add(processId);
                Log.Warn($"Track '{TrackName}': could not attach to {session} ({ex.Message}).");
            }
        }
    }

    private void OnSamples(long qpcTicks, ReadOnlyMemory<float> samples) =>
        _track.Accumulate(qpcTicks, samples.Span);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var source in _attached.Values) source.Dispose();
        _attached.Clear();
    }
}
