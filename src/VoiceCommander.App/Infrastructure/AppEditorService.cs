using VoiceCommander.App.Dialogs;
using VoiceCommander.App.ViewModels;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.Infrastructure;

public interface IAppEditorService
{
    /// <summary>Opens the editor for a new (null) or existing application. Returns the saved entry, or null if cancelled.</summary>
    Task<AppDefinition?> EditAsync(AppDefinition? existing, string? presetPath = null);
}

public sealed class AppEditorService : IAppEditorService
{
    private readonly IAppRegistry _registry;
    private readonly IAppController _controller;
    private readonly IDialogService _dialogs;
    private readonly IIconService _icons;

    public AppEditorService(IAppRegistry registry, IAppController controller, IDialogService dialogs, IIconService icons)
    { _registry = registry; _controller = controller; _dialogs = dialogs; _icons = icons; }

    public Task<AppDefinition?> EditAsync(AppDefinition? existing, string? presetPath = null) => UI.InvokeAsync<AppDefinition?>(() =>
    {
        var vm = new AppEditorViewModel(existing, presetPath, _registry, _controller, _dialogs, _icons);
        var win = new AppEditorWindow(vm);
        if (_dialogs.Owner is { } owner) win.Owner = owner;
        else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        return win.ShowDialog() == true ? vm.Result : null;
    });
}

