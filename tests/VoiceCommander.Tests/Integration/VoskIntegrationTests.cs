using System.Speech.Synthesis;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Tests.Integration;

/// <summary>
/// Real recognition: speech is synthesized with the Windows TTS voice and fed to Vosk as 16 kHz mono PCM.
/// Runs only when VC_MODEL_EN points at an extracted English Vosk model (otherwise passes trivially).
/// </summary>
public class VoskIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public VoskIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string? ModelPath => Environment.GetEnvironmentVariable("VC_MODEL_EN");

    private static byte[] Synthesize(string text)
    {
        using var ms = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);
        }
        // Convert whatever the voice produced to 16 kHz mono 16-bit using NAudio.
        ms.Position = 0;
        using var reader = new NAudio.Wave.WaveFileReader(ms);
        var fmt = new NAudio.Wave.WaveFormat(16000, 16, 1);
        using var conv = new NAudio.Wave.WaveFormatConversionStream(fmt, reader);
        using var outMs = new MemoryStream();
        conv.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static List<SpeechResult> Run(VoskEngine engine, byte[] pcm)
    {
        var results = new List<SpeechResult>();
        engine.Recognized += results.Add;
        for (int i = 0; i < pcm.Length; i += 1920)
        {
            var n = Math.Min(1920, pcm.Length - i);
            var chunk = new byte[n];
            Array.Copy(pcm, i, chunk, 0, n);
            engine.Feed(chunk, n);
        }
        engine.Feed(new byte[16000 * 2], 16000 * 2);   // trailing silence
        engine.EndUtterance();
        return results;
    }

    [Theory]
    [InlineData("open notepad")]
    [InlineData("volume down")]
    [InlineData("take screenshot")]
    public void FreeForm_recognizes_spoken_phrase(string phrase)
    {
        if (ModelPath is null) return;
        using var engine = new VoskEngine("en");
        engine.Load(ModelPath);
        var results = Run(engine, Synthesize(phrase));
        _out.WriteLine($"free-form '{phrase}' -> [{string.Join(" | ", results.Select(r => r.Text + "@" + r.Confidence.ToString("0.00")))}]");
        Assert.Contains(results, r => r.Text.Contains(phrase.Split(' ')[0]));
    }

    [Fact]
    public void Grammar_constrained_recognition_returns_exact_phrase()
    {
        if (ModelPath is null) return;
        using var engine = new VoskEngine("en");
        engine.Load(ModelPath);
        engine.SetGrammar(new GrammarPlan { Language = "en", Phrases = new[] { "open notepad", "volume down", "take screenshot", "volume up" } });
        var results = Run(engine, Synthesize("volume down"));
        _out.WriteLine($"grammar -> [{string.Join(" | ", results.Select(r => r.Text))}] usesGrammar={engine.UsesGrammar}");
        Assert.True(engine.UsesGrammar);
        Assert.Contains(results, r => r.Text == "volume down");
    }
}
