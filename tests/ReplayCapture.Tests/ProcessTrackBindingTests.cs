using ReplayCapture.Core.Audio;
using ReplayCapture.Core.Config;

namespace ReplayCapture.Tests;

public class ProcessTrackBindingTests
{
    private static ProcessTrackBinding Binding(string trackName, params string[] sources) =>
        new(new AudioTrackBuffer(trackName, 0, 5), sources.Select(AudioSourceSpec.Parse));

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
}
