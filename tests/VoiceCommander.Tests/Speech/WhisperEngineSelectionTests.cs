using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Speech;

public class WhisperEngineSelectionTests
{
    [Fact]
    public void Arabic_defaults_to_whisper_and_english_to_vosk()
    {
        var s = new AppSettings();
        Assert.Equal("whisper", s.ResolveEngine("ar"));
        Assert.Equal("vosk", s.ResolveEngine("en"));
    }

    [Fact]
    public void Explicit_per_language_choice_wins_and_is_case_insensitive()
    {
        var s = new AppSettings();
        s.EngineByLanguage["AR"] = "vosk";
        s.EngineByLanguage["en"] = "whisper";
        Assert.Equal("vosk", s.ResolveEngine("ar"));
        Assert.Equal("whisper", s.ResolveEngine("en"));
    }

    [Fact]
    public void Unknown_engine_ids_fall_back_to_the_language_default()
    {
        var s = new AppSettings();
        s.EngineByLanguage["ar"] = "magic";
        Assert.Equal("whisper", s.ResolveEngine("ar"));
    }

    [Fact]
    public void Legacy_windows_engine_still_applies_when_nothing_explicit_is_set()
    {
        var s = new AppSettings { EngineId = "windows" };
        Assert.Equal("windows", s.ResolveEngine("ar"));
        Assert.Equal("windows", s.ResolveEngine("en"));
    }

    [Fact]
    public void Engine_choice_persists_across_a_restart()
    {
        using var sb = new Sandbox();
        var first = new SettingsService(new NullLog());
        first.Current.EngineByLanguage["ar"] = "vosk";
        first.Current.WhisperModelId = "whisper-base";
        first.Current.WhisperModelPath = @"C:\somewhere\ggml-custom.bin";
        first.Save();

        var second = new SettingsService(new NullLog());
        Assert.Equal("vosk", second.Current.ResolveEngine("ar"));
        Assert.Equal("whisper-base", second.Current.WhisperModelId);
        Assert.Equal(@"C:\somewhere\ggml-custom.bin", second.Current.WhisperModelPath);
    }

    [Fact]
    public void Save_drops_unknown_engines_and_repairs_blank_model_fields()
    {
        using var sb = new Sandbox();
        var svc = new SettingsService(new NullLog());
        svc.Current.EngineByLanguage["ar"] = "bogus";
        svc.Current.EngineByLanguage["en"] = "vosk";
        svc.Current.WhisperModelId = "  ";
        svc.Current.WhisperModelPath = " ";
        svc.Save();

        var reloaded = new SettingsService(new NullLog());
        Assert.False(reloaded.Current.EngineByLanguage.ContainsKey("ar"));
        Assert.Equal("vosk", reloaded.Current.EngineByLanguage["en"]);
        Assert.Equal("whisper-small", reloaded.Current.WhisperModelId);
        Assert.Null(reloaded.Current.WhisperModelPath);
    }

    [Fact]
    public void Whisper_engine_without_a_model_reports_missing_model()
    {
        using var engine = new WhisperEngine("ar", new NullLog());
        var ex = Assert.Throws<EngineLoadException>(() => engine.Load(null));
        Assert.Equal(EngineFailure.MissingModel, ex.Kind);

        var ex2 = Assert.Throws<EngineLoadException>(() => engine.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.bin")));
        Assert.Equal(EngineFailure.MissingModel, ex2.Kind);
    }

    [Fact]
    public void Whisper_engine_with_a_corrupt_file_fails_without_crashing()
    {
        var bad = Path.Combine(Path.GetTempPath(), "vc-corrupt-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(bad, new byte[2048]);
        try
        {
            using var engine = new WhisperEngine("ar", new NullLog());
            var ex = Assert.Throws<EngineLoadException>(() => engine.Load(bad));
            Assert.NotEqual(EngineFailure.MissingModel, ex.Kind);
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void Whisper_engine_declares_no_grammar_and_disposes_twice_safely()
    {
        var engine = new WhisperEngine("ar", new NullLog());
        Assert.Equal("whisper", engine.EngineId);
        Assert.False(engine.UsesGrammar);
        Assert.False(engine.SupportsGrammar);
        Assert.Null(engine.Vocabulary);
        engine.Dispose();
        engine.Dispose();
    }
}
