using System.Windows;
using System.Windows.Controls;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Views;

public partial class MicrophoneView : UserControl
{
    public MicrophoneView()
    {
        InitializeComponent();
        // The level test holds the microphone open, so it must end when the page is left.
        Unloaded += (_, _) => (DataContext as MicrophoneViewModel)?.StopTest();
    }
}
