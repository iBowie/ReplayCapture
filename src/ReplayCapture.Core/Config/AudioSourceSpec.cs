using System.Text.RegularExpressions;

namespace ReplayCapture.Core.Config;

public enum AudioSourceKind
{
    /// <summary>Loopback of an audio *render* endpoint — i.e. everything the system plays.</summary>
    RenderLoopback,

    /// <summary>An audio *capture* endpoint — i.e. a microphone.</summary>
    Capture,

    /// <summary>Per-process loopback, matching processes by executable name.</summary>
    Process,
}

/// <summary>
/// One entry in an audio track's source list, parsed from its config string.
/// <para>Grammar:</para>
/// <list type="bullet">
///   <item><c>device:render:default</c> — default playback device, captured as loopback</item>
///   <item><c>device:capture:default</c> — default microphone</item>
///   <item><c>device:render:{endpointId}</c> — a specific endpoint by MMDevice id</item>
///   <item><c>proc:spotify.exe</c> — include this process (and its child tree)</item>
///   <item><c>proc:!spotify.exe</c> — exclude this process</item>
///   <item><c>proc:*</c> — include every process; combine with exclusions for a "everything else" track</item>
/// </list>
/// Executable patterns support <c>*</c> and <c>?</c> wildcards and are matched case-insensitively.
/// </summary>
public sealed record AudioSourceSpec
{
    public required AudioSourceKind Kind { get; init; }

    /// <summary>Endpoint id for device sources, or <c>null</c> to mean the current default endpoint.</summary>
    public string? EndpointId { get; init; }

    /// <summary>Executable-name pattern for <see cref="AudioSourceKind.Process"/> sources.</summary>
    public string? ProcessPattern { get; init; }

    /// <summary>
    /// True when this spec *removes* processes from the track rather than adding them.
    /// Exclusions are evaluated after all inclusions, so ordering in config does not matter.
    /// </summary>
    public bool IsExclusion { get; init; }

    /// <summary>The original config string, kept so round-tripping config never loses formatting.</summary>
    public required string Raw { get; init; }

    private Regex? _compiled;

    public static AudioSourceSpec Parse(string spec)
    {
        if (!TryParse(spec, out var result, out var error))
            throw new FormatException($"Invalid audio source '{spec}': {error}");
        return result;
    }

    public static bool TryParse(string spec, out AudioSourceSpec result, out string? error)
    {
        result = null!;
        error = null;
        var trimmed = spec?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "empty";
            return false;
        }

        var parts = trimmed.Split(':');
        switch (parts[0].ToLowerInvariant())
        {
            case "device":
            {
                if (parts.Length != 3)
                {
                    error = "expected 'device:render|capture:default|<endpointId>'";
                    return false;
                }

                AudioSourceKind kind;
                switch (parts[1].ToLowerInvariant())
                {
                    case "render": kind = AudioSourceKind.RenderLoopback; break;
                    case "capture": kind = AudioSourceKind.Capture; break;
                    default:
                        error = $"unknown device flow '{parts[1]}' (expected 'render' or 'capture')";
                        return false;
                }

                var id = parts[2];
                result = new AudioSourceSpec
                {
                    Kind = kind,
                    EndpointId = id.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : id,
                    Raw = trimmed,
                };
                return true;
            }

            case "proc":
            {
                if (parts.Length != 2 || parts[1].Length == 0)
                {
                    error = "expected 'proc:<exeName>' or 'proc:!<exeName>'";
                    return false;
                }

                var pattern = parts[1];
                var exclude = pattern.StartsWith('!');
                if (exclude) pattern = pattern[1..];

                if (pattern.Length == 0)
                {
                    error = "exclusion needs an executable name";
                    return false;
                }

                result = new AudioSourceSpec
                {
                    Kind = AudioSourceKind.Process,
                    ProcessPattern = pattern,
                    IsExclusion = exclude,
                    Raw = trimmed,
                };
                return true;
            }

            default:
                error = $"unknown source prefix '{parts[0]}' (expected 'device' or 'proc')";
                return false;
        }
    }

    /// <summary>Does the given executable name (e.g. "Spotify.exe") match this process spec?</summary>
    public bool MatchesProcess(string executableName)
    {
        if (Kind != AudioSourceKind.Process) return false;
        _compiled ??= new Regex(
            "^" + Regex.Escape(ProcessPattern!).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return _compiled.IsMatch(executableName);
    }

    public override string ToString() => Raw;
}
