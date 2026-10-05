using System.Threading.Channels;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;

namespace VoiceCommander.Core.Speech;

public interface ISpeechEngineManager : IDisposable
{
    /// <summary>One entry per language the current speech profile needs.</summary>
    IReadOnlyList<LanguageEngineState> States { get; }
    event Action? StatesChanged;
    /// <summary>True when at least one engine is loaded and can take audio.</summary>
    bool IsReady { get; }
    event Action<SpeechResult>? Recognized;
    /// <summary>True while an asynchronous engine (Whisper) is still transcribing a finished utterance.</summary>
    bool IsProcessing { get; }
    event Action? ProcessingChanged;
    /// <summary>An utterance ended without any recognized text (silence, noise, or a transcription failure).</summary>
    event Action<UtteranceOutcome>? UtteranceRejected;

    /// <summary>(Re)loads engines for the current profile. Safe to call repeatedly.</summary>
    Task ReloadAsync();
    /// <summary>Rebuilds the command grammar from the current commands/apps (debounced when triggered by changes).</summary>
    Task RefreshGrammarAsync();
    /// <summary>Reloads engines if engine/profile/model changed, else refreshes the grammar when the wake phrase changed.</summary>
    Task ApplySettingsAsync();
    void Feed(byte[] buffer, int count);
    void EndUtterance();
}

/// <summary>
/// Owns the speech engines (one per language of the active profile) and drives them from a single worker thread,
/// because neither Vosk recognizers nor System.Speech engines are thread-safe. Each language picks its own engine
/// (Arabic defaults to Whisper, English to Vosk; see <see cref="AppSettings.ResolveEngine"/>).
/// </summary>
public sealed class SpeechEngineManager : ISpeechEngineManager
{
    private readonly ISettingsService _settings;
    private readonly ISpeechModelManager _models;
    private readonly IWhisperModelManager? _whisperModels;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly ILogService _log;
    private readonly Func<string, string, ISpeechRecognitionEngine> _factory;

    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>();
    private readonly Thread _worker;
    private readonly List<ISpeechRecognitionEngine> _engines = new();       // worker thread only
    private volatile IAsyncSpeechEngine[] _async = Array.Empty<IAsyncSpeechEngine>();   // snapshot of the async engines in _engines
    private readonly object _stateGate = new();
    private IReadOnlyList<LanguageEngineState> _states = Array.Empty<LanguageEngineState>();
    private volatile bool _ready;
    private volatile bool _disposed;
    private string _loadedSignature = "";
    private string _grammarSignature = "";
    private CancellationTokenSource? _debounce;

    public SpeechEngineManager(
        ISettingsService settings, ISpeechModelManager models, ICommandRepository commands, IAppRegistry apps,
        ILogService? log = null, Func<string, string, ISpeechRecognitionEngine>? factory = null,
        IWhisperModelManager? whisperModels = null)
    {
        _settings = settings;
        _models = models;
        _whisperModels = whisperModels;
        _commands = commands;
        _apps = apps;
        _log = log ?? NullLogService.Instance;
        _factory = factory ?? DefaultFactory;

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VoiceCommander.Speech" };
        _worker.Start();

        _commands.Changed += ScheduleGrammarRefresh;
        _apps.Changed += ScheduleGrammarRefresh;
        _models.Changed += OnModelsChanged;
        if (_whisperModels != null) _whisperModels.Changed += OnModelsChanged;
    }

    public IReadOnlyList<LanguageEngineState> States { get { lock (_stateGate) return _states; } }
    public bool IsReady => _ready;
    public event Action? StatesChanged;
    public event Action<SpeechResult>? Recognized;
    public event Action? ProcessingChanged;
    public event Action<UtteranceOutcome>? UtteranceRejected;

    public bool IsProcessing
    {
        get { foreach (var a in _async) if (a.IsProcessing) return true; return false; }
    }

    private ISpeechRecognitionEngine DefaultFactory(string engineId, string language) => engineId switch
    {
        WindowsSpeechEngine.Id => new WindowsSpeechEngine(language, _log),
        WhisperEngine.Id => new WhisperEngine(language, _log),
        _ => new VoskEngine(language, _log),
    };

