using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Core.Pipeline;

/// <summary>The hypothesis chosen for execution, with how it got there (for logs and tests).</summary>
public sealed record CandidateChoice(SpeechAlternative Hypothesis, MatchEvaluation Evaluation, int Considered, string Reason);

/// <summary>
/// Picks which recognizer hypothesis to run through the pipeline. The decoder only proposes; this deterministic step
/// decides, by asking the matcher about EVERY hypothesis (grammar result, free-form top-1, free-form alternatives).
/// Rules, in order:
/// 1. only hypotheses that match a registered command are candidates; exact beats fuzzy, then higher match score,
///    then the order the recognizer ranked them;
/// 2. candidates for DIFFERENT commands must be clearly separated (exact vs fuzzy, or <see cref="CommandMatcher.MinGap"/>),
///    otherwise nothing is chosen (ambiguity protection) and the caller falls back to the primary text;
/// 3. shutdown / restart / sign-out / sleep need an exact match, and that match must come from the top hypothesis or be
///    confirmed by a second hypothesis. A near-miss alternative can never trigger them (the executor still asks to confirm).
/// </summary>
public static class CandidateSelector
{
    public static CandidateChoice? Choose(SpeechResult result, Func<string, MatchEvaluation> evaluate)
    {
        var hypotheses = result.AllHypotheses();
        var scored = new List<(SpeechAlternative Hyp, MatchEvaluation Eval, int Rank)>();
        for (var i = 0; i < hypotheses.Count; i++)
            scored.Add((hypotheses[i], evaluate(hypotheses[i].Text), i));

        var matches = scored.Where(s => s.Eval.Result != null).ToList();
        if (matches.Count == 0) return null;

        // Dangerous commands: exact AND (top hypothesis OR confirmed by another hypothesis).
        matches = matches.Where(m =>
        {
            var cmd = m.Eval.Result!.Command;
            if (!CommandMatcher.IsDangerous(cmd)) return true;
            if (!m.Eval.Result.IsExact) return false;
            return m.Rank == 0 || matches.Any(o => o.Rank != m.Rank && o.Eval.Result!.IsExact && o.Eval.Result.Command.Id == cmd.Id);
        }).ToList();
        if (matches.Count == 0) return null;

        var ordered = matches
            .OrderByDescending(m => m.Eval.Result!.IsExact)
            .ThenByDescending(m => m.Eval.Result!.Score)
            .ThenBy(m => m.Rank)
            .ToList();

        var best = ordered[0];
        var rival = ordered.FirstOrDefault(o => Signature(o.Eval.Result!) != Signature(best.Eval.Result!));
        if (rival.Eval != null)
        {
            var b = best.Eval.Result!; var r = rival.Eval.Result!;
            var separated = (b.IsExact && !r.IsExact) || b.Score - r.Score >= CommandMatcher.MinGap;
            if (!separated)
            {
                // Two hypotheses name different commands and neither is clearly better: trust only the recognizer's own top choice
                // when it matched something, otherwise refuse.
                var top = ordered.FirstOrDefault(o => o.Rank == 0);
                if (top.Eval == null) return null;
                return new CandidateChoice(top.Hyp, top.Eval, scored.Count, "conflict: kept the recognizer's top hypothesis");
            }
        }

        var why = best.Rank == 0 ? "top hypothesis" : $"alternative #{best.Rank} ({best.Hyp.Source}) matched a registered command";
        return new CandidateChoice(best.Hyp, best.Eval, scored.Count, why);
    }

    private static string Signature(MatchResult r) => $"{r.Command.Id}|{r.App?.Id}|{r.Number}";
}
