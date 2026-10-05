using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Actions;

public enum RecoveryKind { None, LocateApp }

/// <summary>Offered to the user when a failure can be fixed in one click (e.g. "Could not open Discord. [Locate Discord]").</summary>
public sealed record RecoveryAction(RecoveryKind Kind, string TargetId, string Label);

public sealed record ActionResult(bool Success, string Message, RecoveryAction? Recovery = null)
{
    public static ActionResult Ok(string message = "") => new(true, message);
    public static ActionResult Fail(string message, RecoveryAction? recovery = null) => new(false, message, recovery);
}

public enum ParameterKind { Text, Number, App, Folder, Url, Hotkey, Choice, Bool }

public sealed record ChoiceOption(string Value, string LabelKey);

/// <summary>Describes one editable field of an action so the macro editor can render it generically.</summary>
public sealed record ParameterDefinition(
    string Key,
    string LabelKey,
    ParameterKind Kind,
    bool Required = true,
    string Default = "",
    IReadOnlyList<ChoiceOption>? Choices = null,
    string? HintKey = null);

public sealed record ActionDescriptor(
    string Type,
    string NameKey,
    string Icon,
    string GroupKey,
    IReadOnlyList<ParameterDefinition> Parameters,
    bool IsDangerous = false);

/// <summary>Per-execution context handed to handlers: the step's template values and shared services.</summary>
public sealed class ActionContext
{
    public required VoiceCommand Command { get; init; }
    /// <summary>Value captured from {number} in the spoken phrase, if any.</summary>
    public int? Number { get; init; }
    /// <summary>App captured from {app} in the spoken phrase, if any.</summary>
    public AppDefinition? App { get; init; }
    public required TriggerSource Source { get; init; }
    public required ILocalizer Loc { get; init; }
}

/// <summary>One action type. Handlers are registered, not switched on, so new action types are additive.</summary>
public interface ICommandActionHandler
{
    ActionDescriptor Descriptor { get; }
    Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext context, CancellationToken ct);
}

public interface IActionHandlerRegistry
{
    IReadOnlyList<ActionDescriptor> Descriptors { get; }
    ICommandActionHandler? Find(string type);
}

public sealed class ActionHandlerRegistry : IActionHandlerRegistry
{
    private readonly Dictionary<string, ICommandActionHandler> _handlers;

    public ActionHandlerRegistry(IEnumerable<ICommandActionHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Descriptor.Type, StringComparer.OrdinalIgnoreCase);
        Descriptors = _handlers.Values.Select(h => h.Descriptor).ToList();
    }

    public IReadOnlyList<ActionDescriptor> Descriptors { get; }
    public ICommandActionHandler? Find(string type) => _handlers.GetValueOrDefault(type);
}

public static class ActionTypes
{
    public const string OpenApp = "app.open";
    public const string CloseApp = "app.close";
    public const string RestartApp = "app.restart";
    public const string FocusApp = "app.focus";
    public const string MinimizeApp = "app.minimize";
    public const string MaximizeApp = "app.maximize";
    public const string OpenFolder = "folder.open";
    public const string OpenUrl = "url.open";
    public const string OpenSettings = "windows.settings";
    public const string VolumeSet = "volume.set";
    public const string VolumeChange = "volume.change";
    public const string Mute = "volume.mute";
    public const string AppVolumeSet = "volume.app.set";
    public const string AppVolumeChange = "volume.app.change";
    public const string AppMute = "volume.app.mute";
    public const string Screenshot = "screenshot";
    public const string Wait = "wait";
    public const string Hotkey = "keyboard.shortcut";
    public const string Notification = "notification";
    public const string Lock = "windows.lock";
    public const string ShowDesktop = "windows.showdesktop";
    public const string Shutdown = "power.shutdown";
    public const string Restart = "power.restart";
    public const string SignOut = "power.signout";
    public const string Sleep = "power.sleep";
}
