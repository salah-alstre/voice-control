using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Core.Pipeline;

public enum PipelineStage { Heard, Matched, NoCommand, Ambiguous, Blocked, Executed, WakeArmed }

/// <summary>One UI-facing feedback event: HEARD / MATCHED / STATUS / NO COMMAND FOUND.</summary>
public sealed record PipelineEvent(
    PipelineStage Stage,
    string RecognizedText,
    string? CommandName = null,
    string? Message = null,
    ExecutionReport? Report = null,
    TriggerSource Source = TriggerSource.Voice,
    string? CommandId = null,
    string? AppId = null,
    MatchDiagnostics? Diagnostics = null,
    double SpeechConfidence = 0);

public interface IRecognitionPipeline
{
    event Action<PipelineEvent>? Event;
    /// <summary>Feed recognized text. Voice and developer input take the same path; nothing here ever executes text.</summary>
    Task<PipelineEvent> ProcessAsync(string recognizedText, double confidence, TriggerSource source, CancellationToken ct = default);
    /// <summary>Feed a recognizer result with its alternatives; the best matching hypothesis goes through the normal single-text path.</summary>
    Task<PipelineEvent> ProcessAsync(SpeechResult result, TriggerSource source, CancellationToken ct = default);
    /// <summary>Runs a command by id through the executor (the Test button), bypassing recognition and cooldown.</summary>
    Task<ExecutionReport?> TestAsync(string commandId, CancellationToken ct = default);
}

/// <summary>Recognized text -> wake gate -> confidence -> matcher -> cooldown -> executor -> event.</summary>
public sealed class RecognitionPipeline : IRecognitionPipeline
{
    private static readonly TimeSpan WakeWindow = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Arabic speech confidence from Vosk is noisy even when the intent is clear, so a clearly matched (exact or safe fuzzy)
    /// non-dangerous Arabic command may pass with speech confidence between this floor and the user's threshold.
    /// The match decision itself is made independently (CommandMatchConfidence vs SpeechConfidence).
    /// </summary>
    public const double ArabicSpeechFloor = 0.30;

    private readonly ICommandMatcher _matcher;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly ICommandExecutor _executor;
    private readonly ISettingsService _settings;
    private readonly ILocalizer _loc;
    private readonly ILogService _log;
    private readonly IHistoryService _history;
    private readonly CooldownGate _cooldown;
    private DateTime _wakeArmedUntil = DateTime.MinValue;

    public RecognitionPipeline(ICommandMatcher matcher, ICommandRepository commands, IAppRegistry apps, ICommandExecutor executor,
        ISettingsService settings, ILocalizer loc, ILogService log, IHistoryService history, CooldownGate cooldown)
    {
        _matcher = matcher; _commands = commands; _apps = apps; _executor = executor; _settings = settings;
        _loc = loc; _log = log; _history = history; _cooldown = cooldown;
    }

    public event Action<PipelineEvent>? Event;

    private PipelineEvent Emit(PipelineEvent e) { Event?.Invoke(e); return e; }

    public Task<PipelineEvent> ProcessAsync(SpeechResult result, TriggerSource source, CancellationToken ct = default)
    {
        var s = _settings.Current;
        var hypotheses = result.AllHypotheses();
        if (hypotheses.Count <= 1)
            return ProcessAsync(result.Text, result.Confidence, source, ct);

        var commands = CommandsForMatching();
        var choice = CandidateSelector.Choose(result, t => _matcher.Evaluate(StripWake(t, s), commands, _apps.Apps, s.SimilarityTolerance));
        if (choice == null)
        {
            _log.Info(LogChannel.Command, $"Candidates: none of {hypotheses.Count} hypotheses matched a command: " +
                string.Join(" | ", hypotheses.Select(h => $"{h.Source}:\"{h.Text}\"")));
            return ProcessAsync(result.Text, result.Confidence, source, ct);
        }

        _log.Info(LogChannel.Command, $"Candidates ({choice.Considered}): chose {choice.Hypothesis.Source}:\"{choice.Hypothesis.Text}\" - {choice.Reason}" +
            (choice.Hypothesis.Text != result.Text ? $" (top transcript was \"{result.Text}\")" : ""));
        // The audio-level confidence belongs to the utterance, not to which alternative the matcher preferred.
        return ProcessAsync(choice.Hypothesis.Text, result.Confidence, source, ct);
    }

    /// <summary>Removes a leading wake phrase so every hypothesis is matched on the command part only.</summary>
    private static string StripWake(string text, AppSettings s)
    {
        if (!s.WakePhraseEnabled || string.IsNullOrWhiteSpace(s.WakePhrase)) return text;
        var wake = TextNormalizer.Normalize(s.WakePhrase);
        var norm = TextNormalizer.Normalize(text);
        return norm.StartsWith(wake + " ", StringComparison.Ordinal) ? norm[(wake.Length + 1)..] : text;
    }

