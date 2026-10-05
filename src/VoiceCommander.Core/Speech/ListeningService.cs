using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.Core.Speech;

public enum ListeningState
{
    /// <summary>Listening is switched off.</summary>
    Stopped,
    /// <summary>Ready; the microphone is closed (push-to-talk waiting for the key).</summary>
    Idle,
    /// <summary>The microphone is open and audio is going to the recognizer.</summary>
    Listening,
    /// <summary>Speech models are loading.</summary>
    Loading,
    /// <summary>Something prevents listening; see <see cref="IListeningService.ErrorKey"/>.</summary>
    Error,
}

public interface IListeningService : IDisposable
{
    ListeningState State { get; }
    /// <summary>Localization key describing the current problem when <see cref="State"/> is Error, else null.</summary>
    string? ErrorKey { get; }
    bool IsEnabled { get; }
    event Action? StateChanged;
    /// <summary>Microphone peak level 0..1 while capturing.</summary>
    event Action<float>? LevelChanged;
    /// <summary>True while an engine (Whisper) is still transcribing a finished utterance.</summary>
    bool IsProcessing { get; }
    event Action? ProcessingChanged;
    /// <summary>Raised when an utterance ended without any recognition result (silence, or a recognizer failure).</summary>
    event Action<UtteranceOutcome>? UtteranceRejected;

    /// <summary>Loads the speech engines and, in always-listening mode, opens the microphone.</summary>
    Task StartAsync();
    void SetEnabled(bool enabled);
    void Toggle();
    /// <summary>Starts/stops capture the same way the push-to-talk key does (used by the on-screen button).</summary>
    void PushToTalkDown();
    void PushToTalkUp();
    /// <summary>Re-reads settings: reloads engines/microphone only where something relevant changed.</summary>
    Task ApplySettingsAsync();
}

