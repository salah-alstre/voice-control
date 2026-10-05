using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Views;

public partial class DashboardView : UserControl
{
    private bool _talking;
    private DashboardViewModel? _vm;
    private Storyboard? _wave;
    private bool _waveRunning;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => SyncWave();
        Unloaded += (_, _) => StopWave();
    }

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = Vm;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        SyncWave();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.CardListening)) SyncWave();
    }

    // The equalizer animation runs only while the card shows "Listening" and the page is on screen.
    private void SyncWave()
    {
        if (_vm?.CardListening == true && IsLoaded) StartWave(); else StopWave();
    }

    private void StartWave()
    {
        if (_waveRunning) return;
        _wave ??= (Storyboard)Resources["WaveStoryboard"];
        _wave.Begin(this, true);
        _waveRunning = true;
    }

    private void StopWave()
    {
        if (!_waveRunning) return;
        _wave?.Stop(this);
        _waveRunning = false;
    }

    // Push-to-talk by mouse: press and hold the button, release to stop.
    private void OnTalkDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null || _talking) return;
        _talking = true;
        Mouse.Capture(TalkButton);
        Vm.PushToTalkDown();
    }

    private void OnTalkUp(object sender, MouseButtonEventArgs e) => EndTalk();
    private void OnTalkLost(object sender, MouseEventArgs e) => EndTalk();

    private void EndTalk()
    {
        if (!_talking) return;
        _talking = false;
        if (Mouse.Captured == TalkButton) Mouse.Capture(null);
        Vm?.PushToTalkUp();
    }
}