    /// <summary>The model file/folder an engine needs for a language; null when the engine has none (Windows) or it is missing.</summary>
    private string? ModelPathFor(string engineId, string language) => engineId switch
    {
        WindowsSpeechEngine.Id => null,
        WhisperEngine.Id => _whisperModels?.ResolveModelPath(),
        _ => _models.ResolveModelPath(language),
    };

    private bool AlwaysListening => _settings.Current.ListeningMode == ListeningMode.AlwaysListening;

    private static string[] LanguagesFor(SpeechProfile profile) => profile switch
    {
        SpeechProfile.Arabic => new[] { "ar" },
        SpeechProfile.Mixed => new[] { "en", "ar" },
        _ => new[] { "en" },
    };

    private string ComputeLoadSignature()
    {
        var s = _settings.Current;
        var langs = string.Join(';', LanguagesFor(s.SpeechProfile).Select(l =>
        {
            var engine = s.ResolveEngine(l);
            return $"{l}={engine}@{ModelPathFor(engine, l) ?? "-"}";
        }));
        return $"{s.SpeechProfile}|{langs}";
    }

    private string ComputeGrammarSignature()
    {
        var s = _settings.Current;
        return s.WakePhraseEnabled ? s.WakePhrase : "";
    }

    // ---- public operations -------------------------------------------------------------------------------

    public Task ReloadAsync()
    {
        _loadedSignature = ComputeLoadSignature();
        _grammarSignature = ComputeGrammarSignature();
        var languages = LanguagesFor(_settings.Current.SpeechProfile);
        var plan = languages.Select(l => (Language: l, EngineId: _settings.Current.ResolveEngine(l))).ToArray();

        // Show "loading" right away; the worker replaces it with the real result.
        SetStates(plan.Select(p => new LanguageEngineState(p.Language, EngineStatus.Loading, null, false, p.EngineId)).ToList());
        _ready = false;
        return Run(() => LoadEngines(plan));
    }

    public Task RefreshGrammarAsync()
    {
        _grammarSignature = ComputeGrammarSignature();
        return Run(ApplyGrammar);
    }

    public void Feed(byte[] buffer, int count)
    {
        if (!_ready || _disposed) return;
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        _queue.Writer.TryWrite(() =>
        {
            SyncEndpointing();
            foreach (var e in _engines) Safe(() => e.Feed(copy, count));
        });
    }

    public void EndUtterance()
    {
        if (!_ready || _disposed) return;
        // Announce the end synchronously so "processing" is true from the moment the key is released, not once the queue catches up.
        foreach (var a in _async) Safe(a.NotifyUtteranceEnding);
        _queue.Writer.TryWrite(() =>
        {
            SyncEndpointing();
            foreach (var e in _engines) Safe(e.EndUtterance);
        });
    }

    // ---- triggers ----------------------------------------------------------------------------------------

    private void OnModelsChanged()
    {
        if (_disposed || ComputeLoadSignature() == _loadedSignature) return;
        _ = ReloadAsync();
    }

