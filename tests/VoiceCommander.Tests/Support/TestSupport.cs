using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;

// AppPaths.Root is process-wide, so tests that touch the disk must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VoiceCommander.Tests.Support;

/// <summary>Redirects every on-disk location into a throw-away temp folder for the lifetime of the test.</summary>
public sealed class Sandbox : IDisposable
{
    private readonly string _previous;
    public string Root { get; }

    public Sandbox()
    {
        _previous = AppPaths.Root;
        Root = Path.Combine(Path.GetTempPath(), "vc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        AppPaths.Root = Root;
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

public sealed class NullLog : ILogService
{
    public event Action<LogChannel, string>? Written;
    public void Info(LogChannel channel, string message) => Written?.Invoke(channel, message);
    public void Warn(LogChannel channel, string message) => Written?.Invoke(channel, message);
    public void Error(LogChannel channel, string message, Exception? ex = null) => Written?.Invoke(channel, message);
}

public sealed class MemorySettings : ISettingsService
{
    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;
    public void Save() => Changed?.Invoke();
    public void Replace(AppSettings settings) { Current = settings; Changed?.Invoke(); }
}

public sealed class MemoryHistory : IHistoryService
{
    private readonly List<HistoryEntry> _entries = new();
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public event Action<HistoryEntry>? Added;
    public event Action? Cleared;
    public void Add(HistoryEntry entry) { _entries.Add(entry); Added?.Invoke(entry); }
    public void Clear() { _entries.Clear(); Cleared?.Invoke(); }
}

public sealed class FakeConfirm : Core.Pipeline.IConfirmationService
{
    public bool Answer { get; set; }
    public int Asked { get; private set; }
    public Task<bool> ConfirmAsync(string title, string message) { Asked++; return Task.FromResult(Answer); }
}

/// <summary>A scriptable action handler that records every call so tests can assert on order and arguments.</summary>
public sealed class FakeHandler : ICommandActionHandler
{
    private readonly Func<ActionStep, ActionContext, CancellationToken, Task<ActionResult>> _run;

    public FakeHandler(string type, bool dangerous = false, Func<ActionStep, ActionContext, CancellationToken, Task<ActionResult>>? run = null)
    {
        Descriptor = new ActionDescriptor(type, "test." + type, "", "test", Array.Empty<ParameterDefinition>(), dangerous);
        _run = run ?? ((_, _, _) => Task.FromResult(ActionResult.Ok(type + " done")));
    }

    public ActionDescriptor Descriptor { get; }
    public List<ActionStep> Calls { get; } = new();
    public List<ActionContext> Contexts { get; } = new();

    public Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext context, CancellationToken ct)
    {
        Calls.Add(step);
        Contexts.Add(context);
        return _run(step, context, ct);
    }
}

public static class Make
{
    public static VoiceCommand Command(string id, string[] phrases, params string[] actionTypes) => new()
    {
        Id = id,
        Name = id,
        Phrases = phrases.ToList(),
        Actions = (actionTypes.Length == 0 ? new[] { "wait" } : actionTypes).Select(t => new ActionStep(t)).ToList(),
    };

    public static AppDefinition App(string id, string name, params string[] aliases) => new()
    {
        Id = id, Name = name, ExecutablePath = name + ".exe", ProcessName = name, Aliases = aliases.ToList(),
    };
}
