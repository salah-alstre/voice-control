using System.Globalization;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Matching;

/// <summary>Result of matching recognized text to a registered command.</summary>
/// <param name="Score">Command-match confidence in [0,1] (1.0 for an exact phrase match). Not the speech confidence.</param>
public sealed record MatchResult(
    VoiceCommand Command,
    string MatchedPhrase,
    int? Number,
    AppDefinition? App,
    bool IsExact,
    double Score);

public enum MatchDecision { Exact, Fuzzy, NoMatch, Ambiguous, RejectedDangerous }

/// <summary>Why the matcher decided what it did. Developer logging / debug UI only.</summary>
public sealed record MatchDiagnostics(
    string OriginalText,
    string NormalizedText,
    MatchDecision Decision,
    string Reason,
    string? BestCommandId = null,
    string? BestPhrase = null,
    double BestScore = 0,
    string? SecondBestCommandId = null,
    string? SecondBestPhrase = null,
    double SecondBestScore = 0)
{
    public string Summary =>
        string.Create(CultureInfo.InvariantCulture,
            $"decision={Decision} reason={Reason} best={BestCommandId ?? "-"}({BestScore:0.00}) second={SecondBestCommandId ?? "-"}({SecondBestScore:0.00}) original=\"{OriginalText}\" normalized=\"{NormalizedText}\"");
}

public sealed record MatchEvaluation(MatchResult? Result, MatchDiagnostics Diagnostics)
{
    public bool IsAmbiguous => Diagnostics.Decision == MatchDecision.Ambiguous;
}

public interface ICommandMatcher
{
    /// <summary>Returns the best enabled command for the text, or null. Never executes anything.</summary>
    MatchResult? Match(string recognizedText, IReadOnlyList<VoiceCommand> commands, IReadOnlyList<AppDefinition> apps, double similarityTolerance = 0);

    /// <summary>Same as <see cref="Match"/> but also reports scores, runner-up and the accept/reject reason.</summary>
    MatchEvaluation Evaluate(string recognizedText, IReadOnlyList<VoiceCommand> commands, IReadOnlyList<AppDefinition> apps, double similarityTolerance = 0);
}

/// <summary>
/// Deterministic matcher.
/// 1) Exact: normalized text equals a normalized phrase (placeholders {number}/{app} are bound); most literal words wins.
/// 2) Arabic fuzzy (only when exact fails and the text contains Arabic): token-aware weighted similarity against every
///    concrete phrase (templates expanded with app aliases). A fuzzy match executes only when the best candidate is above
///    <see cref="MinScore"/>, its most important token is clearly recognised, and it beats the best candidate with a
///    DIFFERENT effect by <see cref="MinGap"/>. Shutdown/restart/sign-out/sleep can never win a fuzzy match.
/// 3) Non-Arabic text keeps the legacy optional whole-string tolerance (off by default).
/// </summary>
public sealed class CommandMatcher : ICommandMatcher
{
    public const double MinScore = 0.80;
    public const double MinScoreSingleToken = 0.90;
    public const double MinGap = 0.10;
    /// <summary>The highest-weight (most identifying) candidate token must be recognised at least this well.</summary>
    public const double MinAnchorSimilarity = 0.80;
    private const double MinTokenSimilarity = 0.50;

    public MatchResult? Match(string recognizedText, IReadOnlyList<VoiceCommand> commands, IReadOnlyList<AppDefinition> apps, double similarityTolerance = 0) =>
        Evaluate(recognizedText, commands, apps, similarityTolerance).Result;

    public MatchEvaluation Evaluate(string recognizedText, IReadOnlyList<VoiceCommand> commands, IReadOnlyList<AppDefinition> apps, double similarityTolerance = 0)
    {
        var normalized = TextNormalizer.Normalize(recognizedText);
        if (normalized.Length == 0)
            return new MatchEvaluation(null, new MatchDiagnostics(recognizedText, normalized, MatchDecision.NoMatch, "empty"));

        var withDigits = NumberParser.ReplaceNumberWords(normalized);
        var tokens = TextNormalizer.Tokenize(withDigits);
        var appIndex = BuildAppIndex(apps);
        var arabic = TextNormalizer.ContainsArabic(normalized);

        // ---- 1. exact ----
        MatchResult? exact = null;
        var bestLiterals = -1;
        foreach (var cmd in commands)
        {
            if (!cmd.Enabled) continue;
            foreach (var phrase in cmd.Phrases)
            {
                var pTokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(phrase));
                if (pTokens.Length == 0) continue;
                if (!TryBind(pTokens, tokens, appIndex, out var number, out var app)) continue;
                var literals = pTokens.Count(p => !IsPlaceholder(p));
                if (literals > bestLiterals)
                {
                    exact = new MatchResult(cmd, phrase, number, app, true, 1.0);
                    bestLiterals = literals;
                }
            }
        }

