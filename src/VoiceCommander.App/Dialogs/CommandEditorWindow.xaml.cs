using System.Windows;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Dialogs;

public partial class CommandEditorWindow : WindowBase
{
    private readonly CommandEditorViewModel _vm;

    public CommandEditorWindow(CommandEditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => { if (vm.IsNew) NameBox.Focus(); };
        Closed += (_, _) => vm.Dispose();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.TrySave()) DialogResult = true;
    }

    // The dropdown's search box takes keyboard focus each time the popup opens.
    private void OnSearchLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            tb.Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); System.Windows.Input.Keyboard.Focus(tb); }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
