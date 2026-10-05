using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Matching;

public class CommandMatcherTests
{
    private readonly CommandMatcher _m = new();
    private static readonly IReadOnlyList<AppDefinition> NoApps = Array.Empty<AppDefinition>();

    [Fact]
    public void Exact_phrase_matches_regardless_of_case_and_punctuation()
    {
        var cmds = new[] { Make.Command("notepad", new[] { "open notepad" }) };
        var r = _m.Match("Open  Notepad!", cmds, NoApps);
        Assert.NotNull(r);
        Assert.Equal("notepad", r!.Command.Id);
        Assert.True(r.IsExact);
        Assert.Null(r.Number);
    }

    [Fact]
    public void Unknown_text_does_not_match()
    {
        var cmds = new[] { Make.Command("notepad", new[] { "open notepad" }) };
        Assert.Null(_m.Match("open calculator", cmds, NoApps));
        Assert.Null(_m.Match("", cmds, NoApps));
    }

    [Fact]
    public void Partial_phrase_does_not_match()
    {
        var cmds = new[] { Make.Command("notepad", new[] { "open notepad" }) };
        Assert.Null(_m.Match("please open notepad now", cmds, NoApps));
    }

    [Fact]
    public void Any_of_several_phrases_matches_including_arabic()
    {
        var cmds = new[] { Make.Command("shot", new[] { "take screenshot", "التقط صورة للشاشة" }) };
        Assert.NotNull(_m.Match("take screenshot", cmds, NoApps));
        Assert.NotNull(_m.Match("التقط صورة للشاشة", cmds, NoApps));
        Assert.NotNull(_m.Match("التقط صوره للشاشه", cmds, NoApps)); // ta marbuta variants
    }

    [Fact]
    public void Disabled_commands_are_skipped()
    {
        var c = Make.Command("a", new[] { "do it" });
        c.Enabled = false;
        Assert.Null(_m.Match("do it", new[] { c }, NoApps));
    }

    [Fact]
    public void Number_template_captures_digits_and_spoken_numbers()
    {
        var cmds = new[] { Make.Command("vol", new[] { "volume {number}" }) };
        Assert.Equal(40, _m.Match("volume 40", cmds, NoApps)!.Number);
        Assert.Equal(25, _m.Match("volume twenty five", cmds, NoApps)!.Number);
        Assert.Equal(100, _m.Match("volume one hundred", cmds, NoApps)!.Number);
        Assert.Null(_m.Match("volume loud", cmds, NoApps));
        Assert.Null(_m.Match("volume", cmds, NoApps));
    }

    [Fact]
    public void Arabic_number_template_works()
    {
        var cmds = new[] { Make.Command("vol", new[] { "الصوت {number}" }) };
        Assert.Equal(30, _m.Match("الصوت ٣٠", cmds, NoApps)!.Number);
        Assert.Equal(25, _m.Match("الصوت خمسة وعشرين", cmds, NoApps)!.Number);
    }

    [Fact]
    public void App_template_resolves_alias_and_longer_alias_wins()
    {
        var apps = new[]
        {
            Make.App("app.chrome", "Chrome", "google chrome", "chrome"),
            Make.App("app.chromium", "Chromium", "chrome beta"),
        };
        var cmds = new[] { Make.Command("mute", new[] { "mute {app}" }) };
        Assert.Equal("app.chrome", _m.Match("mute google chrome", cmds, apps)!.App!.Id);
        Assert.Equal("app.chrome", _m.Match("mute chrome", cmds, apps)!.App!.Id);
        Assert.Null(_m.Match("mute spotify", cmds, apps));
    }

    [Fact]
    public void App_and_number_template_capture_both()
    {
        var apps = new[] { Make.App("app.discord", "Discord", "discord") };
        var cmds = new[] { Make.Command("appvol", new[] { "{app} volume {number}" }) };
        var r = _m.Match("discord volume 35", cmds, apps)!;
        Assert.Equal("app.discord", r.App!.Id);
        Assert.Equal(35, r.Number);
    }

    [Fact]
    public void Literal_phrase_beats_template()
    {
        var apps = new[] { Make.App("app.discord", "Discord", "discord") };
        var generic = Make.Command("mute.app", new[] { "mute {app}" });
        var specific = Make.Command("mute.discord", new[] { "mute discord" });
        var r = _m.Match("mute discord", new[] { generic, specific }, apps)!;
        Assert.Equal("mute.discord", r.Command.Id);
        // order must not matter
        r = _m.Match("mute discord", new[] { specific, generic }, apps)!;
        Assert.Equal("mute.discord", r.Command.Id);
    }

    [Fact]
    public void Similarity_is_off_by_default_so_near_misses_do_not_match()
    {
        var cmds = new[] { Make.Command("notepad", new[] { "open notepad" }) };
        Assert.Null(_m.Match("open notepat", cmds, NoApps));
    }

    [Fact]
    public void Similarity_tolerance_enables_fuzzy_matching_for_literals_only()
    {
        var cmds = new[]
        {
            Make.Command("notepad", new[] { "open notepad" }),
            Make.Command("vol", new[] { "volume {number}" }),
        };
        var r = _m.Match("open notepat", cmds, NoApps, 0.2);
        Assert.NotNull(r);
        Assert.False(r!.IsExact);
        Assert.Equal("notepad", r.Command.Id);
        Assert.Null(_m.Match("totally different words", cmds, NoApps, 0.2));
        // templated phrases are never fuzzy-matched
        Assert.Null(_m.Match("volum 40", cmds, NoApps, 0.2));
    }

    [Fact]
    public void Exact_match_is_preferred_over_fuzzy_even_when_tolerance_is_set()
    {
        var cmds = new[]
        {
            Make.Command("a", new[] { "open notepad" }),
            Make.Command("b", new[] { "open notepads" }),
        };
        var r = _m.Match("open notepads", cmds, NoApps, 0.3)!;
        Assert.Equal("b", r.Command.Id);
        Assert.True(r.IsExact);
    }

    [Fact]
    public void FindDuplicates_reports_phrases_shared_by_enabled_commands()
    {
        var a = Make.Command("a", new[] { "Start Game", "play" });
        var b = Make.Command("b", new[] { "start  game!" });
        var c = Make.Command("c", new[] { "unique" });
        var dups = CommandMatcher.FindDuplicates(new[] { a, b, c });
        var d = Assert.Single(dups);
        Assert.Equal("start game", TextNormalizer.Normalize(d.Phrase));
        Assert.Equal(2, d.Commands.Count);
    }

    [Fact]
    public void FindDuplicates_ignores_disabled_commands()
    {
        var a = Make.Command("a", new[] { "go" });
        var b = Make.Command("b", new[] { "go" });
        b.Enabled = false;
        Assert.Empty(CommandMatcher.FindDuplicates(new[] { a, b }));
    }
}
