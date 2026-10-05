using Microsoft.Extensions.DependencyInjection;
using VoiceCommander.App.Dialogs;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Infrastructure;

public interface IOnboardingService
{
    /// <summary>Shows the setup guide modally. A second call while it is open does nothing.</summary>
    Task ShowAsync();
}

public sealed class OnboardingService : IOnboardingService
{
    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private bool _open;

    // IServiceProvider rather than the view model itself: the wizard is created fresh per run.
    public OnboardingService(IServiceProvider services, IDialogService dialogs)
    {
        _services = services; _dialogs = dialogs;
    }

    public Task ShowAsync() => UI.InvokeAsync(() =>
    {
        if (_open) return false;
        _open = true;
        try
        {
            var vm = ActivatorUtilities.CreateInstance<OnboardingViewModel>(_services);
            var win = new OnboardingWindow(vm);
            if (_dialogs.Owner is { } owner) win.Owner = owner;
            else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            win.ShowDialog();
            return true;
        }
        finally { _open = false; }
    });
}