    private void ScheduleGrammarRefresh()
    {
        if (_disposed) return;
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _debounce, cts);
        old?.Cancel();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !_disposed) _ = RefreshGrammarAsync();
        }, TaskScheduler.Default);
    }

    /// <summary>Called when settings change: reloads when the engine/profile/model changed, else refreshes the grammar.</summary>
    public Task ApplySettingsAsync()
    {
        if (ComputeLoadSignature() != _loadedSignature) return ReloadAsync();
        if (ComputeGrammarSignature() != _grammarSignature) return RefreshGrammarAsync();
        return Run(SyncEndpointing);
    }

    // ---- worker ------------------------------------------------------------------------------------------

    private Task Run(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed) { tcs.SetResult(); return tcs.Task; }
        _queue.Writer.TryWrite(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { _log.Error(LogChannel.Speech, "Speech worker task failed", ex); tcs.SetResult(); }
        });
        return tcs.Task;
    }

    private void WorkerLoop()
    {
        var reader = _queue.Reader;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (reader.TryRead(out var work)) Safe(work);
        }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "Speech worker stopped", ex); }
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "Speech engine error", ex); }
    }

    /// <summary>Always-listening engines find utterance boundaries themselves; push-to-talk engines wait for the key release.</summary>
    private void SyncEndpointing()
    {
        var auto = AlwaysListening;
        foreach (var a in _async) if (a.AutoEndpoint != auto) a.AutoEndpoint = auto;
    }

    private void DetachEngines()
    {
        _async = Array.Empty<IAsyncSpeechEngine>();
        foreach (var e in _engines) DetachEngine(e);
        foreach (var e in _engines) Safe(e.Dispose);
        _engines.Clear();
    }

    private void DetachEngine(ISpeechRecognitionEngine e)
    {
        e.Recognized -= OnEngineRecognized;
        if (e is IAsyncSpeechEngine a)
        {
            a.ProcessingChanged -= OnEngineProcessingChanged;
            a.UtteranceRejected -= OnEngineRejected;
        }
    }

    private void LoadEngines((string Language, string EngineId)[] plan)
    {
        var hadAsync = _async.Length > 0;
        DetachEngines();

        var states = new List<LanguageEngineState>();
        foreach (var (language, engineId) in plan)
        {
            ISpeechRecognitionEngine? engine = null;
            try
            {
                engine = _factory(engineId, language);
                if (engine is IAsyncSpeechEngine pre) pre.AutoEndpoint = AlwaysListening;
                engine.Load(ModelPathFor(engineId, language));
                engine.Recognized += OnEngineRecognized;
                if (engine is IAsyncSpeechEngine a)
                {
                    a.ProcessingChanged += OnEngineProcessingChanged;
                    a.UtteranceRejected += OnEngineRejected;
                }
                _engines.Add(engine);
                states.Add(new LanguageEngineState(language, EngineStatus.Ready, null, false, engineId));
            }
            catch (EngineLoadException ex)
            {
                engine?.Dispose();
                var status = ex.Kind switch
                {
                    EngineFailure.MissingModel => EngineStatus.MissingModel,
                    EngineFailure.Unavailable => EngineStatus.Unavailable,
                    _ => EngineStatus.Failed,
                };
                _log.Warn(LogChannel.Speech, $"Engine '{engineId}' for '{language}' not loaded: {ex.Message}");
                states.Add(new LanguageEngineState(language, status, ex.Message, false, engineId));
            }
            catch (Exception ex)
            {
                engine?.Dispose();
                _log.Error(LogChannel.Speech, $"Engine '{engineId}' for '{language}' failed to load", ex);
                states.Add(new LanguageEngineState(language, EngineStatus.Failed, ex.Message, false, engineId));
            }
        }

        _async = _engines.OfType<IAsyncSpeechEngine>().ToArray();
        SetStates(states);
        ApplyGrammar();
        _ready = _engines.Count > 0;
        if (hadAsync) RaiseProcessingChanged();   // a disposed async engine may have been mid-utterance
    }

    private void ApplyGrammar()
    {
        var s = _settings.Current;
        var wake = s.WakePhraseEnabled ? s.WakePhrase : null;
        var commands = _commands.ActiveCommands;
        var apps = _apps.Apps;
        foreach (var engine in _engines)
        {
            var plan = GrammarBuilder.Build(engine.Language, commands, apps, engine.Vocabulary, wake);
            Safe(() => engine.SetGrammar(plan));
        }

        // Refresh the "uses grammar" flag shown in the UI.
        var current = States;
        SetStates(current.Select(st =>
        {
            var e = _engines.FirstOrDefault(x => x.Language == st.Language);
            return e == null ? st : st with { UsesGrammar = e.UsesGrammar };
        }).ToList());
    }

    private void OnEngineRecognized(SpeechResult result) => Recognized?.Invoke(result);

    private void OnEngineProcessingChanged() => RaiseProcessingChanged();

    private void RaiseProcessingChanged()
    {
        try { ProcessingChanged?.Invoke(); }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "ProcessingChanged handler failed", ex); }
    }

    private void OnEngineRejected(UtteranceOutcome outcome)
    {
        try { UtteranceRejected?.Invoke(outcome); }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "UtteranceRejected handler failed", ex); }
    }

    private void SetStates(IReadOnlyList<LanguageEngineState> states)
    {
        lock (_stateGate) _states = states;
        StatesChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        _commands.Changed -= ScheduleGrammarRefresh;
        _apps.Changed -= ScheduleGrammarRefresh;
        _models.Changed -= OnModelsChanged;
        if (_whisperModels != null) _whisperModels.Changed -= OnModelsChanged;
        _debounce?.Cancel();
        _queue.Writer.TryWrite(DetachEngines);
        _queue.Writer.TryComplete();
        _worker.Join(TimeSpan.FromSeconds(8));
    }
}
