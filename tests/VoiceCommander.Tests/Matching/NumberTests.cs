using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Tests.Matching;

public class NumberTests
{
    [Theory]
    [InlineData("volume twenty five", "volume 25")]
    [InlineData("volume fifty", "volume 50")]
    [InlineData("set volume to one hundred", "set volume to 100")]
    [InlineData("volume zero", "volume 0")]
    [InlineData("volume 40", "volume 40")]
    public void English_number_words_become_digits(string input, string expected) =>
        Assert.Equal(expected, NumberParser.ReplaceNumberWords(TextNormalizer.Normalize(input)));

    [Fact]
    public void Arabic_number_words_become_digits()
    {
        Assert.Equal("25", NumberParser.ReplaceNumberWords(TextNormalizer.Normalize("خمسة وعشرين")));
        Assert.Equal("13", NumberParser.ReplaceNumberWords(TextNormalizer.Normalize("ثلاثة عشر")));
    }

    [Fact]
    public void Text_without_numbers_is_untouched() =>
        Assert.Equal("open notepad", NumberParser.ReplaceNumberWords("open notepad"));

    [Theory]
    [InlineData("42", 42)]
    [InlineData("seven", 7)]
    [InlineData("ninety nine", 99)]
    public void TryParse_accepts_digits_and_words(string input, int expected)
    {
        Assert.True(NumberParser.TryParse(TextNormalizer.Normalize(input), out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("twelve apples")]
    public void TryParse_rejects_non_numbers(string input) =>
        Assert.False(NumberParser.TryParse(TextNormalizer.Normalize(input), out _));

    [Fact]
    public void Spell_clamps_to_zero_through_one_hundred()
    {
        Assert.Equal(NumberWords.Spell(0, "en"), NumberWords.Spell(-5, "en"));
        Assert.Equal(NumberWords.Spell(100, "en"), NumberWords.Spell(250, "en"));
        Assert.Equal("twenty five", NumberWords.Spell(25, "en"));
        Assert.Equal("one hundred", NumberWords.Spell(100, "en"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void All_returns_every_value_and_each_spelling_parses_back(string language)
    {
        var all = NumberWords.All(language);
        Assert.Equal(101, all.Count);
        for (var n = 0; n <= 100; n++)
        {
            var spelled = TextNormalizer.Normalize(NumberWords.Spell(n, language));
            Assert.True(NumberParser.TryParse(spelled, out var parsed), $"{language} {n} -> '{spelled}' did not parse");
            Assert.Equal(n, parsed);
        }
    }

    [Fact]
    public void Spelled_forms_are_distinct()
    {
        Assert.Equal(101, NumberWords.All("en").Distinct().Count());
        Assert.Equal(101, NumberWords.All("ar").Distinct().Count());
    }
}
