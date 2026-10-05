using VoiceCommander.Core.Matching;

namespace VoiceCommander.Tests.Matching;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Open Notepad", "open notepad")]
    [InlineData("  open    notepad  ", "open notepad")]
    [InlineData("OPEN\tNOTEPAD\n", "open notepad")]
    [InlineData("Open, Notepad!", "open notepad")]
    [InlineData("what's up?", "what s up")]
    [InlineData("take-a-screenshot", "take a screenshot")]
    public void English_is_lowercased_trimmed_and_stripped_of_punctuation(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!.,")]
    public void Empty_or_punctuation_only_input_becomes_empty(string? input) =>
        Assert.Equal("", TextNormalizer.Normalize(input));

    [Fact]
    public void Template_braces_survive_normalization()
    {
        Assert.Equal("volume {number}", TextNormalizer.Normalize("Volume {number}"));
        Assert.Equal("{app} volume {number}", TextNormalizer.Normalize("{APP}  Volume {Number}"));
    }

    [Theory]
    [InlineData("أحمد", "احمد")]   // hamza above
    [InlineData("إسلام", "اسلام")] // hamza below
    [InlineData("آلة", "اله")]     // madda + ta marbuta
    [InlineData("مدرسة", "مدرسه")] // ta marbuta -> ha
    [InlineData("على", "علي")]     // alef maqsura -> ya
    public void Arabic_letter_variants_are_unified(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Arabic_diacritics_and_tatweel_are_removed()
    {
        Assert.Equal("مرحبا", TextNormalizer.Normalize("مَرْحَبًا"));
        Assert.Equal("مرحبا", TextNormalizer.Normalize("مــرحبا"));
    }

    [Fact]
    public void Arabic_indic_digits_become_ascii_digits()
    {
        Assert.Equal("الصوت 50", TextNormalizer.Normalize("الصوت ٥٠"));
        Assert.Equal("الصوت 50", TextNormalizer.Normalize("الصوت ۵۰"));
    }

    [Fact]
    public void Arabic_punctuation_is_removed()
    {
        Assert.Equal("افتح المفكره", TextNormalizer.Normalize("افتح، المفكرة؟"));
    }

    [Fact]
    public void Spelling_variants_of_the_same_phrase_normalize_identically()
    {
        Assert.Equal(TextNormalizer.Normalize("افتح المفكرة"), TextNormalizer.Normalize("أفتح  المفكرَة!"));
        Assert.Equal(TextNormalizer.Normalize("إفتح الحاسبة"), TextNormalizer.Normalize("افتح الحاسبه"));
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        foreach (var s in new[] { "Open  Notepad!", "أحمد مدرسة", "Volume {number}", "الصوت ٥٠" })
        {
            var once = TextNormalizer.Normalize(s);
            Assert.Equal(once, TextNormalizer.Normalize(once));
        }
    }

    [Fact]
    public void Tokenize_splits_on_spaces_and_handles_empty()
    {
        Assert.Equal(new[] { "open", "notepad" }, TextNormalizer.Tokenize("open notepad"));
        Assert.Empty(TextNormalizer.Tokenize(""));
    }

    [Fact]
    public void Similarity_is_one_for_equal_zero_for_empty_and_fractional_otherwise()
    {
        Assert.Equal(1.0, TextNormalizer.Similarity("open notepad", "open notepad"));
        Assert.Equal(0.0, TextNormalizer.Similarity("", "open"));
        var s = TextNormalizer.Similarity("kitten", "sitting");
        Assert.InRange(s, 0.01, 0.99);
        Assert.True(TextNormalizer.Similarity("opn notepad", "open notepad") > TextNormalizer.Similarity("close window", "open notepad"));
    }
}
