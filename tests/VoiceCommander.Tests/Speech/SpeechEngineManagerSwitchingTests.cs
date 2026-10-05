using VoiceCommander.Core.Models;
using VoiceCommander.Core.Speech;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Speech;

public sealed class SpeechEngineManagerSwitchingTests : IDisposable
{
    private readonly MemorySettings _settings = new();
    private readonly FakeEngineFactory _factory = new();
    private readonly FakeVoskModels _vosk = new();
    private readonly FakeWhisperModels _whisper = new();
    private readonly SpeechEngineManager _mgr;

    public SpeechEngineManagerSwitchingTests()
    {
        FakeEngine.ResetCounters();
        _mgr = new SpeechEngineManager(_settings, _vosk, new FakeCommands(), new FakeApps(), new NullLog(),
            _factory.Create, _whisper);
    }

    public void Dispose() => _mgr.Dispose();

    private async Task Use(SpeechProfile profile)
    {
        _settings.Current.SpeechProfile = profile;
        await _mgr.ApplySettingsAsync();
    }

    private LanguageEngineState State(string lang) => _mgr.States.Single(s => s.Language == lang);

    // ---- defaults ---------------------------------------------------------------------------------------

    [Fact]
    public async Task English_profile_uses_vosk()
    {
        await Use(SpeechProfile.English);
        var live = _factory.Live.Single();
        Assert.Equal("vosk", live.EngineId);
        Assert.Equal("en", live.Language);
        Assert.Equal(@"C:\models\vosk-en", live.LoadedPath);
        Assert.Equal(EngineStatus.Ready, State("en").Status);
        Assert.True(_mgr.IsReady);
    }

    [Fact]
    public async Task Arabic_profile_uses_whisper_with_the_whisper_model()
    {
        await Use(SpeechProfile.Arabic);
        var live = _factory.Live.Single();
        Assert.Equal("whisper", live.EngineId);
        Assert.Equal("ar", live.Language);
        Assert.Equal(@"C:\models\ggml-small.bin", live.LoadedPath);
        Assert.Equal("whisper", State("ar").EngineId);
        Assert.Equal(EngineStatus.Ready, State("ar").Status);
    }

    [Fact]
    public async Task Arabic_vosk_stays_selectable_for_comparison()
    {
        _settings.Current.EngineByLanguage["ar"] = "vosk";
        await Use(SpeechProfile.Arabic);
        var live = _factory.Live.Single();
        Assert.Equal("vosk", live.EngineId);
        Assert.Equal(@"C:\models\vosk-ar", live.LoadedPath);
    }

    [Fact]
    public async Task Mixed_profile_runs_vosk_for_english_and_whisper_for_arabic()
    {
        await Use(SpeechProfile.Mixed);
        Assert.Equal(2, _factory.Live.Count);
        Assert.Equal("vosk", _factory.Live.Single(e => e.Language == "en").EngineId);
        Assert.Equal("whisper", _factory.Live.Single(e => e.Language == "ar").EngineId);
    }

    // ---- switching ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Switching_back_and_forth_never_leaves_orphaned_engines()
    {
        for (int i = 0; i < 6; i++)
        {
            await Use(SpeechProfile.English);
            Assert.Equal(1, FakeEngine.Alive);
            Assert.Equal("vosk", _factory.Live.Single().EngineId);

            await Use(SpeechProfile.Arabic);
            Assert.Equal(1, FakeEngine.Alive);
            Assert.Equal("whisper", _factory.Live.Single().EngineId);
        }
        await Use(SpeechProfile.English);
        Assert.Equal(1, FakeEngine.Alive);
        Assert.Equal(13, _factory.Created.Count);   // one fresh engine per switch, every old one disposed
        Assert.All(_factory.Created.Where(e => !_factory.Live.Contains(e)), e => Assert.Equal(1, e.DisposeCalls));
    }

