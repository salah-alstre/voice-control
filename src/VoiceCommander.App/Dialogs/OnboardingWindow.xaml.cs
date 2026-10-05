using VoiceCommander.App.Infrastructure;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Dialogs;

public partial class OnboardingWindow : WindowBase
{
    private readonly OnboardingViewModel _vm;

    public OnboardingWindow(OnboardingViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += () => Close();
        // Closing with the X counts as "seen": the guide never nags again, and Settings can reopen it.
        Closing += (_, _) => _vm.MarkSeen();
        Closed += (_, _) => _vm.Dispose();
    }
}
