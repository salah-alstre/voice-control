using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Speech;

/// <summary>
/// Turns the enabled commands into the closed list of phrases an engine should listen for. Templates are expanded
/// ({number} → spoken numbers 0–100, {app} → app names and aliases) and every word is checked against the
/// speech model's vocabulary, so the grammar never contains a word the model cannot recognize.
/// </summary>
public static class GrammarBuilder
{
    public const int MaxPhrases = 5000;
    private const int MaxPerTemplate = 3000;

    public static bool IsArabicText(string text)
    {
        foreach (var c in text)
            if (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ') return true;
        return false;
    }

    public static GrammarPlan Build(
        string language,
        IEnumerable<VoiceCommand> commands,
        IEnumerable<AppDefinition> apps,
        ModelVocabulary? vocabulary,
        string? wakePhrase = null)
    {
        var arabic = language == "ar";
        var numbers = NumberWords.All(language);
        var appNames = apps
            .SelectMany(a => a.Aliases.Prepend(a.Name))
            .Select(TextNormalizer.Normalize)
            .Where(n => n.Length > 0 && IsArabicText(n) == arabic)
            .Distinct()
            .ToList();

        var literal = new List<string>();
        var templated = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unavailable = new List<string>();

        foreach (var cmd in commands.Where(c => c.Enabled))
        {
            foreach (var phrase in cmd.Phrases)
            {
                var normalized = TextNormalizer.Normalize(phrase);
                if (normalized.Length == 0) continue;
                if (IsArabicText(normalized.Replace("{number}", "").Replace("{app}", "")) != arabic) continue;

                var isTemplate = normalized.Contains('{');
                var expansions = Expand(TextNormalizer.Tokenize(normalized), language, numbers, appNames);
                var kept = 0;
                foreach (var e in expansions)
                {
                    var resolved = Resolve(e, vocabulary);
                    if (resolved == null) continue;
                    kept++;
                    if (seen.Add(resolved)) (isTemplate ? templated : literal).Add(resolved);
                }
                if (kept == 0) unavailable.Add(phrase);
            }
        }

        var all = literal.Concat(templated).ToList();

        // Wake phrase: alone, and in front of every phrase, so "computer volume 40" works as one utterance.
        var wakeNormalized = TextNormalizer.Normalize(wakePhrase);
        if (wakeNormalized.Length > 0 && IsArabicText(wakeNormalized) == arabic)
        {
            var wake = Resolve(wakeNormalized, vocabulary);
            if (wake == null) unavailable.Add(wakePhrase!);
            else
            {
                var prefixed = new List<string> { wake };
                prefixed.AddRange(all.Select(p => wake + " " + p));
                all.AddRange(prefixed.Where(seen.Add));
            }
        }

        return new GrammarPlan
        {
            Language = language,
            Phrases = all.Count > MaxPhrases ? all.Take(MaxPhrases).ToList() : all,
            Unavailable = unavailable,
        };
    }

    private static List<string> Expand(string[] tokens, string language, IReadOnlyList<string> numbers, IReadOnlyList<string> appNames)
    {
        var results = new List<string> { "" };
        foreach (var token in tokens)
        {
            IReadOnlyList<string> options =
                token == "{number}" ? numbers :
                token == "{app}" ? appNames :
                int.TryParse(token, out var n) && n is >= 0 and <= 100 ? new[] { NumberWords.Spell(n, language) } :
                new[] { token };

            var next = new List<string>(Math.Min(results.Count * options.Count, MaxPerTemplate));
            foreach (var prefix in results)
            {
                foreach (var option in options)
                {
                    next.Add(prefix.Length == 0 ? option : prefix + " " + option);
                    if (next.Count >= MaxPerTemplate) break;
                }
                if (next.Count >= MaxPerTemplate) break;
            }
            results = next;
            if (results.Count == 0) break;
        }
        return results;
    }

    /// <summary>Maps every word to the model's spelling; null when any word is unknown to the model.</summary>
    private static string? Resolve(string phrase, ModelVocabulary? vocabulary)
    {
        if (vocabulary == null) return phrase;
        var tokens = TextNormalizer.Tokenize(phrase);
        var words = new string[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var w = vocabulary.Resolve(tokens[i]);
            if (w == null) return null;
            words[i] = w;
        }
        return string.Join(' ', words);
    }
}