    [Fact]
    public async Task Reapplying_unchanged_settings_does_not_reload_the_model()
    {
        await Use(SpeechProfile.Arabic);
        var engine = _factory.Live.Single();
        await _mgr.ApplySettingsAsync();
        await _mgr.ApplySettingsAsync();
        Assert.Same(engine, _factory.Live.Single());
        Assert.Equal(1, engine.LoadCalls);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public async Task Changing_the_engine_for_a_language_replaces_it()
    {
        await Use(SpeechProfile.Arabic);
        _settings.Current.EngineByLanguage["ar"] = "vosk";
        await _mgr.ApplySettingsAsync();
        Assert.Equal("vosk", _factory.Live.Single().EngineId);
        Assert.Equal(1, FakeEngine.Alive);

        _settings.Current.EngineByLanguage["ar"] = "whisper";
        await _mgr.ApplySettingsAsync();
        Assert.Equal("whisper", _factory.Live.Single().EngineId);
        Assert.Equal(1, FakeEngine.Alive);
    }

    [Fact]
    public async Task A_different_whisper_model_reloads_the_engine()
    {
        await Use(SpeechProfile.Arabic);
        _whisper.Path = @"C:\models\ggml-base.bin";
        _whisper.RaiseChanged();
        await _mgr.ReloadAsync();
        var live = _factory.Live.Single();
        Assert.Equal(@"C:\models\ggml-base.bin", live.LoadedPath);
        Assert.Equal(1, FakeEngine.Alive);
    }

    [Fact]
    public async Task Disposing_the_manager_disposes_every_engine()
    {
        await Use(SpeechProfile.Mixed);
        Assert.Equal(2, FakeEngine.Alive);
        _mgr.Dispose();
        Assert.Equal(0, FakeEngine.Alive);
    }

    // ---- failures -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_whisper_model_is_reported_not_thrown()
    {
        _whisper.Path = null;
        await Use(SpeechProfile.Arabic);
        Assert.Equal(EngineStatus.MissingModel, State("ar").Status);
        Assert.Equal("whisper", State("ar").EngineId);
        Assert.False(_mgr.IsReady);
        Assert.Empty(_factory.Live);   // the failed engine is not left behind
    }

    [Fact]
    public async Task Installing_the_model_later_recovers_without_a_restart()
    {
        _whisper.Path = null;
        await Use(SpeechProfile.Arabic);
        Assert.False(_mgr.IsReady);

        _whisper.Path = @"C:\models\ggml-small.bin";
        _whisper.RaiseChanged();
        await _mgr.ReloadAsync();
        Assert.True(_mgr.IsReady);
        Assert.Equal(EngineStatus.Ready, State("ar").Status);
    }

    [Fact]
    public async Task Whisper_load_failure_marks_the_language_failed_and_cleans_up()
    {
        _factory.FailLoad = (engine, _) => engine == "whisper" ? new EngineLoadException(EngineFailure.Failed, "bad model", null) : null;
        await Use(SpeechProfile.Arabic);
        Assert.Equal(EngineStatus.Failed, State("ar").Status);
        Assert.Equal(0, FakeEngine.Alive);

        // The user can fall back to Vosk and the manager recovers.
        _settings.Current.EngineByLanguage["ar"] = "vosk";
        await _mgr.ApplySettingsAsync();
        Assert.Equal(EngineStatus.Ready, State("ar").Status);
        Assert.Equal(1, FakeEngine.Alive);
    }

    [Fact]
    public async Task An_unexpected_exception_while_loading_does_not_crash_the_manager()
    {
        _factory.FailLoad = (_, _) => new OutOfMemoryException();
        await Use(SpeechProfile.Arabic);
        Assert.Equal(EngineStatus.Failed, State("ar").Status);
        Assert.Equal(0, FakeEngine.Alive);

        _factory.FailLoad = null;
        _whisper.RaiseChanged();
        await _mgr.ReloadAsync();
        Assert.True(_mgr.IsReady);
    }

    // ---- audio / results ----------------------------------------------------------------------------------

    [Fact]
    public async Task Audio_reaches_only_the_live_engine()
    {
        await Use(SpeechProfile.English);
        await Use(SpeechProfile.Arabic);
        var whisper = _factory.Live.Single();
        _mgr.Feed(new byte[3200], 3200);
        await WaitFor(() => whisper.FedBytes == 3200);
        Assert.All(_factory.Created.Where(e => e != whisper), e => Assert.Equal(0, e.FedBytes));
    }

    [Fact]
    public async Task Whisper_result_flows_to_subscribers_with_its_language()
    {
        await Use(SpeechProfile.Arabic);
        var seen = new List<SpeechResult>();
        _mgr.Recognized += seen.Add;

        _factory.Live.Single().Complete("افتح المفكرة", 0.87);

        var r = Assert.Single(seen);
        Assert.Equal("افتح المفكرة", r.Text);
        Assert.Equal("ar", r.Language);
        Assert.Equal(0.87, r.Confidence, 2);
    }

    [Fact]
    public async Task EndUtterance_marks_the_manager_processing_until_the_engine_finishes()
    {
        await Use(SpeechProfile.Arabic);
        var engine = _factory.Live.Single();
        var changes = new List<bool>();
        _mgr.ProcessingChanged += () => changes.Add(_mgr.IsProcessing);

        Assert.False(_mgr.IsProcessing);
        _mgr.EndUtterance();
        Assert.True(_mgr.IsProcessing);               // immediately, before the worker even runs
        await WaitFor(() => engine.EndCalls == 1);
        Assert.True(_mgr.IsProcessing);               // still transcribing: must not report ready

        engine.Complete("افتح الحاسبة");
        Assert.False(_mgr.IsProcessing);
        Assert.Contains(true, changes);
        Assert.False(changes.Last());
    }

    [Fact]
    public async Task Rejected_utterances_are_forwarded_and_clear_processing()
    {
        await Use(SpeechProfile.Arabic);
        var engine = _factory.Live.Single();
        var outcomes = new List<UtteranceOutcome>();
        _mgr.UtteranceRejected += outcomes.Add;

        _mgr.EndUtterance();
        await WaitFor(() => engine.EndCalls == 1);
        engine.Reject(UtteranceOutcome.Empty);

        Assert.Equal(new[] { UtteranceOutcome.Empty }, outcomes);
        Assert.False(_mgr.IsProcessing);

        _mgr.EndUtterance();
        await WaitFor(() => engine.EndCalls == 2);
        engine.Reject(UtteranceOutcome.Failed);
        Assert.Equal(UtteranceOutcome.Failed, outcomes.Last());
        Assert.False(_mgr.IsProcessing);
    }

    [Fact]
    public async Task Audio_and_end_of_utterance_are_ignored_when_nothing_is_loaded()
    {
        _whisper.Path = null;
        await Use(SpeechProfile.Arabic);
        var processing = false;
        _mgr.ProcessingChanged += () => processing = true;

        _mgr.Feed(new byte[3200], 3200);
        _mgr.EndUtterance();

        Assert.False(_mgr.IsProcessing);
        Assert.False(processing);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition(), "condition not reached in time");
    }
}
