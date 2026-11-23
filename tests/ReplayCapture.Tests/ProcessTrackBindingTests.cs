using ReplayCapture.Core.Audio;
using ReplayCapture.Core.Config;

namespace ReplayCapture.Tests;

public class ProcessTrackBindingTests
{
    private static ProcessTrackBinding Binding(string trackName, params string[] sources) =>
        Binding(trackName, AppConfig.DefaultProcessGroups, sources);

    private static ProcessTrackBinding Binding(
        string trackName, IReadOnlyDictionary<string, IReadOnlyList<string>> groups, params string[] sources) =>
        new(new AudioTrackBuffer(trackName, 0, 5), sources.Select(AudioSourceSpec.Parse), groups);

    private static AudioSessionInfo Session(string exe, uint pid = 1234) => new(pid, exe);

    private static ProcessTrackBinding DefaultTrack(string name)
    {
        var track = AppConfig.DefaultAudioTracks.Single(t => t.Name == name);
        return Binding(name, [.. track.Sources]);
    }

    [Fact]
    public void Include_matches_and_exclude_wins()
    {
        var binding = Binding("Game", "proc:*", "proc:!discord.exe");

        Assert.True(binding.Matches(Session("someGame.exe")));
        Assert.False(binding.Matches(Session("discord.exe")));
    }

    [Fact]
    public void Exclusions_win_regardless_of_config_order()
    {
        // Ordering in config must not change behaviour; excludes are always applied last.
        var before = Binding("Game", "proc:!discord.exe", "proc:*");
        var after = Binding("Game", "proc:*", "proc:!discord.exe");

        Assert.False(before.Matches(Session("discord.exe")));
        Assert.False(after.Matches(Session("discord.exe")));
        Assert.True(before.Matches(Session("game.exe")));
        Assert.True(after.Matches(Session("game.exe")));
    }

    [Fact]
    public void A_track_with_no_process_rules_binds_to_nothing()
    {
        var binding = Binding("Desktop", "device:render:default");

        Assert.False(binding.HasRules);
        Assert.False(binding.Matches(Session("anything.exe")));
    }

    [Fact]
    public void Own_process_is_never_captured()
    {
        // Otherwise a save chime would land on the Game stem.
        var binding = Binding("Game", "proc:*");

        Assert.False(binding.Matches(new AudioSessionInfo((uint)Environment.ProcessId, "ReplayCapture.exe")));
        Assert.True(binding.Matches(new AudioSessionInfo((uint)Environment.ProcessId + 1, "ReplayCapture.exe")));
    }

    [Theory]
    [InlineData("discord.exe", "Communications")]
    [InlineData("Discord.exe", "Communications")]   // real sessions report mixed case
    [InlineData("ms-teams.exe", "Communications")]
    [InlineData("slack.exe", "Communications")]
    [InlineData("spotify.exe", "Music")]
    [InlineData("foobar2000.exe", "Music")]
    public void Shipped_defaults_route_known_apps_off_the_game_track(string exe, string expectedTrack)
    {
        Assert.True(DefaultTrack(expectedTrack).Matches(Session(exe)),
            $"{exe} should be on {expectedTrack}");

        // The important half: it must not also land on Game, or it would be duplicated.
        Assert.False(DefaultTrack("Game").Matches(Session(exe)),
            $"{exe} must be excluded from Game");
    }

    [Theory]
    [InlineData("eldenring.exe")]
    [InlineData("cs2.exe")]
    [InlineData("some-brand-new-game.exe")]
    public void Unknown_apps_fall_through_to_the_game_track(string exe)
    {
        // A game nobody has configured must still be captured, with no config edit required.
        Assert.True(DefaultTrack("Game").Matches(Session(exe)));
        Assert.False(DefaultTrack("Communications").Matches(Session(exe)));
        Assert.False(DefaultTrack("Music").Matches(Session(exe)));
    }

    [Fact]
    public void Every_process_lands_on_exactly_one_default_track()
    {
        var processTracks = AppConfig.DefaultAudioTracks
            .Where(t => t.ParsedSources.Any(s => s.Kind == AudioSourceKind.Process))
            .Select(t => DefaultTrack(t.Name))
            .ToList();

        foreach (var exe in (ReadOnlySpan<string>)
                 ["discord.exe", "spotify.exe", "slack.exe", "ms-teams.exe", "foobar2000.exe", "game.exe"])
        {
            var matches = processTracks.Count(b => b.Matches(Session(exe)));
            Assert.True(matches == 1, $"{exe} matched {matches} tracks; expected exactly 1.");
        }
    }

