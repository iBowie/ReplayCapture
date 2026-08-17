using ReplayCapture.Core.Config;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Answers "which tracks would this process land on?" against a set of tracks and process groups,
/// without needing a live <see cref="AudioSessionMonitor"/>/<c>ProcessTrackBinding</c> pair. Used to
/// preview routing for a process that may not even be running, or for tracks whose source text is
/// still being edited and has not been saved yet — so malformed source lines are skipped rather than
/// thrown on.
/// </summary>
public static class ProcessTrackRouter
{
    /// <summary>The names of every enabled track whose rules currently claim this process.</summary>
    public static IReadOnlyList<string> ResolveTrackNames(
        string executableName,
        IEnumerable<AudioTrackConfig> tracks,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? processGroups)
    {
        var matches = new List<string>();
        foreach (var track in tracks)
        {
            if (!track.Enabled) continue;

            var specs = ExpandSpecs(track, processGroups);
            var includes = specs.Where(s => !s.IsExclusion);
            var excludes = specs.Where(s => s.IsExclusion);

            if (includes.Any(s => s.MatchesProcess(executableName)) &&
                !excludes.Any(s => s.MatchesProcess(executableName)))
            {
                matches.Add(track.Name);
            }
        }

        return matches;
    }

    /// <summary>
    /// Expands a track's <c>proc:</c> specs as-is and <c>group:</c> specs into their member
    /// <c>proc:</c> specs. Source lines that fail to parse and <c>group:</c> specs naming an unknown
    /// group are dropped silently rather than throwing, since callers preview in-progress config.
    /// </summary>
    public static IReadOnlyList<AudioSourceSpec> ExpandSpecs(
        AudioTrackConfig track, IReadOnlyDictionary<string, IReadOnlyList<string>>? processGroups)
    {
        var result = new List<AudioSourceSpec>();

        foreach (var source in track.Sources)
        {
            if (!AudioSourceSpec.TryParse(source, out var spec, out _)) continue;

            if (spec.Kind == AudioSourceKind.Process)
            {
                result.Add(spec);
                continue;
            }

            if (spec.Kind != AudioSourceKind.Group) continue;
            if (!AudioSourceSpec.TryResolveGroup(spec.GroupName!, processGroups, out var members)) continue;

            result.AddRange(members.Select(pattern => new AudioSourceSpec
            {
                Kind = AudioSourceKind.Process,
                ProcessPattern = pattern,
                IsExclusion = spec.IsExclusion,
                Raw = spec.Raw,
            }));
        }

        return result;
    }
}
