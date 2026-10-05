namespace VoiceCommander.Core.Speech;

/// <summary>Spoken forms of 0–100 for building recognition grammars (the inverse of <see cref="Matching.NumberParser"/>).</summary>
public static class NumberWords
{
    private static readonly string[] EnUnits =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    };
    private static readonly string[] EnTens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    // Normalized Arabic spellings; the grammar builder maps them to the model's own orthography.
    private static readonly string[] ArUnits = { "صفر", "واحد", "اثنين", "ثلاثه", "اربعه", "خمسه", "سته", "سبعه", "ثمانيه", "تسعه", "عشره" };
    private static readonly string[] ArTens = { "", "", "عشرين", "ثلاثين", "اربعين", "خمسين", "ستين", "سبعين", "ثمانين", "تسعين" };

    public static string Spell(int n, string language)
    {
        n = Math.Clamp(n, 0, 100);
        return language == "ar" ? Arabic(n) : English(n);
    }

    public static IReadOnlyList<string> All(string language) =>
        Enumerable.Range(0, 101).Select(n => Spell(n, language)).ToList();

    private static string English(int n)
    {
        if (n == 100) return "one hundred";
        if (n < 20) return EnUnits[n];
        return n % 10 == 0 ? EnTens[n / 10] : EnTens[n / 10] + " " + EnUnits[n % 10];
    }

    private static string Arabic(int n)
    {
        if (n == 100) return "مئه";
        if (n <= 10) return ArUnits[n];
        if (n == 11) return "احد عشر";
        if (n == 12) return "اثنا عشر";
        if (n < 20) return ArUnits[n - 10] + " عشر";
        return n % 10 == 0 ? ArTens[n / 10] : ArUnits[n % 10] + " و " + ArTens[n / 10];
    }
}
