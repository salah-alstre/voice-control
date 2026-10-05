using System.Text;

namespace VoiceCommander.Core.Matching;

/// <summary>
/// Deterministic text normalization shared by recognition output and stored phrases, so both sides compare equal:
/// lowercase, punctuation removed, whitespace collapsed, Arabic letters/digits unified.
/// Placeholder braces ({number}, {app}) are preserved so templates can be normalized too.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var raw in text.Trim())
        {
            var c = MapChar(raw);
            if (c == '\0') continue;                       // dropped (diacritics, tatweel)
            if (char.IsWhiteSpace(c) || IsPunctuation(c))
            {
                if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static bool IsPunctuation(char c) =>
        c != '{' && c != '}' && (char.IsPunctuation(c) || char.IsSymbol(c) || c == '،' || c == '؛' || c == '؟');

    private static char MapChar(char c)
    {
        // Arabic diacritics (tashkeel), superscript alef, tatweel
        if ((c >= 'ً' && c <= 'ٟ') || c == 'ٰ' || c == 'ـ') return '\0';
        switch (c)
        {
            case 'أ': case 'إ': case 'آ': case 'ٱ': return 'ا';
            case 'ى': return 'ي';
            case 'ة': return 'ه';
            case 'ؤ': return 'و';
            case 'ئ': return 'ي';
            case 'گ': return 'ك';
            case 'ک': return 'ك';
            case 'ی': return 'ي';
        }
        if (c >= '٠' && c <= '٩') return (char)('0' + (c - '٠'));   // Arabic-Indic digits
        if (c >= '۰' && c <= '۹') return (char)('0' + (c - '۰'));   // Extended Arabic-Indic digits
        return char.ToLowerInvariant(c);
    }

    /// <summary>True when the text contains at least one Arabic-script letter.</summary>
    public static bool ContainsArabic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if ((c >= '؀' && c <= 'ۿ') || (c >= 'ݐ' && c <= 'ݿ')) return true;
        return false;
    }

    /// <summary>Strips a leading definite article (ال) from a normalized Arabic token when enough letters remain.</summary>
    public static string StripArticle(string token) =>
        token.Length > 4 && token.StartsWith("ال", StringComparison.Ordinal) ? token[2..] : token;

    public static string[] Tokenize(string normalized) =>
        normalized.Length == 0 ? Array.Empty<string>() : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Levenshtein-based similarity in [0,1]; used only when the user enables similarity tolerance.</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}
