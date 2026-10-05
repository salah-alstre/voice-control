using System.Net;
using System.Net.Http.Headers;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.Tests.Support;

/// <summary>A speech engine that records its lifecycle. Behaves like Whisper: results arrive after the utterance ends.</summary>
public sealed class FakeEngine : ISpeechRecognitionEngine, IAsyncSpeechEngine
{
    private static int _alive;
    public static int Alive => Volatile.Read(ref _alive);
    public static void ResetCounters() => Volatile.Write(ref _alive, 0);

    private bool _busy;

    public FakeEngine(string engineId, string language) { EngineId = engineId; Language = language; Interlocked.Increment(ref _alive); }

    public string EngineId { get; }
    public string Language { get; }
    public bool UsesGrammar => false;
    public bool SupportsGrammar => false;
    public ModelVocabulary? Vocabulary => null;
    public bool IsProcessing => _busy;
    public bool AutoEndpoint { get; set; }

    public int LoadCalls { get; private set; }
    public string? LoadedPath { get; private set; }
    public int DisposeCalls { get; private set; }
    public int FedBytes { get; private set; }
    public int EndCalls { get; private set; }
    public int GrammarCalls { get; private set; }
    public Exception? LoadFailure { get; set; }

    public event Action<SpeechResult>? Recognized;
    public event Action? ProcessingChanged;
    public event Action<UtteranceOutcome>? UtteranceRejected;

    public void Load(string? modelPath)
    {
        LoadCalls++;
        LoadedPath = modelPath;
        // Like the real Vosk/Whisper engines: no model file, no engine.
        if (modelPath == null && EngineId != "windows") throw new EngineLoadException(EngineFailure.MissingModel, "model missing", null);
        if (LoadFailure != null) throw LoadFailure;
    }

    public void SetGrammar(GrammarPlan plan) => GrammarCalls++;
    public void Feed(byte[] buffer, int count) => FedBytes += count;
    public void NotifyUtteranceEnding() => SetBusy(true);
    public void EndUtterance() { EndCalls++; SetBusy(true); }

    public void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        ProcessingChanged?.Invoke();
    }

    /// <summary>Finishes the pending utterance with a recognized text.</summary>
    public void Complete(string text, double confidence = 0.9)
    {
        Recognized?.Invoke(new SpeechResult(text, confidence, Language));
        SetBusy(false);
    }

    public void Reject(UtteranceOutcome outcome)
    {
        UtteranceRejected?.Invoke(outcome);
        SetBusy(false);
    }

    public void Dispose()
    {
        if (DisposeCalls++ == 0) Interlocked.Decrement(ref _alive);
    }
}

/// <summary>Creates <see cref="FakeEngine"/>s and remembers every one, so tests can assert nothing is orphaned.</summary>
public sealed class FakeEngineFactory
{
    public List<FakeEngine> Created { get; } = new();
    public Func<string, string, Exception?>? FailLoad { get; set; }

    public ISpeechRecognitionEngine Create(string engineId, string language)
    {
        var e = new FakeEngine(engineId, language);
        var failure = FailLoad?.Invoke(engineId, language);
        if (failure != null) e.LoadFailure = failure;
        lock (Created) Created.Add(e);
        return e;
    }

    public IReadOnlyList<FakeEngine> Live { get { lock (Created) return Created.Where(e => e.DisposeCalls == 0).ToList(); } }
}

