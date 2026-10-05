using VoiceCommander.Core.Speech;

namespace VoiceCommander.Tests.Speech;

public class WhisperEnginePrepareTests
{
    private const int Rate = 16000;

    private static byte[] Silence(int ms) => new byte[Rate * 2 * ms / 1000];

    private static byte[] Tone(int ms, double amplitude)
    {
        int n = Rate * ms / 1000;
        var b = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(b.AsSpan(i * 2), v);
        }
        return b;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Empty_and_tiny_buffers_are_rejected()
    {
        Assert.Null(WhisperEngine.Prepare(Array.Empty<byte>(), out _, out _));
        Assert.Null(WhisperEngine.Prepare(new byte[10], out _, out _));
    }

    [Fact]
    public void Pure_silence_is_rejected()
    {
        Assert.Null(WhisperEngine.Prepare(Silence(2000), out var trimmed, out var peak));
        Assert.Equal(0, trimmed);
        Assert.True(peak < 0.008f);
    }

    [Fact]
    public void Very_quiet_noise_is_rejected()
    {
        Assert.Null(WhisperEngine.Prepare(Tone(1500, 0.004), out _, out _));
    }

    [Fact]
    public void A_click_shorter_than_the_minimum_speech_is_rejected()
    {
        var pcm = Concat(Silence(800), Tone(60, 0.5), Silence(800));
        Assert.Null(WhisperEngine.Prepare(pcm, out _, out _));
    }

    [Fact]
    public void Speech_is_trimmed_and_padded_to_at_least_a_second()
    {
        var pcm = Concat(Silence(1500), Tone(500, 0.4), Silence(1500));
        var samples = WhisperEngine.Prepare(pcm, out var trimmedMs, out var peak);

        Assert.NotNull(samples);
        Assert.InRange(trimmedMs, 500, 1100);          // speech plus the padding, not the 3.5 s capture
        Assert.True(samples!.Length >= Rate);          // never shorter than a second
        Assert.True(samples.Length < Rate * 2);
        Assert.True(peak > 0.1f);
    }

    [Fact]
    public void Quiet_speech_is_boosted_but_loud_speech_never_clips()
    {
        var quiet = WhisperEngine.Prepare(Concat(Silence(300), Tone(600, 0.05), Silence(300)), out _, out _);
        Assert.NotNull(quiet);
        Assert.InRange(quiet!.Max(Math.Abs), 0.3f, 0.55f);

        var loud = WhisperEngine.Prepare(Concat(Silence(300), Tone(600, 1.0), Silence(300)), out _, out _);
        Assert.NotNull(loud);
        Assert.True(loud!.Max(Math.Abs) <= 1.0f);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(موسيقى)", "")]
    [InlineData("♪♪", "")]
    [InlineData("*music*", "")]
    [InlineData("   ", "")]
    [InlineData("...", "")]
    [InlineData("اشتركوا في القناة", "")]
    [InlineData("Thanks for watching!", "")]
    [InlineData("افتح المفكرة", "افتح المفكرة")]
    [InlineData("  افتح   الحاسبة  ", "افتح الحاسبة")]
    [InlineData("[موسيقى] ارفع الصوت", "ارفع الصوت")]
    [InlineData("open notepad", "open notepad")]
    public void Clean_strips_noise_markers_and_known_hallucinations(string raw, string expected)
    {
        Assert.Equal(expected, WhisperEngine.Clean(raw));
    }
}
