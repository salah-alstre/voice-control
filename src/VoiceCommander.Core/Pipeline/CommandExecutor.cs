using System.Diagnostics;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;

namespace VoiceCommander.Core.Pipeline;

/// <summary>Asks the user to approve a dangerous action. The WPF app supplies a dialog.</summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message);
}

/// <summary>Safe default: nothing is ever confirmed.</summary>
public sealed class DenyConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
}

public sealed record ExecutionReport(
    ExecutionOutcome Outcome,
    string Message,
    RecoveryAction? Recovery,
    string CommandName,
    long DurationMs,
    int StepsCompleted,
    int StepsTotal)
{
    public bool Success => Outcome == ExecutionOutcome.Success;
}

public interface ICommandExecutor
{
    Task<ExecutionReport> ExecuteAsync(VoiceCommand command, int? number, AppDefinition? app, TriggerSource source,
        string recognizedText = "", double confidence = 1.0, CancellationToken ct = default);
}

/// <summary>
/// Runs a registered command's steps in order. Only handlers from the registry are used and only the command's own
/// stored steps are run; recognized text is never executed. Executions are serialized.
/// </summary>
public sealed class CommandExecutor : ICommandExecutor
{
    private readonly IActionHandlerRegistry _handlers;
    private readonly ISettingsService _settings;
    private readonly IHistoryService _history;
    private readonly ILocalizer _loc;
    private readonly IConfirmationService _confirm;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandExecutor(IActionHandlerRegistry handlers, ISettingsService settings, IHistoryService history,
        ILocalizer loc, IConfirmationService confirm, ILogService log)
    {
        _handlers = handlers; _settings = settings; _history = history; _loc = loc; _confirm = confirm; _log = log;
    }

    /// <summary>Built-ins show their localized name; renamed or custom commands (no NameKey) show their own Name.</summary>
    public static string DisplayName(VoiceCommand c, ILocalizer loc) =>
        !string.IsNullOrEmpty(c.NameKey) && loc.Has(c.NameKey) ? loc[c.NameKey]
        : !string.IsNullOrWhiteSpace(c.Name) ? c.Name
        : c.Id;

    /// <summary>Returns a localized reason when the step is switched off by the safety settings, otherwise null.</summary>
    private string? SafetyBlock(ActionStep step)
    {
        var s = _settings.Current;
        bool off = step.Type switch
        {
            ActionTypes.Shutdown => s.DisableShutdownCommands,
            ActionTypes.Restart => s.DisableRestartCommands,
            ActionTypes.SignOut => s.DisableSignOutCommands,
            ActionTypes.Sleep => s.DisableSleepCommands,
            _ => false,
        };
        return off ? _loc.Get("msg.poweroff") : null;
    }

    public async Task<ExecutionReport> ExecuteAsync(VoiceCommand command, int? number, AppDefinition? app, TriggerSource source,
        string recognizedText = "", double confidence = 1.0, CancellationToken ct = default)
    {
        var name = DisplayName(command, _loc);
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        ExecutionReport report;
        try
        {
            report = await RunAsync(command, name, number, app, source, ct, sw).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        _history.Add(new HistoryEntry
        {
            Timestamp = DateTime.Now, RecognizedText = recognizedText, CommandId = command.Id, CommandName = name,
            Outcome = report.Outcome, Message = report.Message, DurationMs = report.DurationMs, Source = source, Confidence = confidence,
        });
        _log.Info(LogChannel.Command, $"[{source}] {command.Id}: {report.Outcome} - {report.Message} ({report.DurationMs} ms)");
        return report;
    }

    private async Task<ExecutionReport> RunAsync(VoiceCommand command, string name, int? number, AppDefinition? app,
        TriggerSource source, CancellationToken ct, Stopwatch sw)
    {
        ExecutionReport Done(ExecutionOutcome o, string msg, int done, RecoveryAction? rec = null) =>
            new(o, msg, rec, name, sw.ElapsedMilliseconds, done, command.Actions.Count);

        if (command.Actions.Count == 0) return Done(ExecutionOutcome.Failed, _loc.Get("msg.noactions"), 0);

        // Validate and safety-check the whole workflow up front so nothing runs half-way because of a blocked step.
        foreach (var step in command.Actions)
        {
            if (_handlers.Find(step.Type) == null)
                return Done(ExecutionOutcome.Failed, _loc.Get("msg.unknownaction", step.Type), 0);
            var block = SafetyBlock(step);
            if (block != null) return Done(ExecutionOutcome.Blocked, block, 0);
        }

        var dangerous = command.Actions.FirstOrDefault(a => _handlers.Find(a.Type)!.Descriptor.IsDangerous);
        if (dangerous != null && _settings.Current.ConfirmDangerousActions)
        {
            var ok = await _confirm.ConfirmAsync(_loc["confirm.title"], _loc.Get("confirm.message", name)).ConfigureAwait(false);
            if (!ok) return Done(ExecutionOutcome.Cancelled, _loc.Get("msg.declined"), 0);
        }

        var ctx = new ActionContext { Command = command, Number = number, App = app, Source = source, Loc = _loc };
        using var timeout = new CancellationTokenSource();
        var secs = _settings.Current.CommandTimeoutSeconds;
        if (secs > 0) timeout.CancelAfter(TimeSpan.FromSeconds(secs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var completed = 0;
        string lastMessage = "";
        foreach (var step in command.Actions)
        {
            try
            {
                var handler = _handlers.Find(step.Type)!;
                var r = await handler.ExecuteAsync(step, ctx, linked.Token).ConfigureAwait(false);
                if (!r.Success) return Done(ExecutionOutcome.Failed, r.Message, completed, r.Recovery);
                lastMessage = r.Message;
                completed++;
            }
            catch (OperationCanceledException)
            {
                return timeout.IsCancellationRequested
                    ? Done(ExecutionOutcome.Failed, _loc.Get("msg.timeout", secs), completed)
                    : Done(ExecutionOutcome.Cancelled, _loc["msg.cancelled"], completed);
            }
            catch (Exception ex)
            {
                _log.Error(LogChannel.Command, $"Step {step.Type} of {command.Id} threw", ex);
                return Done(ExecutionOutcome.Failed, _loc.Get("msg.steperror", ex.Message), completed);
            }
        }

        var summary = command.Actions.Count == 1 && lastMessage.Length > 0 ? lastMessage : _loc.Get("msg.workflowdone", name);
        return Done(ExecutionOutcome.Success, summary, completed);
    }
}