        if (exact != null)
        {
            var second = arabic ? RankFuzzy(tokens, normalized, commands, apps).FirstOrDefault(c => c.Signature != Signature(exact.Command, exact.App, exact.Number)) : null;
            return new MatchEvaluation(exact, new MatchDiagnostics(recognizedText, normalized, MatchDecision.Exact, "exact phrase",
                exact.Command.Id, exact.MatchedPhrase, 1.0, second?.Command.Id, second?.Phrase, second?.Score ?? 0));
        }

        // ---- 3. legacy whole-string tolerance for non-Arabic text ----
        if (!arabic)
        {
            if (similarityTolerance <= 0)
                return new MatchEvaluation(null, new MatchDiagnostics(recognizedText, normalized, MatchDecision.NoMatch, "no exact match"));
            var legacy = LegacyFuzzy(normalized, commands, similarityTolerance);
            return legacy != null
                ? new MatchEvaluation(legacy, new MatchDiagnostics(recognizedText, normalized, MatchDecision.Fuzzy, "legacy similarity",
                    legacy.Command.Id, legacy.MatchedPhrase, legacy.Score))
                : new MatchEvaluation(null, new MatchDiagnostics(recognizedText, normalized, MatchDecision.NoMatch, "no exact match"));
        }

        // ---- 2. Arabic fuzzy ----
        var ranked = RankFuzzy(tokens, normalized, commands, apps);
        if (ranked.Count == 0)
            return new MatchEvaluation(null, new MatchDiagnostics(recognizedText, normalized, MatchDecision.NoMatch, "no candidates"));

        var best = ranked[0];
        var next = ranked.Count > 1 ? ranked[1] : null;
        MatchDiagnostics Diag(MatchDecision d, string reason) => new(recognizedText, normalized, d, reason,
            best.Command.Id, best.Phrase, best.Score, next?.Command.Id, next?.Phrase, next?.Score ?? 0);

        var min = tokens.Length == 1 ? MinScoreSingleToken : MinScore;
        if (best.Score < min)
            return new MatchEvaluation(null, Diag(MatchDecision.NoMatch, $"best score {best.Score:0.00} below {min:0.00}"));
        if (best.AnchorSimilarity < MinAnchorSimilarity)
            return new MatchEvaluation(null, Diag(MatchDecision.NoMatch, $"key word not recognised clearly ({best.AnchorSimilarity:0.00})"));
        if (best.Dangerous)
            return new MatchEvaluation(null, Diag(MatchDecision.RejectedDangerous, "dangerous command is never executed by fuzzy match"));
        if (next != null && best.Score - next.Score < MinGap)
            return new MatchEvaluation(null, Diag(MatchDecision.Ambiguous, $"gap {best.Score - next.Score:0.00} below {MinGap:0.00}"));

