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
    public required string DeviceName { get; init; }
    public required string Description { get; init; }

    [ObservableProperty] private bool _enabled = true;

    /// <summary>Empty or "auto" follows the display's own refresh rate.</summary>
    [ObservableProperty] private string _fps = "auto";

    [ObservableProperty] private int _bitrateMbps = 40;

    public DisplayConfig ToConfig() => new()
    {
        DeviceName = DeviceName,
        Enabled = Enabled,
        Fps = int.TryParse(Fps, out var parsed) && parsed > 0 ? parsed : null,
        BitrateMbps = Math.Clamp(BitrateMbps, 1, 500),
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
    [ObservableProperty] private CaptureBackend _captureBackend;
    [ObservableProperty] private string? _validationError;

    public ObservableCollection<DisplayRowViewModel> Displays { get; } = [];
    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];
    public ObservableCollection<GroupRowViewModel> Groups { get; } = [];
    public ObservableCollection<AudioSessionInfo> RunningAudioProcesses { get; } = [];

    public IReadOnlyList<OverlayCorner> OverlayCorners { get; } = Enum.GetValues<OverlayCorner>();
    public IReadOnlyList<CaptureBackend> CaptureBackends { get; } = Enum.GetValues<CaptureBackend>();

    [ObservableProperty] private TrackRowViewModel? _selectedTrack;
    [ObservableProperty] private GroupRowViewModel? _selectedGroup;
    [ObservableProperty] private AudioSessionInfo? _selectedProcess;

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
        _captureBackend = config.CaptureBackend;

        LoadDisplays(config);
        foreach (var track in config.AudioTracks) Tracks.Add(TrackRowViewModel.From(track));
        SelectedTrack = Tracks.FirstOrDefault();

        foreach (var (name, members) in config.ProcessGroups) Groups.Add(GroupRowViewModel.From(name, members));
        SelectedGroup = Groups.FirstOrDefault();

        RefreshProcesses();
    }

    private void LoadDisplays(AppConfig config)
    {
        foreach (var display in DisplayEnumerator.Enumerate())
        {
            var existing = config.Displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));

            Displays.Add(new DisplayRowViewModel
            {
                DeviceName = display.DeviceName,
                Description = display.Label + (display.IsPrimary ? "  •  primary" : ""),
                // No config yet means first run, and capturing everything is the useful default.
                Enabled = existing?.Enabled ?? true,
                Fps = existing?.Fps?.ToString() ?? "auto",
                BitrateMbps = existing?.BitrateMbps ?? 40,
            });
        }
    }

    partial void OnBufferSecondsChanged(int value) => OnPropertyChanged(nameof(EstimatedMemory));

    [RelayCommand]
    private void RefreshProcesses()
    {
        RunningAudioProcesses.Clear();
        foreach (var session in AudioSessionMonitor.ListActiveSessions().OrderBy(s => s.ExecutableName))
            RunningAudioProcesses.Add(session);
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
            CaptureBackend = CaptureBackend,
            Displays = [.. Displays.Select(d => d.ToConfig())],
            AudioTracks = [.. Tracks.Select(t => t.ToConfig())],
            ProcessGroups = Groups.ToDictionary(
                g => g.Name.Trim(), g => g.Members, StringComparer.OrdinalIgnoreCase),
        };
    }
}
