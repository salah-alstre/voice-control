using VoiceCommander.App.Dialogs;
using VoiceCommander.App.ViewModels;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.Infrastructure;

public interface ICommandEditorService
{
    /// <summary>Opens the editor for a new (null) or existing command. Returns the saved command, or null if cancelled.</summary>
    Task<VoiceCommand?> EditAsync(VoiceCommand? existing, bool workflow = false);
}

public sealed class CommandEditorService : ICommandEditorService
{
    private readonly ICommandRepository _repo;
    private readonly IAppRegistry _apps;
    private readonly IActionHandlerRegistry _registry;
    private readonly ICommandExecutor _executor;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _loc;
    private readonly AppPickerContext _picker;

    public CommandEditorService(ICommandRepository repo, IAppRegistry apps, IActionHandlerRegistry registry,
        ICommandExecutor executor, IDialogService dialogs, ILocalizer loc,
        IIconService icons, IAppEditorService appEditor, IAppLocator locator)
    {
        _repo = repo; _apps = apps; _registry = registry; _executor = executor; _dialogs = dialogs; _loc = loc;
        _picker = new AppPickerContext(apps, icons, appEditor, locator, dialogs);
    }

    public Task<VoiceCommand?> EditAsync(VoiceCommand? existing, bool workflow = false) => UI.InvokeAsync<VoiceCommand?>(() =>
    {
        var vm = new CommandEditorViewModel(existing, _repo, _apps, _registry, _executor, _dialogs, _loc, workflow, _picker);
        var win = new CommandEditorWindow(vm);
        if (_dialogs.Owner is { } owner) win.Owner = owner;
        else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        return win.ShowDialog() == true ? vm.Result : null;
    });
}