public sealed class FakeVoskModels : ISpeechModelManager
{
    public Dictionary<string, string?> Paths { get; } = new() { ["en"] = @"C:\models\vosk-en", ["ar"] = @"C:\models\vosk-ar" };
    public IReadOnlyList<SpeechModelInfo> Catalog => Array.Empty<SpeechModelInfo>();
    public event Action? Changed;
    public string? ResolveModelPath(string language) => Paths.TryGetValue(language, out var p) ? p : null;
    public bool IsInstalled(SpeechModelInfo model) => false;
    public string InstallPath(SpeechModelInfo model) => "";
    public Task DownloadAsync(SpeechModelInfo model, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
    public string? ValidateModelFolder(string path) => null;
    public string? UseFolder(string language, string path) => null;
    public void Remove(SpeechModelInfo model) { }
    public void RaiseChanged() => Changed?.Invoke();
}

public sealed class FakeWhisperModels : IWhisperModelManager
{
    private static readonly WhisperModelInfo Info = new("whisper-small", "Small", "ggml-small.bin", "https://example.invalid/x", 1, "00", true);
    public string? Path { get; set; } = @"C:\models\ggml-small.bin";
    public IReadOnlyList<WhisperModelInfo> Catalog { get; } = new[] { Info };
    public event Action? Changed;
    public WhisperModelInfo Selected => Info;
    public string? CustomPath => null;
    public string ModelsDirectory => @"C:\models";
    public string? ResolveModelPath() => Path;
    public bool IsInstalled(WhisperModelInfo model) => Path != null;
    public string InstallPath(WhisperModelInfo model) => Path ?? "";
    public Task DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
    public string? ValidateQuick(string path) => null;
    public Task<string?> ValidateAsync(string path, IProgress<double>? progress, CancellationToken ct) => Task.FromResult<string?>(null);
    public string? UseFile(string path) => null;
    public void ClearCustom() { }
    public void Select(string modelId) { }
    public void Remove(WhisperModelInfo model) { }
    public void RaiseChanged() => Changed?.Invoke();
}

public sealed class FakeCommands : ICommandRepository
{
    public List<VoiceCommand> Items { get; } = new();
    public IReadOnlyList<VoiceCommand> Commands => Items;
    public IReadOnlyList<VoiceCommand> ActiveCommands => Items;
    public IReadOnlyList<CommandPack> Packs => Array.Empty<CommandPack>();
    public event Action? Changed;
    public VoiceCommand? FindById(string? id) => Items.FirstOrDefault(c => c.Id == id);
    public void AddOrUpdate(VoiceCommand command) { Items.RemoveAll(c => c.Id == command.Id); Items.Add(command); Changed?.Invoke(); }
    public bool Remove(string id) => Items.RemoveAll(c => c.Id == id) > 0;
    public void ReplaceAll(IEnumerable<VoiceCommand> commands) { Items.Clear(); Items.AddRange(commands); Changed?.Invoke(); }
    public bool IsPackEnabled(string packId) => true;
    public void SetPackEnabled(string packId, bool enabled) { }
    public void RestoreBuiltIns() { }
    public IReadOnlyList<string> Validate(VoiceCommand command) => Array.Empty<string>();
}

public sealed class FakeApps : IAppRegistry
{
    public List<AppDefinition> Items { get; } = new();
    public IReadOnlyList<AppDefinition> Apps => Items;
    public event Action? Changed;
    public AppDefinition? FindById(string? id) => Items.FirstOrDefault(a => a.Id == id);
    public AppDefinition? FindByName(string spoken) => Items.FirstOrDefault(a => a.Name.Equals(spoken, StringComparison.OrdinalIgnoreCase));
    public void AddOrUpdate(AppDefinition app) { Items.RemoveAll(a => a.Id == app.Id); Items.Add(app); Changed?.Invoke(); }
    public bool Remove(string id) => Items.RemoveAll(a => a.Id == id) > 0;
    public void ReplaceAll(IEnumerable<AppDefinition> apps) { Items.Clear(); Items.AddRange(apps); Changed?.Invoke(); }
}

public sealed class FakeIcons : IIconService
{
    public string? GetIconPath(AppDefinition app) => null;
}

public sealed class FakeListening : IListeningService
{
    public ListeningState State { get; private set; } = ListeningState.Idle;
    public string? ErrorKey { get; set; }
    public bool IsEnabled => true;
    public bool IsProcessing { get; private set; }
    public event Action? StateChanged;
    public event Action<float>? LevelChanged { add { } remove { } }
    public event Action? ProcessingChanged;
    public event Action<UtteranceOutcome>? UtteranceRejected;

    public Task StartAsync() => Task.CompletedTask;
    public void SetEnabled(bool enabled) { }
    public void Toggle() { }
    public void PushToTalkDown() => SetState(ListeningState.Listening);
    public void PushToTalkUp() => SetState(ListeningState.Idle);
    public Task ApplySettingsAsync() => Task.CompletedTask;
    public void Dispose() { }

    public void SetState(ListeningState s) { State = s; StateChanged?.Invoke(); }
    public void SetProcessing(bool busy) { IsProcessing = busy; ProcessingChanged?.Invoke(); }
    public void Reject(UtteranceOutcome o) { UtteranceRejected?.Invoke(o); }
}

public sealed class FakePipeline : IRecognitionPipeline
{
    public event Action<PipelineEvent>? Event;
    public void Raise(PipelineEvent e) => Event?.Invoke(e);
    public Task<PipelineEvent> ProcessAsync(string recognizedText, double confidence, TriggerSource source, CancellationToken ct = default)
        => Task.FromResult(new PipelineEvent(PipelineStage.Heard, recognizedText));
    public Task<PipelineEvent> ProcessAsync(SpeechResult result, TriggerSource source, CancellationToken ct = default)
        => ProcessAsync(result.Text, result.Confidence, source, ct);
    public Task<ExecutionReport?> TestAsync(string commandId, CancellationToken ct = default)
        => Task.FromResult<ExecutionReport?>(null);
}

/// <summary>A scriptable HTTP server: each request gets the next scripted reply (or the last one when the script runs out).</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Reply(HttpStatusCode Status, byte[] Body, long? RangeStart = null, Exception? Throw = null,
        Action<CancellationToken>? OnSend = null);

    private readonly Queue<Reply> _script = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<long?> RangeStarts { get; } = new();
    public void Enqueue(Reply r) => _script.Enqueue(r);

    /// <summary>A server that honours <c>Range</c> requests over a fixed payload.</summary>
    public byte[]? Payload { get; set; }
    public bool IgnoreRange { get; set; }
    public Exception? ThrowOnSend { get; set; }
    public Action? BeforeBody { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        RangeStarts.Add(from);

        if (_script.Count > 0)
        {
            var r = _script.Dequeue();
            r.OnSend?.Invoke(ct);
            if (r.Throw != null) throw r.Throw;
            return Task.FromResult(Build(r.Status, r.Body, r.RangeStart, r.Body.LongLength));
        }
        if (ThrowOnSend != null) throw ThrowOnSend;
        if (Payload == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        BeforeBody?.Invoke();

        if (from is { } start && !IgnoreRange)
        {
            if (start >= Payload.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            var slice = Payload[(int)start..];
            return Task.FromResult(Build(HttpStatusCode.PartialContent, slice, start, Payload.LongLength));
        }
        return Task.FromResult(Build(HttpStatusCode.OK, Payload, null, Payload.LongLength));
    }

    private static HttpResponseMessage Build(HttpStatusCode status, byte[] body, long? rangeStart, long total)
    {
        var msg = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        if (status == HttpStatusCode.PartialContent && rangeStart is { } s)
            msg.Content.Headers.ContentRange = new ContentRangeHeaderValue(s, total - 1, total);
        return msg;
    }
}
