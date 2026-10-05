using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Tests.Pipeline;

public class CandidateSelectorTests
{
    private readonly IReadOnlyList<VoiceCommand> _commands = BuiltInCommands.Create();
    private readonly IReadOnlyList<AppDefinition> _apps = BuiltInApps.Create();
    private readonly CommandMatcher _matcher = new();

    private MatchEvaluation Eval(string text) => _matcher.Evaluate(text, _commands, _apps);

    private static SpeechResult Result(string top, params string[] alternatives) =>
        new(top, 0.8, "ar", alternatives.Select((a, i) => new SpeechAlternative(a, Math.Max(0.3, 0.7 - 0.15 * (i + 1)), "free")).ToList());

    private CandidateChoice? Choose(string top, params string[] alternatives) =>
        CandidateSelector.Choose(Result(top, alternatives), Eval);

    // ---------- choosing ----------

    [Fact]
    public void Top_hypothesis_that_matches_is_kept()
    {
        var c = Choose("افتح ديسكورد", "افتتح ديسكورد");
        Assert.NotNull(c);
        Assert.Equal("افتح ديسكورد", c!.Hypothesis.Text);
        Assert.Equal("discord.open", c.Evaluation.Result!.Command.Id);
    }

    [Fact]
    public void A_correct_alternative_rescues_a_garbled_top_transcript()
    {
        // The recognizer's #1 is nonsense; its #2 is the registered phrase.
        var c = Choose("كتب كتب", "افتح ديسكورد");
        Assert.NotNull(c);
        Assert.Equal("افتح ديسكورد", c!.Hypothesis.Text);
        Assert.Contains("alternative", c.Reason);
    }

    [Fact]
    public void English_alternative_is_chosen_when_the_top_one_matches_nothing()
    {
        var c = Choose("volume dawn", "volume down");
        Assert.NotNull(c);
        Assert.Equal("volume down", c!.Hypothesis.Text);
    }

    [Fact]
    public void No_candidate_when_nothing_matches()
    {
        Assert.Null(Choose("كتب كتب", "حبيبي يا حبيبي", "ما في شي"));
    }

    [Fact]
    public void Exact_alternative_beats_a_fuzzy_top()
    {
        var top = "افتتاح ديسكورد";
        var fuzzy = Eval(top).Result;
        var c = Choose(top, "افتح ديسكورد");
        Assert.NotNull(c);
        Assert.True(c!.Evaluation.Result!.IsExact);
        if (fuzzy != null) Assert.True(c.Evaluation.Result.Score >= fuzzy.Score);
    }

    // ---------- ambiguity protection ----------

    [Fact]
    public void Conflicting_hypotheses_for_different_commands_do_not_pick_the_wrong_one()
    {
        // Two exact, different commands: neither is better, so the recognizer's own top choice is kept (never the lower-ranked one).
        var c = Choose("volume up", "volume down");
        Assert.NotNull(c);
        Assert.Equal("volume up", c!.Hypothesis.Text);
        Assert.Contains("conflict", c.Reason);
    }

    [Fact]
    public void Conflict_with_an_unmatched_top_hypothesis_refuses_to_choose()
    {
        // Top matches nothing, the two alternatives are equally good but name different commands.
        Assert.Null(Choose("blah blah", "volume up", "volume down"));
    }

    [Fact]
    public void Same_command_in_several_hypotheses_is_not_a_conflict()
    {
        var c = Choose("volume up", "increase volume", "louder");
        Assert.NotNull(c);
        Assert.Equal("audio.up", c!.Evaluation.Result!.Command.Id);
    }

    // ---------- power commands ----------

    private const string ShutdownPhrase = "shut down computer";

    /// <summary>Built-in commands with the power commands switched ON (they ship disabled), as a user who opted in would have them.</summary>
    private IReadOnlyList<VoiceCommand> WithPowerEnabled()
    {
        var list = BuiltInCommands.Create().ToList();
        foreach (var c in list.Where(CommandMatcher.IsDangerous)) c.Enabled = true;
        return list;
    }

    private CandidateChoice? ChoosePowerEnabled(string top, params string[] alternatives)
    {
        var cmds = WithPowerEnabled();
        return CandidateSelector.Choose(Result(top, alternatives), t => _matcher.Evaluate(t, cmds, _apps));
    }

    [Fact]
    public void Power_commands_exist_ship_disabled_and_are_marked_dangerous()
    {
        var dangerous = _commands.Where(CommandMatcher.IsDangerous).ToList();
        Assert.NotEmpty(dangerous);
        Assert.All(dangerous, c => Assert.False(c.Enabled));
    }

