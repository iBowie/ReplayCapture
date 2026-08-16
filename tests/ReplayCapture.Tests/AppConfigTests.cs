using System.Text.Json;
using ReplayCapture.Core.Config;

namespace ReplayCapture.Tests;

public class AppConfigTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Default_audio_tracks_all_parse()
    {
        foreach (var track in AppConfig.DefaultAudioTracks)
        foreach (var source in track.Sources)
        {
            Assert.True(
                AudioSourceSpec.TryParse(source, out _, out var error),
                $"track '{track.Name}' source '{source}': {error}");
        }
    }

    [Fact]
    public void Default_layout_has_the_six_planned_tracks_in_order()
    {
        Assert.Equal(
            ["Desktop + Mic", "Desktop", "Mic", "Game", "Communications", "Music"],
            AppConfig.DefaultAudioTracks.Select(t => t.Name));
    }

    [Fact]
    public void Game_track_captures_everything_not_claimed_elsewhere()
    {
        var game = AppConfig.DefaultAudioTracks.Single(t => t.Name == "Game");
        var specs = game.ParsedSources.ToList();

        Assert.Contains(specs, s => s is { Kind: AudioSourceKind.Process, ProcessPattern: "*", IsExclusion: false });

        // Anything routed to Communications or Music must be excluded from Game, or it would be
        // duplicated across tracks and defeat the point of stems.
        var excluded = specs.Where(s => s.IsExclusion).ToList();
        foreach (var other in AppConfig.DefaultAudioTracks.Where(t => t.Name is "Communications" or "Music"))
        foreach (var source in other.ParsedSources.Where(s => s.Kind == AudioSourceKind.Process))
        {
            Assert.True(
                excluded.Any(e => e.MatchesProcess(source.ProcessPattern!)),
                $"'{source.ProcessPattern}' is on the {other.Name} track but not excluded from Game.");
        }
    }

    [Fact]
    public void Track_count_is_not_capped_at_six()
    {
        var config = new AppConfig
        {
            AudioTracks =
            [
                .. AppConfig.DefaultAudioTracks,
                new AudioTrackConfig { Name = "Browser", Sources = ["proc:chrome.exe"] },
                new AudioTrackConfig { Name = "Alerts", Sources = ["proc:obs64.exe"] },
            ],
        };

        var restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, Options), Options)!;

        Assert.Equal(8, restored.AudioTracks.Count);
        Assert.Equal("Alerts", restored.AudioTracks[^1].Name);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var config = new AppConfig
        {
            BufferSeconds = 45,
            Hotkey = "Ctrl+Alt+R",
            OutputDirectory = @"S:\Replays",
            MaxRingMemoryMegabytes = 1536,
            Displays =
            [
                new DisplayConfig { DeviceName = @"\\.\DISPLAY1", Fps = null, BitrateMbps = 50, Label = "Main" },
                new DisplayConfig { DeviceName = @"\\.\DISPLAY2", Fps = 60, BitrateMbps = 30, Enabled = false },
            ],
        };

        var restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, Options), Options)!;

        Assert.Equal(45, restored.BufferSeconds);
        Assert.Equal("Ctrl+Alt+R", restored.Hotkey);
        Assert.Equal(@"S:\Replays", restored.OutputDirectory);
        Assert.Equal(1536, restored.MaxRingMemoryMegabytes);
        Assert.Equal(2, restored.Displays.Count);
        Assert.Null(restored.Displays[0].Fps);          // null means "follow the display refresh rate"
        Assert.Equal(60, restored.Displays[1].Fps);
        Assert.False(restored.Displays[1].Enabled);
    }

    [Fact]
    public void Displays_default_to_empty_so_first_run_can_autodetect()
    {
        Assert.Empty(new AppConfig().Displays);
    }

    [Fact]
    public void Capture_backend_defaults_to_dxgi()
    {
        Assert.Equal(CaptureBackend.Dxgi, new AppConfig().CaptureBackend);
    }

    [Fact]
    public void Capture_backend_round_trips_through_json()
    {
        var config = new AppConfig { CaptureBackend = CaptureBackend.Wgc };

        var restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, Options), Options)!;

        Assert.Equal(CaptureBackend.Wgc, restored.CaptureBackend);
    }

    [Fact]
    public void Video_encoder_backend_defaults_to_nvenc()
    {
        Assert.Equal(VideoEncoderBackend.Nvenc, new AppConfig().VideoEncoderBackend);
    }

    [Fact]
    public void Video_encoder_backend_round_trips_through_json()
    {
        var config = new AppConfig { VideoEncoderBackend = VideoEncoderBackend.X264 };

        var restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, Options), Options)!;

        Assert.Equal(VideoEncoderBackend.X264, restored.VideoEncoderBackend);
    }

    [Fact]
    public void Process_groups_round_trip_through_json()
    {
        var config = new AppConfig
        {
            ProcessGroups = new Dictionary<string, IReadOnlyList<string>>
            {
                ["comms"] = ["discord.exe", "telegram.exe"],
            },
        };

        var restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, Options), Options)!;

        Assert.True(AudioSourceSpec.TryResolveGroup("comms", restored.ProcessGroups, out var members));
        Assert.Equal(["discord.exe", "telegram.exe"], members);
    }
}