    public async Task<PipelineEvent> ProcessAsync(string recognizedText, double confidence, TriggerSource source, CancellationToken ct = default)
    {
        var text = (recognizedText ?? "").Trim();
        if (text.Length == 0) return new PipelineEvent(PipelineStage.NoCommand, text, Source: source);
        var s = _settings.Current;
        _log.Info(LogChannel.Speech, $"Heard [{source}] \"{text}\" conf={confidence:0.00}");
        Emit(new PipelineEvent(PipelineStage.Heard, text, Source: source));

        // Optional wake phrase (voice only; never required for developer/test input).
        if (source == TriggerSource.Voice && s.WakePhraseEnabled && !string.IsNullOrWhiteSpace(s.WakePhrase))
        {
            var wake = TextNormalizer.Normalize(s.WakePhrase);
            var norm = TextNormalizer.Normalize(text);
            if (norm == wake)
            {
                _wakeArmedUntil = DateTime.UtcNow + WakeWindow;
                return Emit(new PipelineEvent(PipelineStage.WakeArmed, text, Message: _loc["status.wakearmed"], Source: source));
            }
            if (norm.StartsWith(wake + " ", StringComparison.Ordinal))
                text = norm[(wake.Length + 1)..];
            else if (DateTime.UtcNow <= _wakeArmedUntil)
                _wakeArmedUntil = DateTime.MinValue;
            else
                return Emit(new PipelineEvent(PipelineStage.Blocked, text, Message: _loc["status.wakerequired"], Source: source));
        }

        // Matching happens BEFORE the speech-confidence gate so the two confidences stay independent.
        var evaluation = _matcher.Evaluate(text, CommandsForMatching(), _apps.Apps, s.SimilarityTolerance);
        var diag = evaluation.Diagnostics;
        var match = evaluation.Result;
        _log.Info(LogChannel.Command, $"Match [{source}] speechConf={confidence:0.00} {diag.Summary}");

        if (source == TriggerSource.Voice && confidence < s.ConfidenceThreshold)
        {
            var relaxed = match != null && confidence >= ArabicSpeechFloor
                          && TextNormalizer.ContainsArabic(text) && !CommandMatcher.IsDangerous(match.Command);
            if (!relaxed)
            {
                var low = _loc.Get("status.lowconfidence", (int)Math.Round(confidence * 100));
                _log.Info(LogChannel.Command, $"Rejected: low speech confidence {confidence:0.00} < {s.ConfidenceThreshold:0.00}");
                return Emit(new PipelineEvent(PipelineStage.Blocked, text, Message: low, Source: source, Diagnostics: diag, SpeechConfidence: confidence));
            }
        }

        if (match == null)
        {
            if (evaluation.IsAmbiguous)
            {
                _history.Add(new HistoryEntry { RecognizedText = text, Outcome = ExecutionOutcome.NoMatch, Message = _loc["status.ambiguous"], Source = source, Confidence = confidence });
                return Emit(new PipelineEvent(PipelineStage.Ambiguous, text, Message: _loc["status.ambiguous"], Source: source, Diagnostics: diag, SpeechConfidence: confidence));
            }
            _history.Add(new HistoryEntry { RecognizedText = text, Outcome = ExecutionOutcome.NoMatch, Message = _loc["status.nocommand"], Source = source, Confidence = confidence });
            return Emit(new PipelineEvent(PipelineStage.NoCommand, text, Message: _loc["status.nocommand"], Source: source, Diagnostics: diag, SpeechConfidence: confidence));
        }

        var name = CommandExecutor.DisplayName(match.Command, _loc);
        Emit(new PipelineEvent(PipelineStage.Matched, text, name, Source: source, CommandId: match.Command.Id, AppId: match.App?.Id, Diagnostics: diag, SpeechConfidence: confidence));

        if (source == TriggerSource.Voice && !_cooldown.TryEnter(match.Command.Id, s.CommandCooldownMs))
            return Emit(new PipelineEvent(PipelineStage.Blocked, text, name, _loc["status.cooldown"], Source: source, CommandId: match.Command.Id, AppId: match.App?.Id, Diagnostics: diag, SpeechConfidence: confidence));

        var report = await _executor.ExecuteAsync(match.Command, match.Number, match.App, source, text, confidence, ct).ConfigureAwait(false);
        return Emit(new PipelineEvent(PipelineStage.Executed, text, name, report.Message, report, source, match.Command.Id, match.App?.Id, diag, confidence));
    }

    /// <summary>Active commands plus disabled power commands: the latter can never execute but stop a garbled phrase from fuzzy-matching something else.</summary>
    private IReadOnlyList<VoiceCommand> CommandsForMatching()
    {
        var active = _commands.ActiveCommands;
        var blockers = _commands.Commands.Where(c => !c.Enabled && CommandMatcher.IsDangerous(c)).ToList();
        return blockers.Count == 0 ? active : active.Concat(blockers).ToList();
    }

    public async Task<ExecutionReport?> TestAsync(string commandId, CancellationToken ct = default)
    {
        var cmd = _commands.FindById(commandId);
        if (cmd == null) return null;
        var name = CommandExecutor.DisplayName(cmd, _loc);
        // Template commands are tested with a sample number and the first registered app.
        int? number = cmd.Phrases.Any(p => p.Contains("{number}")) ? 50 : null;
        AppDefinition? app = cmd.Phrases.Any(p => p.Contains("{app}")) ? _apps.Apps.FirstOrDefault() : null;
        Emit(new PipelineEvent(PipelineStage.Matched, name, name, Source: TriggerSource.Test, CommandId: cmd.Id, AppId: app?.Id));
        var report = await _executor.ExecuteAsync(cmd, number, app, TriggerSource.Test, name, 1.0, ct).ConfigureAwait(false);
        Emit(new PipelineEvent(PipelineStage.Executed, name, name, report.Message, report, TriggerSource.Test, cmd.Id, app?.Id));
        return report;
    }
}
