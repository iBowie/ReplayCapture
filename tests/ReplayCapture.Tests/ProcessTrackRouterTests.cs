using ReplayCapture.Core.Audio;
using ReplayCapture.Core.Config;

namespace ReplayCapture.Tests;

public class ProcessTrackRouterTests
{
    private static AudioTrackConfig Track(string name, bool enabled, params string[] sources) => new()
    {
        Name = name,
        Enabled = enabled,
        Sources = sources,
    };

    private static readonly Dictionary<string, IReadOnlyList<string>> Groups = new()
    {
        ["comms"] = ["discord.exe", "telegram.exe"],
    };

    [Fact]
    public void Resolves_to_the_one_track_whose_rules_match()
    {
        var tracks = new[]
        {
            Track("Game", true, "proc:*", "proc:!spotify.exe"),
            Track("Music", true, "proc:spotify.exe"),
        };

        Assert.Equal(["Music"], ProcessTrackRouter.ResolveTrackNames("spotify.exe", tracks, null));
        Assert.Equal(["Game"], ProcessTrackRouter.ResolveTrackNames("game.exe", tracks, null));
    }

    [Fact]
    public void Reports_every_track_a_process_lands_on_so_duplicates_are_visible()
    {
        // A missing exclusion on the catch-all track must surface as two matches, not one.
        var tracks = new[]
        {
            Track("Game", true, "proc:*"),
            Track("Music", true, "proc:spotify.exe"),
        };

        Assert.Equal(["Game", "Music"], ProcessTrackRouter.ResolveTrackNames("spotify.exe", tracks, null));
    }

    [Fact]
    public void Disabled_tracks_are_never_matched()
    {
        var tracks = new[] { Track("Music", false, "proc:spotify.exe") };

        Assert.Empty(ProcessTrackRouter.ResolveTrackNames("spotify.exe", tracks, null));
    }

    [Fact]
    public void Unmatched_process_resolves_to_no_tracks()
    {
        var tracks = new[] { Track("Music", true, "proc:spotify.exe") };

        Assert.Empty(ProcessTrackRouter.ResolveTrackNames("game.exe", tracks, null));
    }

    [Fact]
    public void Group_specs_expand_before_matching()
    {
        var tracks = new[] { Track("Comms", true, "group:comms") };

        Assert.Equal(["Comms"], ProcessTrackRouter.ResolveTrackNames("discord.exe", tracks, Groups));
        Assert.Equal(["Comms"], ProcessTrackRouter.ResolveTrackNames("telegram.exe", tracks, Groups));
        Assert.Empty(ProcessTrackRouter.ResolveTrackNames("spotify.exe", tracks, Groups));
    }

    [Fact]
    public void Group_exclusion_removes_members_from_a_catch_all()
    {
        var tracks = new[] { Track("Game", true, "proc:*", "group:!comms") };

        Assert.Empty(ProcessTrackRouter.ResolveTrackNames("discord.exe", tracks, Groups));
        Assert.Equal(["Game"], ProcessTrackRouter.ResolveTrackNames("game.exe", tracks, Groups));
    }

    [Fact]
    public void Malformed_source_lines_are_skipped_instead_of_throwing()
    {
        // The Settings preview evaluates tracks while the user is still mid-edit.
        var tracks = new[] { Track("Game", true, "proc:*", "not-a-real-source") };

        var matched = ProcessTrackRouter.ResolveTrackNames("game.exe", tracks, null);

        Assert.Equal(["Game"], matched);
    }

    [Fact]
    public void Unknown_group_is_skipped_instead_of_throwing()
    {
        var tracks = new[] { Track("Comms", true, "group:nonexistent") };

        Assert.Empty(ProcessTrackRouter.ResolveTrackNames("discord.exe", tracks, Groups));
    }

    [Fact]
    public void ExpandSpecs_turns_a_group_into_one_process_spec_per_member()
    {
        var expanded = ProcessTrackRouter.ExpandSpecs(Track("Comms", true, "group:comms"), Groups);

        Assert.Equal(2, expanded.Count);
        Assert.All(expanded, s => Assert.Equal(AudioSourceKind.Process, s.Kind));
        Assert.Contains(expanded, s => s.ProcessPattern == "discord.exe");
        Assert.Contains(expanded, s => s.ProcessPattern == "telegram.exe");
    }

    [Fact]
    public void ExpandSpecs_drops_malformed_lines_and_unknown_groups()
    {
        var expanded = ProcessTrackRouter.ExpandSpecs(
            Track("Game", true, "proc:*", "not-a-real-source", "group:nonexistent"), Groups);

        Assert.Single(expanded);
        Assert.Equal("*", expanded[0].ProcessPattern);
    }
}
