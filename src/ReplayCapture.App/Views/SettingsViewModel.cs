using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReplayCapture.Core.Audio;
using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Config;
using ReplayCapture.Core.Input;

namespace ReplayCapture.App.Views;

public sealed partial class DisplayRowViewModel : ObservableObject
{
    public required string MonitorId { get; init; }

    /// <summary>Persisted across saves so a disconnected display still shows something recognizable.</summary>
    public required string Label { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>False for a display that is in config but not currently attached.</summary>
    public bool IsAvailable { get; init; } = true;

    public string Description => Label
        + (IsPrimary ? "  •  primary" : "")
        + (IsAvailable ? "" : "  •  not connected");

    [ObservableProperty] private bool _enabled = true;

    /// <summary>Empty or "auto" follows the display's own refresh rate.</summary>
    [ObservableProperty] private string _fps = "auto";

    [ObservableProperty] private int _bitrateMbps = 40;

    /// <summary>Blank means "native" for both — same freeform convention as <see cref="Fps"/>.</summary>
    [ObservableProperty] private string _captureWidth = "";
    [ObservableProperty] private string _captureHeight = "";

    /// <summary>True when exactly one of <see cref="CaptureWidth"/>/<see cref="CaptureHeight"/> is set.</summary>
    public bool HasPartialResolutionOverride =>
        string.IsNullOrWhiteSpace(CaptureWidth) != string.IsNullOrWhiteSpace(CaptureHeight);

    public DisplayConfig ToConfig() => new()
    {
        MonitorId = MonitorId,
        Enabled = Enabled,
        Fps = int.TryParse(Fps, out var parsed) && parsed > 0 ? parsed : null,
        BitrateMbps = Math.Clamp(BitrateMbps, 1, 500),
        CaptureWidth = int.TryParse(CaptureWidth, out var width) && width > 0 ? width : null,
        CaptureHeight = int.TryParse(CaptureHeight, out var height) && height > 0 ? height : null,
        Label = Label,
    };
}

public sealed partial class TrackRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "New track";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private double _gain = 1.0;

    /// <summary>
    /// One source per line. A plain text box beats a nested editable grid here — the grammar is
    /// short, and copy/pasting a rule between tracks is something people actually do.
    /// </summary>
    [ObservableProperty] private string _sourcesText = "";

    public static TrackRowViewModel From(AudioTrackConfig config) => new()
    {
        Name = config.Name,
        Enabled = config.Enabled,
        Gain = config.Gain,
        SourcesText = string.Join(Environment.NewLine, config.Sources),
    };

    public AudioTrackConfig ToConfig() => new()
    {
        Name = Name,
        Enabled = Enabled,
        Gain = Gain,
        Sources = SourcesText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
    };

    /// <summary>Human-readable problems with this track's sources, or empty when it is valid.</summary>
    public IEnumerable<string> Validate()
    {
        foreach (var source in ToConfig().Sources)
        {
            if (!AudioSourceSpec.TryParse(source, out _, out var error))
                yield return $"{Name}: '{source}' — {error}";
        }
    }
}

public sealed partial class GroupRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "new-group";

    /// <summary>One executable pattern per line, same free-text convention as track sources.</summary>
    [ObservableProperty] private string _membersText = "";

    public static GroupRowViewModel From(string name, IReadOnlyList<string> members) => new()
    {
        Name = name,
        MembersText = string.Join(Environment.NewLine, members),
    };

    public IReadOnlyList<string> Members => MembersText
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}

/// <summary>
/// One entry in the running-processes picker: the raw session plus a preview of which track(s) it
/// currently resolves to, given the in-progress (possibly unsaved) track and group edits.
/// </summary>
public sealed record ProcessRouteRow(AudioSessionInfo Session, string RouteDescription)
{
    public string ExecutableName => Session.ExecutableName;

    public override string ToString() => $"{Session}  →  {RouteDescription}";
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _original;

