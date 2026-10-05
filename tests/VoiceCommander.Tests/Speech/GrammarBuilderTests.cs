using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Speech;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Speech;

public class GrammarBuilderTests
{
    private static readonly AppDefinition[] NoApps = Array.Empty<AppDefinition>();

    [Fact]
    public void Literal_phrases_are_included_and_normalized()
    {
        var cmds = new[] { Make.Command("a", new[] { "Open Notepad!" }) };
        var plan = GrammarBuilder.Build("en", cmds, NoApps, null);
        Assert.Contains("open notepad", plan.Phrases);
    }

    [Fact]
    public void Only_phrases_of_the_requested_language_are_used()
    {
        var cmds = new[] { Make.Command("a", new[] { "open notepad", "افتح المفكرة" }) };
        var en = GrammarBuilder.Build("en", cmds, NoApps, null);
        var ar = GrammarBuilder.Build("ar", cmds, NoApps, null);
        Assert.Contains("open notepad", en.Phrases);
        Assert.DoesNotContain(en.Phrases, p => p.Any(ch => ch >= '؀' && ch <= 'ۿ'));
        Assert.Contains(TextNormalizer.Normalize("افتح المفكرة"), ar.Phrases);
        Assert.DoesNotContain("open notepad", ar.Phrases);
    }

    [Fact]
    public void Disabled_commands_contribute_nothing()
    {
        var c = Make.Command("a", new[] { "secret phrase" });
        c.Enabled = false;
        Assert.DoesNotContain("secret phrase", GrammarBuilder.Build("en", new[] { c }, NoApps, null).Phrases);
    }

    [Fact]
    public void Number_template_expands_to_every_spoken_number()
    {
        var cmds = new[] { Make.Command("v", new[] { "volume {number}" }) };
        var plan = GrammarBuilder.Build("en", cmds, NoApps, null);
        Assert.Contains("volume zero", plan.Phrases);
        Assert.Contains("volume twenty five", plan.Phrases);
        Assert.Contains("volume one hundred", plan.Phrases);
        Assert.DoesNotContain(plan.Phrases, p => p.Contains('{'));
    }

    [Fact]
    public void App_template_expands_names_and_aliases()
    {
        var apps = new[] { Make.App("app.discord", "Discord", "disc", "voice chat") };
        var cmds = new[] { Make.Command("m", new[] { "mute {app}" }) };
        var plan = GrammarBuilder.Build("en", cmds, apps, null);
        Assert.Contains("mute discord", plan.Phrases);
        Assert.Contains("mute disc", plan.Phrases);
        Assert.Contains("mute voice chat", plan.Phrases);
    }

    [Fact]
    public void Phrases_are_deduplicated_and_literals_come_first()
    {
        var cmds = new[]
        {
            Make.Command("t", new[] { "volume {number}" }),
            Make.Command("a", new[] { "open notepad" }),
            Make.Command("b", new[] { "Open Notepad" }),
        };
        var plan = GrammarBuilder.Build("en", cmds, NoApps, null);
        Assert.Equal(plan.Phrases.Count, plan.Phrases.Distinct().Count());
        Assert.Equal("open notepad", plan.Phrases[0]);
    }

    [Fact]
    public void Words_missing_from_the_model_vocabulary_are_reported_unavailable()
    {
        var vocab = ModelVocabulary.FromWords(new[] { "open", "notepad", "[unk]" });
        var cmds = new[]
        {
            Make.Command("ok", new[] { "open notepad" }),
            Make.Command("bad", new[] { "open xylophone" }),
        };
        var plan = GrammarBuilder.Build("en", cmds, NoApps, vocab);
        Assert.Contains("open notepad", plan.Phrases);
        Assert.DoesNotContain("open xylophone", plan.Phrases);
        Assert.NotEmpty(plan.Unavailable);
    }

    [Fact]
    public void Wake_phrase_is_added_alone_and_as_prefix()
    {
        var cmds = new[] { Make.Command("a", new[] { "open notepad" }) };
        var plan = GrammarBuilder.Build("en", cmds, NoApps, null, "computer");
        Assert.Contains("computer", plan.Phrases);
        Assert.Contains("computer open notepad", plan.Phrases);
        Assert.Contains("open notepad", plan.Phrases);
    }

    [Fact]
    public void Wake_phrase_in_another_script_is_not_added()
    {
        var cmds = new[] { Make.Command("a", new[] { "open notepad" }) };
        var plan = GrammarBuilder.Build("en", cmds, NoApps, null, "يا حاسوب");
        Assert.DoesNotContain(plan.Phrases, p => p.Contains("حاسوب"));
    }

    [Fact]
    public void Phrase_count_is_capped()
    {
        // 100 numbers x 60 templates would be 6000+ phrases
        var cmds = Enumerable.Range(0, 60)
            .Select(i => Make.Command("c" + i, new[] { "word" + (char)('a' + i % 26) + (char)('a' + i / 26) + " {number}" }))
            .ToArray();
        var plan = GrammarBuilder.Build("en", cmds, NoApps, null);
        Assert.True(plan.Phrases.Count <= GrammarBuilder.MaxPhrases);
    }
}