    [Fact]
    public void Disabled_power_command_is_never_chosen_even_when_spoken_exactly()
    {
        Assert.Null(Choose(ShutdownPhrase));
        Assert.Null(Choose("blah blah", ShutdownPhrase, ShutdownPhrase + " "));
    }

    [Fact]
    public void Dangerous_command_in_an_alternative_alone_is_never_chosen()
    {
        // top = garbage, only alternative #1 says shutdown: must not be selected.
        Assert.Null(ChoosePowerEnabled("blah blah", ShutdownPhrase));
    }

    [Fact]
    public void Dangerous_command_as_the_top_exact_hypothesis_is_allowed_through_to_the_confirmation_step()
    {
        var c = ChoosePowerEnabled(ShutdownPhrase, "blah blah");
        Assert.NotNull(c);
        Assert.Equal("power.shutdown", c!.Evaluation.Result!.Command.Id);
    }

    [Fact]
    public void Dangerous_command_confirmed_by_two_exact_hypotheses_is_allowed()
    {
        // Same words, different surface text (trailing punctuation) -> two distinct hypotheses that both match exactly.
        var c = ChoosePowerEnabled("blah blah", ShutdownPhrase, ShutdownPhrase + " .");
        Assert.NotNull(c);
        Assert.Equal("power.shutdown", c!.Evaluation.Result!.Command.Id);
    }

    [Fact]
    public void Fuzzy_dangerous_alternative_never_wins()
    {
        // Arabic near-miss of "اغلاق الكمبيوتر": fuzzy matching must never select a power command.
        var c = ChoosePowerEnabled("كتب كتب", "اغلاق الكمبيتور");
        Assert.True(c == null || !CommandMatcher.IsDangerous(c.Evaluation.Result!.Command));
        var c2 = ChoosePowerEnabled("اغلاق الكمبيتور");
        Assert.True(c2 == null || !CommandMatcher.IsDangerous(c2.Evaluation.Result!.Command));
    }

    // ---------- SpeechResult.AllHypotheses ----------

    [Fact]
    public void AllHypotheses_deduplicates_and_skips_blanks()
    {
        var r = new SpeechResult("a", 0.9, "en", new[]
        {
            new SpeechAlternative("a", 0.5, "free"), new SpeechAlternative("", 0.4, "free"), new SpeechAlternative("b", 0.3, "grammar"),
        });
        var all = r.AllHypotheses();
        Assert.Equal(new[] { "a", "b" }, all.Select(h => h.Text));
    }

    // ---------- Vosk JSON parsing ----------

    [Fact]
    public void ParseHypotheses_reads_the_plain_format_with_word_confidences()
    {
        var json = "{\"text\":\"volume down\",\"result\":[{\"word\":\"volume\",\"conf\":1.0,\"start\":0,\"end\":1},{\"word\":\"down\",\"conf\":0.5,\"start\":1,\"end\":2}]}";
        var h = VoskEngine.ParseHypotheses(json, "grammar");
        Assert.Single(h);
        Assert.Equal("volume down", h[0].Text);
        Assert.Equal(0.75, h[0].Confidence, 2);
        Assert.Equal("grammar", h[0].Source);
    }

    [Fact]
    public void ParseHypotheses_reads_the_alternatives_format()
    {
        var json = "{\"alternatives\":[{\"text\":\"افتح ديسكورد\",\"confidence\":250.5},{\"text\":\"افتتاح ديسكورد\",\"confidence\":240.1},{\"text\":\"\",\"confidence\":1}]}";
        var h = VoskEngine.ParseHypotheses(json, "free");
        Assert.Equal(new[] { "افتح ديسكورد", "افتتاح ديسكورد" }, h.Select(x => x.Text));
        // Raw lattice scores are not probabilities: confidences stay in 0..1 and rank 0 is the best.
        Assert.All(h, x => Assert.InRange(x.Confidence, 0, 1));
        Assert.True(h[0].Confidence >= h[1].Confidence);
    }

    [Fact]
    public void ParseHypotheses_drops_unk_and_empty_results()
    {
        Assert.Empty(VoskEngine.ParseHypotheses("{\"text\":\"[unk]\"}", "grammar"));
        Assert.Empty(VoskEngine.ParseHypotheses("{\"text\":\"\"}", "free"));
        Assert.Empty(VoskEngine.ParseHypotheses("{\"alternatives\":[]}", "free"));
    }
}
