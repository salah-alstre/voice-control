using System.Windows;
using VoiceCommander.App.Infrastructure;

namespace VoiceCommander.App.Dialogs;

public partial class ConfirmDialog : WindowBase
{
    public ConfirmDialog(string title, string message, string? yes, string? no, bool danger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = yes ?? LocSource.Instance["common.yes"];
        if (no == null && yes != null && yes == LocSource.Instance["common.ok"])
        {
            NoButton.Visibility = Visibility.Collapsed;
        }
        else NoButton.Content = no ?? LocSource.Instance["common.no"];
        YesButton.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
    }

    /// <summary>0 = cancelled, 1 = the primary (yes) button, 2 = the alternative button.</summary>
    public int Choice { get; private set; }

    /// <summary>Turns the dialog into a three-way choice: primary, alternative, cancel.</summary>
    public void UseAlternative(string text)
    {
        AltButton.Content = text;
        AltButton.Visibility = Visibility.Visible;
    }

    private void OnAlt(object sender, RoutedEventArgs e) { Choice = 2; DialogResult = true; }

    private void OnYes(object sender, RoutedEventArgs e) { Choice = 1; DialogResult = true; }
    private void OnNo(object sender, RoutedEventArgs e) { Choice = 0; DialogResult = false; }
}