/// <summary>
/// Connects hotkeys → microphone → speech engines → recognition pipeline. In push-to-talk mode the microphone is
/// only open while the key is held; in always-listening mode it stays open while listening is enabled.
/// </summary>
public sealed class ListeningService : IListeningService
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(400);
    private const int ReleaseTailMs = 250;

    private readonly IMicrophoneService _mic;
    private readonly ISpeechEngineManager _engines;
    private readonly IRecognitionPipeline _pipeline;
    private readonly IHotkeyService _hotkeys;
    private readonly ISettingsService _settings;
    private readonly ICommandMatcher _matcher;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly ILogService _log;

    private readonly object _gate = new();
    private bool _enabled;
    private bool _pttHeld;
    private int _pttGeneration;
    private bool _tailPending;
    private ListeningState _state = ListeningState.Stopped;
    private string? _errorKey;
    private string _micSignature = "";
    private bool _started;
    private bool _disposed;

    // Mixed profile: results from both languages are collected briefly and the best acceptable one is used.
    private readonly List<SpeechResult> _pending = new();
    private Timer? _mergeTimer;

    public ListeningService(
        IMicrophoneService mic, ISpeechEngineManager engines, IRecognitionPipeline pipeline, IHotkeyService hotkeys,
        ISettingsService settings, ICommandMatcher matcher, ICommandRepository commands, IAppRegistry apps, ILogService? log = null)
    {
        _mic = mic;
        _engines = engines;
        _pipeline = pipeline;
        _hotkeys = hotkeys;
        _settings = settings;
        _matcher = matcher;
        _commands = commands;
        _apps = apps;
        _log = log ?? NullLogService.Instance;

        var s = settings.Current;
        _enabled = s.ListeningMode == ListeningMode.PushToTalk || s.ResumeListeningOnStart;
        _micSignature = MicSignature();

        _mic.AudioAvailable += OnAudio;
        _mic.LevelChanged += OnLevel;
        _mic.Failed += OnMicFailed;
        _engines.Recognized += OnRecognized;
        _engines.StatesChanged += OnEngineStatesChanged;
        _engines.ProcessingChanged += OnEngineProcessingChanged;
        _engines.UtteranceRejected += OnEngineUtteranceRejected;
        _hotkeys.PushToTalkPressed += PushToTalkDown;
        _hotkeys.PushToTalkReleased += PushToTalkUp;
        _hotkeys.ToggleListeningPressed += Toggle;
    }

    public ListeningState State { get { lock (_gate) return _state; } }
    public string? ErrorKey { get { lock (_gate) return _errorKey; } }
    public bool IsEnabled { get { lock (_gate) return _enabled; } }
    public event Action? StateChanged;
    public event Action<float>? LevelChanged;
    public bool IsProcessing => _engines.IsProcessing;
    public event Action? ProcessingChanged;
    public event Action<UtteranceOutcome>? UtteranceRejected;

    private void OnEngineProcessingChanged()
    {
        if (_disposed) return;
        ProcessingChanged?.Invoke();
    }

    private void OnEngineUtteranceRejected(UtteranceOutcome outcome)
    {
        if (_disposed) return;
        UtteranceRejected?.Invoke(outcome);
    }

    private bool AlwaysListening => _settings.Current.ListeningMode == ListeningMode.AlwaysListening;
    private string MicSignature() => $"{_settings.Current.ListeningMode}|{_settings.Current.MicrophoneName}";

    // ---- control -----------------------------------------------------------------------------------------

    public async Task StartAsync()
    {
        _started = true;
        // Loads the engines, unless the startup settings pass has already loaded them for the current settings.
        await _engines.ApplySettingsAsync().ConfigureAwait(false);
        Reevaluate();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate) _enabled = enabled;
        _log.Info(LogChannel.Audio, enabled ? "Listening enabled" : "Listening disabled");
        Reevaluate();
    }

    public void Toggle() => SetEnabled(!IsEnabled);

    public void PushToTalkDown()
    {
        if (AlwaysListening || _disposed) return;     // the key does nothing in always-listening mode
        lock (_gate)
        {
            if (!_enabled) return;
            _pttHeld = true;
            _pttGeneration++;
            _tailPending = false;
        }
        Reevaluate();
    }

    public void PushToTalkUp()
    {
        int generation;
        lock (_gate)
        {
            if (!_pttHeld) return;
            _pttHeld = false;
            _tailPending = true;
            generation = ++_pttGeneration;
        }
        // Keep the microphone open a moment so the end of the last word is not cut off.
        _ = Task.Delay(ReleaseTailMs).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_pttGeneration != generation || _pttHeld) return;
                _tailPending = false;
            }
            if (_mic.IsCapturing) _mic.Stop();
            _engines.EndUtterance();
            Reevaluate();
        }, TaskScheduler.Default);
    }

    public async Task ApplySettingsAsync()
    {
        await _engines.ApplySettingsAsync().ConfigureAwait(false);
        var sig = MicSignature();
        if (sig != _micSignature)
        {
            _micSignature = sig;
            if (_mic.IsCapturing) _mic.Stop();
            lock (_gate)
            {
                _pttHeld = false;
                _tailPending = false;
                _errorKey = null;
                // Switching to push-to-talk always leaves listening available; always-listening follows the setting.
                if (_settings.Current.ListeningMode == ListeningMode.PushToTalk) _enabled = true;
            }
        }
        Reevaluate();
    }

    // ---- state machine -----------------------------------------------------------------------------------

    private void OnEngineStatesChanged() => Reevaluate();

    /// <summary>Brings the microphone and the reported state in line with the current settings and flags.</summary>
    private void Reevaluate()
    {
        if (_disposed) return;
        ListeningState state;
        string? error = null;

        var states = _engines.States;
        var anyReady = _engines.IsReady;
        bool enabled, held;
        lock (_gate) { enabled = _enabled; held = _pttHeld || _tailPending; }

        if (!enabled) state = ListeningState.Stopped;
        else if (!_started || states.Any(st => st.Status == EngineStatus.Loading) || (states.Count == 0)) state = ListeningState.Loading;
        else if (!anyReady) { state = ListeningState.Error; error = "listen.engine-not-ready"; }
        else state = ListeningState.Idle;

        var wantMic = enabled && anyReady && (AlwaysListening || held);
        if (!wantMic && _mic.IsCapturing)
            _mic.Stop();

        if (wantMic && !_mic.IsCapturing)
        {
            var key = _mic.Start(string.IsNullOrWhiteSpace(_settings.Current.MicrophoneName) ? null : _settings.Current.MicrophoneName);
            if (key != null)
            {
                _log.Warn(LogChannel.Audio, "Microphone could not start: " + key);
                state = ListeningState.Error;
                error = "mic." + key;
                lock (_gate) _pttHeld = false;
            }
        }
        if (state == ListeningState.Idle && _mic.IsCapturing) state = ListeningState.Listening;

        // A microphone failure reported asynchronously sticks until the user acts again.
        lock (_gate)
        {
            if (state != ListeningState.Error && enabled && _errorKey != null && _errorKey.StartsWith("mic.", StringComparison.Ordinal) && !_mic.IsCapturing && AlwaysListening)
            {
                state = ListeningState.Error;
                error = _errorKey;
            }
            var changed = _state != state || _errorKey != error;
            _state = state;
            _errorKey = error;
            if (!changed) return;
        }
        StateChanged?.Invoke();
    }

    private void OnMicFailed(string key)
    {
        _log.Warn(LogChannel.Audio, "Microphone failure: " + key);
        lock (_gate) { _pttHeld = false; }
        lock (_gate) { _state = ListeningState.Error; _errorKey = "mic." + key; }
        StateChanged?.Invoke();
    }

    // ---- audio & results ---------------------------------------------------------------------------------

    private void OnAudio(byte[] buffer, int count) => _engines.Feed(buffer, count);

    private void OnLevel(float level) => LevelChanged?.Invoke(level);

    private void OnRecognized(SpeechResult result)
    {
        if (_disposed) return;
        if (_settings.Current.SpeechProfile != SpeechProfile.Mixed)
        {
            Dispatch(result);
            return;
        }

        lock (_pending)
        {
            _pending.Add(result);
            _mergeTimer ??= new Timer(_ => FlushPending(), null, MergeWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPending()
    {
        List<SpeechResult> batch;
        lock (_pending)
        {
            // A slower engine (Whisper) is still transcribing the same utterance: keep waiting for its candidate.
            if (!_disposed && _engines.IsProcessing)
            {
                _mergeTimer?.Change(MergeWindow, Timeout.InfiniteTimeSpan);
                return;
            }
            batch = _pending.ToList();
            _pending.Clear();
            _mergeTimer?.Dispose();
            _mergeTimer = null;
        }
        if (batch.Count == 0) return;
        Dispatch(Choose(batch));
    }

    /// <summary>
    /// Both language engines hear every utterance. Prefer the candidate that actually matches a command
    /// (highest confidence first); when none does, the most confident one is reported as "no command found".
    /// </summary>
    private SpeechResult Choose(List<SpeechResult> candidates)
    {
        var ordered = candidates.OrderByDescending(c => c.Confidence).ToList();
        var s = _settings.Current;
        var commands = _commands.ActiveCommands;
        var apps = _apps.Apps;
        foreach (var c in ordered)
        {
            var text = StripWake(c.Text, s);
            if (text == null) return c;                       // the wake phrase on its own
            if (_matcher.Match(text, commands, apps, s.SimilarityTolerance) != null) return c;
            // A near-miss in the top transcript may still be right in one of the alternatives.
            if (c.Alternatives is { Count: > 0 } && CandidateSelector.Choose(c, t => _matcher.Evaluate(StripWake(t, s) ?? t, commands, apps, s.SimilarityTolerance)) != null) return c;
        }
        return ordered[0];
    }

    /// <summary>Removes a leading wake phrase; returns null when the text is only the wake phrase.</summary>
    private static string? StripWake(string text, AppSettings s)
    {
        if (!s.WakePhraseEnabled || string.IsNullOrWhiteSpace(s.WakePhrase)) return text;
        var wake = TextNormalizer.Normalize(s.WakePhrase);
        var norm = TextNormalizer.Normalize(text);
        if (norm == wake) return null;
        return norm.StartsWith(wake + " ", StringComparison.Ordinal) ? norm[(wake.Length + 1)..] : text;
    }

    private void Dispatch(SpeechResult result)
    {
        _ = Task.Run(async () =>
        {
            try { await _pipeline.ProcessAsync(result, TriggerSource.Voice).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(LogChannel.Command, "Recognition pipeline failed", ex); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mic.AudioAvailable -= OnAudio;
        _mic.LevelChanged -= OnLevel;
        _mic.Failed -= OnMicFailed;
        _engines.Recognized -= OnRecognized;
        _engines.StatesChanged -= OnEngineStatesChanged;
        _engines.ProcessingChanged -= OnEngineProcessingChanged;
        _engines.UtteranceRejected -= OnEngineUtteranceRejected;
        _hotkeys.PushToTalkPressed -= PushToTalkDown;
        _hotkeys.PushToTalkReleased -= PushToTalkUp;
        _hotkeys.ToggleListeningPressed -= Toggle;
        lock (_pending) { _mergeTimer?.Dispose(); _mergeTimer = null; }
        if (_mic.IsCapturing) _mic.Stop();
    }
}