    [ObservableProperty] private int _bufferSeconds;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _hotkey = "";
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _playSoundOnSave;
    [ObservableProperty] private bool _showOverlayIndicator;
    [ObservableProperty] private OverlayCorner _overlayCorner;
    [ObservableProperty] private int _maxRingMemoryMegabytes;
    [ObservableProperty] private int _blankDisplayTimeoutSeconds;
    [ObservableProperty] private CaptureBackend _captureBackend;
    [ObservableProperty] private VideoEncoderBackend _videoEncoderBackend;
    [ObservableProperty] private string? _validationError;

    public ObservableCollection<DisplayRowViewModel> Displays { get; } = [];
    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];
    public ObservableCollection<GroupRowViewModel> Groups { get; } = [];
    public ObservableCollection<ProcessRouteRow> RunningAudioProcesses { get; } = [];

    public IReadOnlyList<OverlayCorner> OverlayCorners { get; } = Enum.GetValues<OverlayCorner>();
    public IReadOnlyList<CaptureBackend> CaptureBackends { get; } = Enum.GetValues<CaptureBackend>();
    public IReadOnlyList<VideoEncoderBackend> VideoEncoderBackends { get; } = Enum.GetValues<VideoEncoderBackend>();

    [ObservableProperty] private TrackRowViewModel? _selectedTrack;
    [ObservableProperty] private GroupRowViewModel? _selectedGroup;
    [ObservableProperty] private ProcessRouteRow? _selectedProcess;

    /// <summary>Projected memory footprint, so the cost of a longer buffer is visible before saving.</summary>
    public string EstimatedMemory
    {
        get
        {
            var video = Displays.Where(d => d.Enabled)
                .Sum(d => (double)d.BitrateMbps * 1_000_000 / 8 * BufferSeconds);
            var audio = Tracks.Count(t => t.Enabled)
                        * (double)AudioFormat.SampleRate * AudioFormat.Channels * sizeof(float) * (BufferSeconds + 2);

            return $"≈ {(video + audio) / (1024 * 1024):N0} MB "
                   + $"({video / (1024 * 1024):N0} MB video + {audio / (1024 * 1024):N0} MB audio)";
        }
    }

    public SettingsViewModel(AppConfig config)
    {
        _original = config;

        _bufferSeconds = config.BufferSeconds;
        _outputDirectory = config.OutputDirectory;
        _hotkey = config.Hotkey;
        _startWithWindows = config.StartWithWindows;
        _playSoundOnSave = config.PlaySoundOnSave;
        _showOverlayIndicator = config.ShowOverlayIndicator;
        _overlayCorner = config.OverlayCorner;
        _maxRingMemoryMegabytes = config.MaxRingMemoryMegabytes;
        _blankDisplayTimeoutSeconds = config.BlankDisplayTimeoutSeconds;
        _captureBackend = config.CaptureBackend;
        _videoEncoderBackend = config.VideoEncoderBackend;

        LoadDisplays(config);
        foreach (var track in config.AudioTracks) Tracks.Add(TrackRowViewModel.From(track));
        SelectedTrack = Tracks.FirstOrDefault();

        foreach (var (name, members) in config.ProcessGroups) Groups.Add(GroupRowViewModel.From(name, members));
        SelectedGroup = Groups.FirstOrDefault();

        RefreshProcesses();
    }

    private void LoadDisplays(AppConfig config)
    {
        var attachedMonitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var display in DisplayEnumerator.Enumerate())
        {
            attachedMonitorIds.Add(display.MonitorId);

            var existing = config.Displays.FirstOrDefault(d =>
                string.Equals(d.MonitorId, display.MonitorId, StringComparison.OrdinalIgnoreCase));

            Displays.Add(new DisplayRowViewModel
            {
                MonitorId = display.MonitorId,
                Label = display.Label,
                IsPrimary = display.IsPrimary,
                IsAvailable = true,
                // No config yet means first run, and capturing everything is the useful default.
                Enabled = existing?.Enabled ?? true,
                Fps = existing?.Fps?.ToString() ?? "auto",
                BitrateMbps = existing?.BitrateMbps ?? 40,
                CaptureWidth = existing?.CaptureWidth?.ToString() ?? "",
                CaptureHeight = existing?.CaptureHeight?.ToString() ?? "",
            });
        }

        // A display can be configured but temporarily disconnected (unplugged, docked laptop closed,
        // etc.) — still list it so its settings aren't silently dropped from view, and so it isn't
        // silently dropped from the saved config either (Displays is rebuilt wholesale from this list).
        foreach (var display in config.Displays)
        {
            if (string.IsNullOrEmpty(display.MonitorId) || attachedMonitorIds.Contains(display.MonitorId))
                continue;

            Displays.Add(new DisplayRowViewModel
            {
                MonitorId = display.MonitorId,
                Label = string.IsNullOrWhiteSpace(display.Label) ? display.MonitorId : display.Label,
                IsAvailable = false,
                Enabled = display.Enabled,
                Fps = display.Fps?.ToString() ?? "auto",
                BitrateMbps = display.BitrateMbps,
                CaptureWidth = display.CaptureWidth?.ToString() ?? "",
                CaptureHeight = display.CaptureHeight?.ToString() ?? "",
            });
        }
    }

    partial void OnBufferSecondsChanged(int value) => OnPropertyChanged(nameof(EstimatedMemory));

    /// <summary>
    /// Re-lists processes currently holding an audio session and, for each, previews which track(s)
    /// it would land on given the tracks and groups as currently edited (not yet saved). Click this
    /// again after editing a track's sources to see the effect.
    /// </summary>
    [RelayCommand]
    private void RefreshProcesses()
    {
        var previousSelection = SelectedProcess?.ExecutableName;

        var tracks = Tracks.Select(t => t.ToConfig()).ToList();
        var groups = Groups.ToDictionary(g => g.Name.Trim(), g => g.Members, StringComparer.OrdinalIgnoreCase);

        RunningAudioProcesses.Clear();
        foreach (var session in AudioSessionMonitor.ListActiveSessions().OrderBy(s => s.ExecutableName))
        {
            var matches = ProcessTrackRouter.ResolveTrackNames(session.ExecutableName, tracks, groups);
            var description = matches.Count switch
            {
                0 => "not captured",
                1 => matches[0],
                // Landing on two tracks means the same audio is duplicated across stems, which is
                // almost always a missing exclusion rather than an intent.
                _ => $"{string.Join(", ", matches)} — duplicated",
            };

            RunningAudioProcesses.Add(new ProcessRouteRow(session, description));
        }

        SelectedProcess = RunningAudioProcesses.FirstOrDefault(p =>
            string.Equals(p.ExecutableName, previousSelection, StringComparison.OrdinalIgnoreCase))
            ?? RunningAudioProcesses.FirstOrDefault();
    }

    /// <summary>Adds the picked process to the selected track as an include rule.</summary>
    [RelayCommand]
    private void AddProcessToTrack()
    {
        if (SelectedTrack is null || SelectedProcess is not { } process) return;

        var rule = $"proc:{process.ExecutableName}";
        var existing = SelectedTrack.SourcesText;
        if (existing.Contains(rule, StringComparison.OrdinalIgnoreCase)) return;

        SelectedTrack.SourcesText = string.IsNullOrWhiteSpace(existing)
            ? rule
            : existing.TrimEnd() + Environment.NewLine + rule;

        RefreshProcesses();
    }

    /// <summary>
    /// Adds the picked process as an exclusion on every track that uses a catch-all rule, which is
    /// what "route this app somewhere else" actually requires.
    /// </summary>
    [RelayCommand]
    private void ExcludeProcessFromCatchAll()
    {
        if (SelectedProcess is not { } process) return;

        var rule = $"proc:!{process.ExecutableName}";
        foreach (var track in Tracks)
        {
            if (!track.SourcesText.Contains("proc:*", StringComparison.OrdinalIgnoreCase)) continue;
            if (track.SourcesText.Contains(rule, StringComparison.OrdinalIgnoreCase)) continue;

            track.SourcesText = track.SourcesText.TrimEnd() + Environment.NewLine + rule;
        }

        RefreshProcesses();
    }

    [RelayCommand]
    private void AddTrack()
    {
        var track = new TrackRowViewModel { Name = $"Track {Tracks.Count + 1}" };
        Tracks.Add(track);
        SelectedTrack = track;
        OnPropertyChanged(nameof(EstimatedMemory));
    }

    [RelayCommand]
    private void RemoveTrack()
    {
        if (SelectedTrack is null) return;
        Tracks.Remove(SelectedTrack);
        SelectedTrack = Tracks.FirstOrDefault();
        OnPropertyChanged(nameof(EstimatedMemory));
    }

    [RelayCommand]
    private void AddGroup()
    {
        var group = new GroupRowViewModel { Name = $"group{Groups.Count + 1}" };
        Groups.Add(group);
        SelectedGroup = group;
    }

    [RelayCommand]
    private void RemoveGroup()
    {
        if (SelectedGroup is null) return;
        Groups.Remove(SelectedGroup);
        SelectedGroup = Groups.FirstOrDefault();
    }

    /// <summary>Builds the new config, or returns null and sets <see cref="ValidationError"/>.</summary>
    public AppConfig? TryBuild()
    {
        if (BufferSeconds is < 5 or > 600)
        {
            ValidationError = "Buffer length must be between 5 and 600 seconds.";
            return null;
        }

        if (!HotkeyBinding.TryParse(Hotkey, out _, out var hotkeyError))
        {
            ValidationError = $"Hotkey: {hotkeyError}";
            return null;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            ValidationError = "Choose an output folder.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(OutputDirectory);
        }
        catch (Exception ex)
        {
            ValidationError = $"Output folder is unusable: {ex.Message}";
            return null;
        }

        if (Tracks.Count == 0)
        {
            ValidationError = "At least one audio track is required.";
            return null;
        }

        var trackErrors = Tracks.SelectMany(t => t.Validate()).ToList();
        if (trackErrors.Count > 0)
        {
            ValidationError = string.Join(Environment.NewLine, trackErrors);
            return null;
        }

        foreach (var group in Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Contains(':'))
            {
                ValidationError = $"Group name '{group.Name}' must be non-empty and cannot contain ':'.";
                return null;
            }
        }

        var duplicateGroupName = Groups
            .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicateGroupName is not null)
        {
            ValidationError = $"Group name '{duplicateGroupName}' is used more than once.";
            return null;
        }

        if (Displays.All(d => !d.Enabled))
        {
            ValidationError = "At least one display must be enabled.";
            return null;
        }

        var partialResolution = Displays.FirstOrDefault(d => d.Enabled && d.HasPartialResolutionOverride);
        if (partialResolution is not null)
        {
            ValidationError = $"{partialResolution.Description}: set both width and height, or leave both blank for native.";
            return null;
        }

        if (BlankDisplayTimeoutSeconds < 0)
        {
            ValidationError = "Ditch-after timeout cannot be negative.";
            return null;
        }

        ValidationError = null;

        return _original with
        {
            BufferSeconds = BufferSeconds,
            OutputDirectory = OutputDirectory,
            Hotkey = Hotkey,
            StartWithWindows = StartWithWindows,
            PlaySoundOnSave = PlaySoundOnSave,
            ShowOverlayIndicator = ShowOverlayIndicator,
            OverlayCorner = OverlayCorner,
            MaxRingMemoryMegabytes = Math.Clamp(MaxRingMemoryMegabytes, 256, 32768),
            BlankDisplayTimeoutSeconds = BlankDisplayTimeoutSeconds,
            CaptureBackend = CaptureBackend,
            VideoEncoderBackend = VideoEncoderBackend,
            Displays = [.. Displays.Select(d => d.ToConfig())],
            AudioTracks = [.. Tracks.Select(t => t.ToConfig())],
            ProcessGroups = Groups.ToDictionary(
                g => g.Name.Trim(), g => g.Members, StringComparer.OrdinalIgnoreCase),
        };
    }
}
