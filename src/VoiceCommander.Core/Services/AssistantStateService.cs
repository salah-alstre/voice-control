using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.Core.Services;

public enum AssistantPhase { Ready, Listening, Processing, Executing, Success, NoMatch, Error }

/// <summary>Immutable view of what the assistant is doing right now. UI formats the strings (so they follow the app language).</summary>
public sealed record AssistantSnapshot(
    AssistantPhase Phase,
    string HeardText,
    string? CommandName,
    string? TargetName,
    string? TargetIconPath,
    string? Message,
    string? ErrorKey,
    bool CanLocate)
{
    public static readonly AssistantSnapshot Idle = new(AssistantPhase.Ready, "", null, null, null, null, null, false);
}

/// <summary>
/// The single source of truth for "what is Voice Commander doing". It only observes the one listening service and the one
/// recognition pipeline - it opens no microphone and runs no speech engine - and both the dashboard and the floating
/// assistant bind to it. Events may be raised from any thread.
/// </summary>
public interface IAssistantStateService : IDisposable
{
    AssistantSnapshot Current { get; }
    ListeningState ListeningState { get; }
    /// <summary>Raised after every change of <see cref="Current"/>.</summary>
    event Action<AssistantSnapshot>? Changed;

    event Action? ListeningStarted;
    event Action? ListeningStopped;
    event Action<string>? SpeechRecognized;
    event Action<string>? CommandMatched;
    event Action<string>? CommandExecutionStarted;
    event Action<ExecutionReport>? CommandExecutionSucceeded;
    event Action<string>? CommandExecutionFailed;

    void Start();
}

public sealed class AssistantStateService : IAssistantStateService
{
    private static readonly TimeSpan ResultHold = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessingHold = TimeSpan.FromSeconds(2.5);
    /// <summary>Upper bound for "Processing speech..." while an engine is genuinely transcribing (a safety net, not the normal exit).</summary>
    private static readonly TimeSpan EngineProcessingLimit = TimeSpan.FromSeconds(90);

    private readonly IListeningService _listening;
    private readonly IRecognitionPipeline _pipeline;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly IIconService _icons;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private AssistantSnapshot _current = AssistantSnapshot.Idle;
    private ListeningState _lastState;
    private bool _started, _disposed;

    public AssistantStateService(IListeningService listening, IRecognitionPipeline pipeline, ICommandRepository commands,
        IAppRegistry apps, IIconService icons, ILogService log)
    {
        _listening = listening; _pipeline = pipeline; _commands = commands; _apps = apps; _icons = icons; _log = log;
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        _lastState = listening.State;
    }

    public AssistantSnapshot Current { get { lock (_gate) return _current; } }
    public ListeningState ListeningState => _listening.State;