    [Fact]
    public void Wildcard_patterns_work_in_rules()
    {
        var binding = Binding("Comms", "proc:steam*.exe");

        Assert.True(binding.Matches(Session("steamwebhelper.exe")));
        Assert.True(binding.Matches(Session("steam.exe")));
        Assert.False(binding.Matches(Session("notsteam.exe")));
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> Groups = new()
    {
        ["comms"] = ["discord.exe", "telegram.exe"],
        ["music"] = ["spotify.exe"],
    };

    [Fact]
    public void Group_include_matches_every_member()
    {
        var binding = Binding("Comms", Groups, "group:comms");

        Assert.True(binding.Matches(Session("discord.exe")));
        Assert.True(binding.Matches(Session("telegram.exe")));
        Assert.False(binding.Matches(Session("spotify.exe")));
    }

    [Fact]
    public void Group_exclusion_removes_every_member_from_a_catch_all()
    {
        var binding = Binding("Game", Groups, "proc:*", "group:!comms", "group:!music");

        Assert.False(binding.Matches(Session("discord.exe")));
        Assert.False(binding.Matches(Session("telegram.exe")));
        Assert.False(binding.Matches(Session("spotify.exe")));
        Assert.True(binding.Matches(Session("game.exe")));
    }

    [Fact]
    public void Group_name_lookup_is_case_insensitive()
    {
        var binding = Binding("Comms", Groups, "group:COMMS");

        Assert.True(binding.Matches(Session("discord.exe")));
    }

    [Fact]
    public void Unknown_group_matches_nothing_instead_of_throwing()
    {
        var binding = Binding("Comms", Groups, "group:nonexistent");

        Assert.False(binding.HasRules);
        Assert.False(binding.Matches(Session("discord.exe")));
    }

    [Fact]
    public void Default_process_groups_match_the_shipped_communications_and_music_tracks()
    {
        Assert.True(AudioSourceSpec.TryResolveGroup("comms", AppConfig.DefaultProcessGroups, out var comms));
        Assert.Contains("discord.exe", comms);
        Assert.Contains("ms-teams.exe", comms);
        Assert.Contains("slack.exe", comms);

        Assert.True(AudioSourceSpec.TryResolveGroup("music", AppConfig.DefaultProcessGroups, out var music));
        Assert.Contains("spotify.exe", music);
        Assert.Contains("foobar2000.exe", music);
    }

    /// <summary>
    /// An <see cref="IAudioSource"/> whose <see cref="Start"/> blocks until told to proceed, standing
    /// in for a slow <c>ActivateAudioInterfaceAsync</c> call without touching real WASAPI.
    /// </summary>
    private sealed class BlockingFakeSource(string name, ManualResetEventSlim release) : IAudioSource
    {
        public string Name { get; } = name;
        public long FramesCaptured => 0;
        public event Action<long, ReadOnlyMemory<float>>? SamplesReady { add { } remove { } }

        // Stands in for the up-to-5s ActivateAudioInterfaceAsync wait a real ProcessLoopbackSource
        // blocks on, so a second Refresh has a wide window to (incorrectly) attach concurrently.
        public void Start() => release.Wait();

        public void Dispose() { }
    }

    [Fact]
    public async Task Concurrent_refresh_does_not_double_attach_the_same_process()
    {
        // Reproduces the poll-timer reentrancy race: a second Refresh (the next timer tick) firing
        // while a slow attach from the previous tick is still in progress must not attach its own
        // second source to the same process, or that process's audio lands on the track twice.
        var binding = Binding("Game", "proc:*");

        var releaseAttach = new ManualResetEventSlim(false);
        var createCount = 0;
        var firstCreated = new ManualResetEventSlim(false);
        var secondCreated = new ManualResetEventSlim(false);

        binding.CreateSource = (pid, exe) =>
        {
            var call = Interlocked.Increment(ref createCount);
            (call == 1 ? firstCreated : secondCreated).Set();
            return new BlockingFakeSource($"{exe}#{pid}", releaseAttach);
        };

        var sessions = new[] { Session("game.exe", pid: 42) };

        var firstTick = Task.Run(() => binding.Refresh(sessions));

        // Wait until the first Refresh call has created its source and is blocked mid-activation.
        Assert.True(firstCreated.Wait(TimeSpan.FromSeconds(5)));

        // The overlapping poll tick: fires before the first attach has returned. Without the fix,
        // nothing blocks it from reaching CreateSource immediately; give it a moment to do so.
        var secondTick = Task.Run(() => binding.Refresh(sessions));
        secondCreated.Wait(TimeSpan.FromMilliseconds(300));

        releaseAttach.Set();
        await Task.WhenAll(firstTick, secondTick).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, createCount);
        Assert.Equal(1, binding.AttachedCount);
    }
}
