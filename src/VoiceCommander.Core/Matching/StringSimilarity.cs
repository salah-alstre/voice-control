namespace VoiceCommander.Core.Matching;

/// <summary>Small deterministic string-distance helpers used by the fuzzy command matcher.</summary>
public static class StringSimilarity
{
    /// <summary>1 - editDistance / maxLength, in [0,1].</summary>
    public static double Levenshtein(string a, string b)
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

    /// <summary>Jaro-Winkler similarity in [0,1] (prefix scale 0.1, up to 4 prefix characters).</summary>
    public static double JaroWinkler(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;

        var window = Math.Max(Math.Max(a.Length, b.Length) / 2 - 1, 0);
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(b.Length - 1, i + window);
            for (var j = from; j <= to; j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = bMatched[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        var m = (double)matches;
        var jaro = (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3.0;

        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(a.Length, b.Length)); i++)
        {
            if (a[i] != b[i]) break;
            prefix++;
        }
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}