    public event Action<AssistantSnapshot>? Changed;
    public event Action? ListeningStarted;
    public event Action? ListeningStopped;
    public event Action<string>? SpeechRecognized;
    public event Action<string>? CommandMatched;
    public event Action<string>? CommandExecutionStarted;
    public event Action<ExecutionReport>? CommandExecutionSucceeded;
    public event Action<string>? CommandExecutionFailed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _lastState = _listening.State;
            _current = Rest();
        }
        _listening.StateChanged += OnListeningChanged;
        _listening.ProcessingChanged += OnEngineProcessingChanged;
        _listening.UtteranceRejected += OnUtteranceRejected;
        _pipeline.Event += OnPipelineEvent;
    }

    // ---------------------------------------------------------------- engine processing (Whisper)

    private static AssistantSnapshot Processing(string heard = "") => new(AssistantPhase.Processing, heard, null, null, null, null, null, false);

    private void OnEngineProcessingChanged()
    {
        var busy = _listening.IsProcessing;
        lock (_gate)
        {
            if (_disposed || !_started) return;
            if (busy)
            {
                // Only the "waiting for speech" phases turn into Processing; a result still on screen is left alone.
                if (_current.Phase is AssistantPhase.Ready or AssistantPhase.Listening or AssistantPhase.Processing
                    && string.IsNullOrEmpty(_current.HeardText))
                    Set(Processing(), EngineProcessingLimit);
            }
            else if (_current.Phase == AssistantPhase.Processing && string.IsNullOrEmpty(_current.HeardText))
            {
                // The engine is done; its result is on its way through the pipeline (Heard arrives next).
                Set(Processing(), ProcessingHold);
            }
        }
        Flush();
    }

    private void OnUtteranceRejected(UtteranceOutcome outcome)
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
            if (outcome == UtteranceOutcome.Failed)
                Set(new AssistantSnapshot(AssistantPhase.Error, "", null, null, null, null, "speech.transcribe-failed", false), FailureHold);
            else
                Set(new AssistantSnapshot(AssistantPhase.NoMatch, "", null, null, null, null, null, false), ResultHold);
        }
        Flush();
    }

    // ---------------------------------------------------------------- listening

    private void OnListeningChanged()
    {
        var now = _listening.State;
        Action? raise = null;
        lock (_gate)
        {
            var before = _lastState;
            _lastState = now;
            if (now == before) return;

            if (now == ListeningState.Listening)
            {
                raise = () => ListeningStarted?.Invoke();
                Set(new AssistantSnapshot(AssistantPhase.Listening, "", null, null, null, null, null, false), null);
            }
            else if (now == ListeningState.Error)
            {
                if (before == ListeningState.Listening) raise = () => ListeningStopped?.Invoke();
                Set(new AssistantSnapshot(AssistantPhase.Error, "", null, null, null, null, _listening.ErrorKey, false), FailureHold);
            }
            else if (before == ListeningState.Listening)
            {
                raise = () => ListeningStopped?.Invoke();
                // Released the key: the final recognition result may still arrive; fall back to Ready if it never does.
                if (_current.Phase == AssistantPhase.Listening)
                    Set(Processing(), _listening.IsProcessing ? EngineProcessingLimit : ProcessingHold);
            }
            else if (_current.Phase == AssistantPhase.Error)
            {
                Set(Rest(), null);
            }
        }
        Flush();
        Safe(raise);
    }

    // ---------------------------------------------------------------- pipeline

    private void OnPipelineEvent(PipelineEvent e)
    {
        Action? raise = null;
        lock (_gate)
        {
            switch (e.Stage)
            {
                case PipelineStage.Heard:
                    raise = () => SpeechRecognized?.Invoke(e.RecognizedText);
                    Set(new AssistantSnapshot(AssistantPhase.Processing, e.RecognizedText, null, null, null, null, null, false), ProcessingHold);
                    break;

                case PipelineStage.Matched:
                {
                    var (target, icon) = ResolveTarget(e);
                    var name = e.CommandName ?? "";
                    raise = () => { CommandMatched?.Invoke(name); CommandExecutionStarted?.Invoke(name); };
                    Set(new AssistantSnapshot(AssistantPhase.Executing, Heard(e), name, target ?? name, icon, null, null, false), FailureHold);
                    break;
                }

                case PipelineStage.Executed:
                {
                    var (target, icon) = ResolveTarget(e);
                    var r = e.Report;
                    target ??= _current.TargetName ?? e.CommandName;
                    icon ??= _current.TargetIconPath;
                    if (r is { Success: true })
                    {
                        raise = () => CommandExecutionSucceeded?.Invoke(r);
                        Set(new AssistantSnapshot(AssistantPhase.Success, Heard(e), e.CommandName, target, icon, r.Message, null, false), ResultHold);
                    }
                    else
                    {
                        var msg = r?.Message ?? e.Message ?? "";
                        raise = () => CommandExecutionFailed?.Invoke(msg);
                        Set(new AssistantSnapshot(AssistantPhase.Error, Heard(e), e.CommandName, target, icon, msg, null,
                            r?.Recovery is { Kind: RecoveryKind.LocateApp }), FailureHold);
                    }
                    break;
                }

                case PipelineStage.NoCommand:
                case PipelineStage.Ambiguous:
                    Set(new AssistantSnapshot(AssistantPhase.NoMatch, Heard(e), null, null, null, e.Message, null, false), ResultHold);
                    break;

                case PipelineStage.Blocked:
                    Set(new AssistantSnapshot(AssistantPhase.NoMatch, Heard(e), e.CommandName, null, null, e.Message, null, false), ResultHold);
                    break;

                case PipelineStage.WakeArmed:
                    Set(new AssistantSnapshot(AssistantPhase.Ready, e.RecognizedText, null, null, null, e.Message, null, false), ResultHold);
                    break;
            }
        }
        Flush();
        Safe(raise);
    }

    private string Heard(PipelineEvent e) => string.IsNullOrWhiteSpace(e.RecognizedText) ? _current.HeardText : e.RecognizedText;

    /// <summary>The application a command acts on: the app the matcher picked, or the app of the command's first app step.</summary>
    private (string? Name, string? Icon) ResolveTarget(PipelineEvent e)
    {
        try
        {
            AppDefinition? app = null;
            if (!string.IsNullOrEmpty(e.AppId)) app = _apps.FindById(e.AppId);
            if (app == null && !string.IsNullOrEmpty(e.CommandId))
            {
                var cmd = _commands.FindById(e.CommandId);
                var step = cmd?.Actions.FirstOrDefault(a => a.Parameters.ContainsKey("app"));
                var raw = step?.Get("app");
                if (!string.IsNullOrEmpty(raw) && raw != "{app}") app = _apps.FindById(raw) ?? _apps.FindByName(raw);
            }
            if (app == null) return (null, null);
            return (app.Name, _icons.GetIconPath(app));
        }
        catch (Exception ex)
        {
            _log.Warn(LogChannel.App, $"Assistant target lookup failed: {ex.Message}");
            return (null, null);
        }
    }

    // ---------------------------------------------------------------- state plumbing

    /// <summary>What "idle" looks like: Listening while the microphone is open (always-listening mode), else Ready.</summary>
    private AssistantSnapshot Rest() => _lastState switch
    {
        ListeningState.Listening => new AssistantSnapshot(AssistantPhase.Listening, "", null, null, null, null, null, false),
        ListeningState.Error => new AssistantSnapshot(AssistantPhase.Error, "", null, null, null, null, _listening.ErrorKey, false),
        _ => AssistantSnapshot.Idle,
    };

    // Callers hold _gate and call Flush() after releasing it, so handlers never run under the lock.
    private AssistantSnapshot? _note;

    private void Set(AssistantSnapshot next, TimeSpan? holdFor)
    {
        _current = next;
        _note = next;
        if (holdFor is { } t) _timer.Change(t, Timeout.InfiniteTimeSpan);
        else _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Flush()
    {
        AssistantSnapshot? n;
        lock (_gate) { n = _note; _note = null; }
        if (n != null) Safe(() => Changed?.Invoke(n));
    }

    private void OnTimer(object? _)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var rest = Rest();
            if (_current == rest) return;
            Set(rest, null);
        }
        Flush();
    }

    private void Safe(Action? a)
    {
        if (a == null) return;
        try { a(); } catch (Exception ex) { _log.Warn(LogChannel.App, $"Assistant event handler failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        try
        {
            _listening.StateChanged -= OnListeningChanged;
            _listening.ProcessingChanged -= OnEngineProcessingChanged;
            _listening.UtteranceRejected -= OnUtteranceRejected;
            _pipeline.Event -= OnPipelineEvent;
        }
        catch { }
        _timer.Dispose();
    }
}
