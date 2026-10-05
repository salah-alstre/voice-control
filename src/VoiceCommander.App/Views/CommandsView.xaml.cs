using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VoiceCommander.App.Views;

public partial class CommandsView : UserControl
{
    public CommandsView() => InitializeComponent();

    // The "more" button opens its context menu on a normal left click.
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
