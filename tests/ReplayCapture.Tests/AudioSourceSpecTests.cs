using ReplayCapture.Core.Config;

namespace ReplayCapture.Tests;

public class AudioSourceSpecTests
{
    [Fact]
    public void Parses_default_render_endpoint()
    {
        var spec = AudioSourceSpec.Parse("device:render:default");

        Assert.Equal(AudioSourceKind.RenderLoopback, spec.Kind);
        // null endpoint means "whatever the default is right now", so the track follows the user
        // switching output devices without a config edit.
        Assert.Null(spec.EndpointId);
        Assert.False(spec.IsExclusion);
    }

    [Fact]
    public void Parses_explicit_endpoint_id()
    {
        var spec = AudioSourceSpec.Parse("device:capture:{0.0.1.00000000}.{abc-123}");

        Assert.Equal(AudioSourceKind.Capture, spec.Kind);
        Assert.Equal("{0.0.1.00000000}.{abc-123}", spec.EndpointId);
    }

    [Theory]
    [InlineData("proc:spotify.exe", "spotify.exe", false)]
    [InlineData("proc:!discord.exe", "discord.exe", true)]
    [InlineData("proc:*", "*", false)]
    public void Parses_process_specs(string input, string expectedPattern, bool expectedExclusion)
    {
        var spec = AudioSourceSpec.Parse(input);

        Assert.Equal(AudioSourceKind.Process, spec.Kind);
        Assert.Equal(expectedPattern, spec.ProcessPattern);
        Assert.Equal(expectedExclusion, spec.IsExclusion);
    }

    [Theory]
    [InlineData("proc:spotify.exe", "Spotify.exe", true)]      // case-insensitive
    [InlineData("proc:spotify.exe", "spotify.exe", true)]
    [InlineData("proc:spotify.exe", "discord.exe", false)]
    [InlineData("proc:*", "anything.exe", true)]
    [InlineData("proc:steam*.exe", "steamwebhelper.exe", true)]
    [InlineData("proc:steam*.exe", "steam.exe", true)]
    [InlineData("proc:steam*.exe", "notsteam.exe", false)]
    [InlineData("proc:!discord.exe", "discord.exe", true)]     // exclusions still match by name
    public void Matches_process_names_with_wildcards(string spec, string exeName, bool expected)
    {
        Assert.Equal(expected, AudioSourceSpec.Parse(spec).MatchesProcess(exeName));
    }

    [Fact]
    public void Wildcard_does_not_leak_across_the_whole_name()
    {
        // '?' is a single character, not "one or more".
        var spec = AudioSourceSpec.Parse("proc:g?me.exe");
        Assert.True(spec.MatchesProcess("game.exe"));
        Assert.False(spec.MatchesProcess("gaaame.exe"));
    }

    [Fact]
    public void Device_specs_never_match_processes()
    {
        Assert.False(AudioSourceSpec.Parse("device:render:default").MatchesProcess("spotify.exe"));
    }

    [Theory]
    [InlineData("group:comms", "comms", false)]
    [InlineData("group:!comms", "comms", true)]
    public void Parses_group_specs(string input, string expectedName, bool expectedExclusion)
    {
        var spec = AudioSourceSpec.Parse(input);

        Assert.Equal(AudioSourceKind.Group, spec.Kind);
        Assert.Equal(expectedName, spec.GroupName);
        Assert.Equal(expectedExclusion, spec.IsExclusion);
    }

    [Fact]
    public void Group_specs_never_match_processes_directly()
    {
        // Resolving a group name needs the group table, which this type has no reference to;
        // ProcessTrackBinding expands group: specs into proc: specs before matching.
        Assert.False(AudioSourceSpec.Parse("group:comms").MatchesProcess("discord.exe"));
    }

    [Fact]
    public void Resolves_group_members_case_insensitively()
    {
        var groups = new Dictionary<string, IReadOnlyList<string>> { ["Comms"] = ["discord.exe"] };

        Assert.True(AudioSourceSpec.TryResolveGroup("comms", groups, out var members));
        Assert.Equal(["discord.exe"], members);
    }

    [Fact]
    public void Unknown_group_fails_to_resolve()
    {
        Assert.False(AudioSourceSpec.TryResolveGroup("nonexistent", AppConfig.DefaultProcessGroups, out var members));
        Assert.Empty(members);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("device:render")]              // missing endpoint segment
    [InlineData("device:sideways:default")]    // unknown flow
    [InlineData("proc:")]                      // no executable
    [InlineData("proc:!")]                     // exclusion with no executable
    [InlineData("group:")]                     // no group name
    [InlineData("group:!")]                    // exclusion with no group name
    [InlineData("window:notepad.exe")]         // unknown prefix
    public void Rejects_malformed_specs(string input)
    {
        Assert.False(AudioSourceSpec.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<FormatException>(() => AudioSourceSpec.Parse(input));
    }

    [Fact]
    public void Round_trips_through_raw_text()
    {
        const string raw = "proc:!spotify.exe";
        Assert.Equal(raw, AudioSourceSpec.Parse(raw).ToString());
    }
}