        var result = new MatchResult(best.Command, best.Phrase, best.Number, best.App, false, best.Score);
        return new MatchEvaluation(result, Diag(MatchDecision.Fuzzy, "fuzzy match above threshold and clearly better than runner-up"));
    }

    // ------------------------------------------------------------------ fuzzy

    private sealed record Candidate(VoiceCommand Command, string Phrase, int? Number, AppDefinition? App, string Signature,
        bool Dangerous, double Score, double AnchorSimilarity);

    private readonly record struct CTok(string Text, double Weight);

    /// <summary>Best candidate per distinct effect, highest score first.</summary>
    private static List<Candidate> RankFuzzy(string[] inputTokens, string normalized, IReadOnlyList<VoiceCommand> commands, IReadOnlyList<AppDefinition> apps)
    {
        var active = commands.Where(c => c.Enabled || IsDangerous(c)).ToList();
        if (active.Count == 0) return new List<Candidate>();

        var inputStripped = inputTokens.Select(TextNormalizer.StripArticle).ToArray();
        var intTokens = inputTokens.Where(t => int.TryParse(t, out _)).ToList();
        int? number = intTokens.Count == 1 ? int.Parse(intTokens[0]) : null;

        // Inverse document frequency over command vocabulary: words shared by many commands carry little identity.
        var n = (double)active.Count;
        var df = new Dictionary<string, int>();
        foreach (var cmd in active)
        {
            var seen = new HashSet<string>();
            foreach (var p in cmd.Phrases)
                foreach (var t in TextNormalizer.Tokenize(TextNormalizer.Normalize(p)))
                    if (!IsPlaceholder(t)) seen.Add(TextNormalizer.StripArticle(t));
            foreach (var t in seen) df[t] = df.GetValueOrDefault(t) + 1;
        }
        double Idf(string stripped, bool alias) => Math.Log(1 + n / (alias ? 1 : Math.Max(1, df.GetValueOrDefault(stripped, 1))));

        var aliases = new List<(string[] Tokens, AppDefinition App)>();
        foreach (var app in apps)
            foreach (var name in app.Aliases.Prepend(app.Name))
            {
                var t = TextNormalizer.Tokenize(TextNormalizer.Normalize(name));
                if (t.Length > 0 && t.Any(x => TextNormalizer.ContainsArabic(x))) aliases.Add((t, app));
            }

        var simCache = new Dictionary<(string, string), double>();
        double TokenSim(string cand, string input)
        {
            if (simCache.TryGetValue((cand, input), out var v)) return v;
            v = ComputeTokenSimilarity(cand, input);
            simCache[(cand, input)] = v;
            return v;
        }

        var bySignature = new Dictionary<string, Candidate>();
        foreach (var cmd in active)
        {
            var dangerous = IsDangerous(cmd);
            foreach (var phrase in cmd.Phrases)
            {
                var pTokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(phrase));
                if (pTokens.Length == 0) continue;
                var hasNumber = pTokens.Contains("{number}");
                if (hasNumber && number == null) continue;
                var appSlot = Array.IndexOf(pTokens, "{app}");

                IEnumerable<(string[] Alias, AppDefinition? App)> expansions = appSlot < 0
                    ? new (string[], AppDefinition?)[] { (Array.Empty<string>(), null) }
                    : aliases.Select(a => (a.Tokens, (AppDefinition?)a.App));

                foreach (var (alias, app) in expansions)
                {
                    var cts = new List<CTok>();
                    foreach (var t in pTokens)
                    {
                        if (t == "{app}") { foreach (var a in alias) cts.Add(new CTok(a, Idf(TextNormalizer.StripArticle(a), true))); }
                        else if (t == "{number}") cts.Add(new CTok(number!.Value.ToString(CultureInfo.InvariantCulture), 1.0));
                        else cts.Add(new CTok(t, Idf(TextNormalizer.StripArticle(t), false)));
                    }
                    if (!cts.Any(c => TextNormalizer.ContainsArabic(c.Text))) continue;

                    var (score, anchor) = Score(cts, inputTokens, inputStripped, normalized, TokenSim);
                    if (score <= 0.3) continue;
                    var sig = Signature(cmd, app, number);
                    if (bySignature.TryGetValue(sig, out var existing) && existing.Score >= score) continue;
                    bySignature[sig] = new Candidate(cmd, phrase, hasNumber ? number : null, app, sig, dangerous, score, anchor);
                }
            }
        }

        return bySignature.Values.OrderByDescending(c => c.Score).ThenBy(c => c.Command.Id, StringComparer.Ordinal).ToList();
    }

    private static (double Score, double Anchor) Score(List<CTok> cand, string[] input, string[] inputStripped, string normalizedInput,
        Func<string, string, double> tokenSim)
    {
        var used = new bool[input.Length];
        double num = 0, den = 0, anchorSim = 0, anchorW = -1;
        foreach (var ct in cand.OrderByDescending(c => c.Weight))
        {
            var bi = -1; var bs = 0.0;
            for (var i = 0; i < input.Length; i++)
            {
                if (used[i]) continue;
                var s = tokenSim(ct.Text, input[i]);
                if (s > bs) { bs = s; bi = i; }
            }
            if (bi >= 0 && bs >= MinTokenSimilarity) used[bi] = true; else bs = 0;
            num += ct.Weight * bs;
            den += ct.Weight;
            if (ct.Weight > anchorW) { anchorW = ct.Weight; anchorSim = bs; }
        }
        var avgW = den / cand.Count;
        var extras = used.Count(u => !u);
        den += extras * avgW * 0.5;
        var tokenScore = den <= 0 ? 0 : num / den;

        var spaced = string.Join(' ', cand.Select(c => c.Text));
        var whole = Math.Max(StringSimilarity.Levenshtein(spaced, normalizedInput),
            StringSimilarity.Levenshtein(spaced.Replace(" ", ""), normalizedInput.Replace(" ", "")));
        whole = Math.Max(whole, StringSimilarity.JaroWinkler(spaced.Replace(" ", ""), normalizedInput.Replace(" ", "")) - 0.05);

        return (0.6 * tokenScore + 0.4 * whole, anchorSim);
    }

    private static double ComputeTokenSimilarity(string cand, string input)
    {
        if (cand == input) return 1.0;
        var a = TextNormalizer.StripArticle(cand);
        var b = TextNormalizer.StripArticle(input);
        if (a == b) return 0.97;
        // numbers must match exactly
        if (a.All(char.IsDigit) || b.All(char.IsDigit)) return 0;
        var lev = StringSimilarity.Levenshtein(a, b);
        var jw = StringSimilarity.JaroWinkler(a, b);
        var s = 0.5 * lev + 0.5 * jw;
        var (shortS, longS) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shortS.Length >= 4 && longS.StartsWith(shortS, StringComparison.Ordinal) && longS.Length - shortS.Length <= 2)
            s = Math.Max(s, 0.9);
        return Math.Min(s, 0.96);
    }

    // ------------------------------------------------------------------ effects / safety

    /// <summary>Shutdown/restart/sign-out/sleep: never matched by fuzzy logic and never run on weak speech confidence.</summary>
    public static bool IsDangerous(VoiceCommand cmd) =>
        cmd.Actions.Any(a => a.Type is ActionTypes.Shutdown or ActionTypes.Restart or ActionTypes.SignOut or ActionTypes.Sleep);

    private static string Signature(VoiceCommand cmd, AppDefinition? app, int? number = null) =>
        string.Join('|', cmd.Actions.Select(a => a.Type + ":" + string.Join(',',
            a.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value switch
            {
                "{app}" => app?.Id ?? "{app}",
                "{number}" => number?.ToString(CultureInfo.InvariantCulture) ?? "{number}",
                var v => v,
            }))));

    // ------------------------------------------------------------------ legacy + exact helpers

    private static MatchResult? LegacyFuzzy(string normalized, IReadOnlyList<VoiceCommand> commands, double similarityTolerance)
    {
        var threshold = 1.0 - Math.Clamp(similarityTolerance, 0, 0.5);
        double bestScore = 0;
        MatchResult? best = null;
        foreach (var cmd in commands)
        {
            if (!cmd.Enabled) continue;
            foreach (var phrase in cmd.Phrases)
            {
                var np = TextNormalizer.Normalize(phrase);
                if (np.Contains('{')) continue;
                var s = TextNormalizer.Similarity(normalized, np);
                if (s >= threshold && s > bestScore)
                {
                    bestScore = s;
                    best = new MatchResult(cmd, phrase, null, null, false, s);
                }
            }
        }
        return best;
    }

    private static bool IsPlaceholder(string token) => token.Length > 2 && token[0] == '{' && token[^1] == '}';

    private static List<(string[] Tokens, AppDefinition App)> BuildAppIndex(IReadOnlyList<AppDefinition> apps)
    {
        var list = new List<(string[], AppDefinition)>();
        foreach (var app in apps)
        {
            foreach (var name in app.Aliases.Prepend(app.Name))
            {
                var t = TextNormalizer.Tokenize(TextNormalizer.Normalize(name));
                if (t.Length > 0) list.Add((t, app));
            }
        }
        // Longest alias first so "visual studio code" wins over "visual".
        return list.OrderByDescending(x => x.Item1.Length).ToList();
    }

    private static bool TryBind(string[] pattern, string[] input, List<(string[] Tokens, AppDefinition App)> appIndex, out int? number, out AppDefinition? app)
    {
        number = null; app = null;
        int? n = null; AppDefinition? a = null;
        var ok = Bind(0, 0);
        number = n; app = a;
        return ok;

        bool Bind(int pi, int ii)
        {
            if (pi == pattern.Length) return ii == input.Length;
            var p = pattern[pi];
            if (p == "{number}")
            {
                if (ii >= input.Length || !int.TryParse(input[ii], out var v) || v < 0) return false;
                var prev = n; n = v;
                if (Bind(pi + 1, ii + 1)) return true;
                n = prev; return false;
            }
            if (p == "{app}")
            {
                foreach (var (tokens, candidate) in appIndex)
                {
                    if (ii + tokens.Length > input.Length) continue;
                    var same = true;
                    for (var k = 0; k < tokens.Length; k++)
                        if (tokens[k] != input[ii + k]) { same = false; break; }
                    if (!same) continue;
                    var prev = a; a = candidate;
                    if (Bind(pi + 1, ii + tokens.Length)) return true;
                    a = prev;
                }
                return false;
            }
            return ii < input.Length && input[ii] == p && Bind(pi + 1, ii + 1);
        }
    }

    /// <summary>Finds phrases used by more than one enabled command (normalized comparison) for duplicate warnings.</summary>
    public static IReadOnlyList<(string Phrase, IReadOnlyList<VoiceCommand> Commands)> FindDuplicates(IEnumerable<VoiceCommand> commands)
    {
        return commands.Where(c => c.Enabled)
            .SelectMany(c => c.Phrases.Select(p => (Key: TextNormalizer.Normalize(p), Cmd: c)))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key)
            .Select(g => (Phrase: g.Key, Commands: (IReadOnlyList<VoiceCommand>)g.Select(x => x.Cmd).Distinct().ToList()))
            .Where(g => g.Commands.Count > 1)
            .ToList();
    }
}
