using System.Windows;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Dialogs;

public partial class AppEditorWindow : WindowBase
{
    private readonly AppEditorViewModel _vm;

    public AppEditorWindow(AppEditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => { if (vm.IsNew && string.IsNullOrEmpty(vm.Name)) NameBox.Focus(); };
        Closed += (_, _) => vm.Dispose();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.TrySave()) DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
