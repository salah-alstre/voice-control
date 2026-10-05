namespace VoiceCommander.Core.Matching;

/// <summary>
/// Converts spoken numbers (English and Arabic, 0–100) in already-normalized text into digits so that
/// "volume twenty five" and "volume 25" are the same input to the matcher.
/// </summary>
public static class NumberParser
{
    private static readonly Dictionary<string, int> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        // Arabic (normalized: ة->ه, أ/إ->ا)
        ["صفر"] = 0, ["واحد"] = 1, ["واحده"] = 1, ["اثنان"] = 2, ["اثنين"] = 2, ["اثنتان"] = 2, ["اتنين"] = 2,
        ["ثلاثه"] = 3, ["ثلاث"] = 3, ["تلاته"] = 3, ["اربعه"] = 4, ["اربع"] = 4, ["خمسه"] = 5, ["خمس"] = 5,
        ["سته"] = 6, ["ست"] = 6, ["سبعه"] = 7, ["سبع"] = 7, ["ثمانيه"] = 8, ["ثماني"] = 8, ["تمانيه"] = 8,
        ["تسعه"] = 9, ["تسع"] = 9, ["عشره"] = 10, ["عشر"] = 10, ["احد"] = 11, ["اثنا"] = 12,
    };

    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70,
        ["eighty"] = 80, ["ninety"] = 90,
        ["عشرين"] = 20, ["عشرون"] = 20, ["ثلاثين"] = 30, ["ثلاثون"] = 30, ["اربعين"] = 40, ["اربعون"] = 40,
        ["خمسين"] = 50, ["خمسون"] = 50, ["ستين"] = 60, ["ستون"] = 60, ["سبعين"] = 70, ["سبعون"] = 70,
        ["ثمانين"] = 80, ["ثمانون"] = 80, ["تسعين"] = 90, ["تسعون"] = 90,
    };

    private static readonly HashSet<string> Hundred = new() { "hundred", "مئه", "مايه", "ميه", "مائه" };

    /// <summary>Replaces runs of number words with their digit value. Input must already be normalized.</summary>
    public static string ReplaceNumberWords(string normalized)
    {
        var tokens = TextNormalizer.Tokenize(normalized);
        if (tokens.Length == 0) return normalized;
        var output = new List<string>(tokens.Length);
        var i = 0;
        while (i < tokens.Length)
        {
            if (TryParseRun(tokens, i, out var value, out var consumed))
            {
                output.Add(value.ToString());
                i += consumed;
            }
            else
            {
                output.Add(tokens[i]);
                i++;
            }
        }
        return string.Join(' ', output);
    }

    /// <summary>Parses a standalone value: digits ("25") or number words ("twenty five").</summary>
    public static bool TryParse(string normalized, out int value)
    {
        value = 0;
        var tokens = TextNormalizer.Tokenize(normalized);
        if (tokens.Length == 0) return false;
        if (tokens.Length == 1 && int.TryParse(tokens[0], out value)) return value >= 0;
        if (TryParseRun(tokens, 0, out value, out var consumed) && consumed == tokens.Length) return true;
        value = 0;
        return false;
    }

    private static bool TryParseRun(string[] t, int start, out int value, out int consumed)
    {
        value = 0; consumed = 0;
        var i = start;
        var total = 0;
        var got = false;

        // optional "one hundred" / "hundred"
        if (i < t.Length && Hundred.Contains(t[i])) { total = 100; i++; got = true; }
        else if (i + 1 < t.Length && t[i] == "one" && Hundred.Contains(t[i + 1])) { total = 100; i += 2; got = true; }
        else if (i + 1 < t.Length && Units.TryGetValue(t[i], out var mult) && mult is >= 1 and <= 9 && t[i + 1] == "hundred")
        { total = mult * 100; i += 2; got = true; }

        if (got && total > 100) return Finish(total, i, start, out value, out consumed);

        // Arabic "unit و tens" order: خمسه وعشرين
        if (i < t.Length && Units.TryGetValue(t[i], out var u) && u is >= 1 and <= 9
            && i + 1 < t.Length && t[i + 1].Length > 1 && t[i + 1][0] == 'و' && Tens.TryGetValue(t[i + 1][1..], out var arTen))
            return Finish(total + arTen + u, i + 2, start, out value, out consumed);
        if (i + 2 < t.Length && Units.TryGetValue(t[i], out var u2) && u2 is >= 1 and <= 9
            && t[i + 1] == "و" && Tens.TryGetValue(t[i + 2], out var arTen2))
            return Finish(total + arTen2 + u2, i + 3, start, out value, out consumed);

        // Arabic teens: "ثلاثه عشر" = 13, "احد عشر" = 11, "اثنا عشر" = 12
        if (i + 1 < t.Length && (t[i + 1] == "عشر" || t[i + 1] == "عشره"))
        {
            if (t[i] == "احد") return Finish(total + 11, i + 2, start, out value, out consumed);
            if (t[i] == "اثنا" || t[i] == "اثني") return Finish(total + 12, i + 2, start, out value, out consumed);
            if (Units.TryGetValue(t[i], out var teen) && teen is >= 3 and <= 9)
                return Finish(total + 10 + teen, i + 2, start, out value, out consumed);
        }

        if (i < t.Length && Tens.TryGetValue(t[i], out var ten))
        {
            total += ten; i++; got = true;
            if (i < t.Length && Units.TryGetValue(t[i], out var unit) && unit is >= 1 and <= 9) { total += unit; i++; }
            return Finish(total, i, start, out value, out consumed);
        }
        if (i < t.Length && Units.TryGetValue(t[i], out var plain))
            return Finish(total + plain, i + 1, start, out value, out consumed);

        return got && Finish(total, i, start, out value, out consumed);
    }

    private static bool Finish(int total, int end, int start, out int value, out int consumed)
    {
        value = total;
        consumed = end - start;
        return consumed > 0;
    }
}
