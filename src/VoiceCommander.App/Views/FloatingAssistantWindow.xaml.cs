using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.App.ViewModels;
using VoiceCommander.Core.Services;

namespace VoiceCommander.App.Views;

/// <summary>
/// Compact always-on-top status window. It never takes keyboard focus (WS_EX_NOACTIVATE), stays out of Alt+Tab and the
/// taskbar (WS_EX_TOOLWINDOW) and only renders <see cref="AssistantViewModel"/>. Code-behind is limited to window chrome:
/// dragging, remembered position, the waveform storyboard and the idle timers.
/// </summary>
public partial class FloatingAssistantWindow : WindowBase
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr value);

    private readonly AssistantViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _idle = new();
    private readonly Storyboard _wave;
    private bool _waveRunning;
    private bool _positioned;
    private Point _pillDown;
    private bool _pillPressed, _pillDragged;

    public FloatingAssistantWindow(AssistantViewModel vm, ISettingsService settings)
    {
        _vm = vm; _settings = settings;
        InitializeComponent();
        DataContext = vm;
        _wave = (Storyboard)Resources["WaveStoryboard"];
        WindowStartupLocation = WindowStartupLocation.Manual;

        _idle.Tick += OnIdleTick;
        vm.PhaseChanged += OnPhaseChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantViewModel.IsListening)) UpdateWave();
        };
        SourceInitialized += (_, _) => MakeNoActivate();
        Loaded += (_, _) => { PlaceInitially(); UpdateWave(); ScheduleIdle(vm.Phase); };
        IsVisibleChanged += (_, _) => UpdateWave();
        Closed += (_, _) => { _idle.Stop(); StopWave(); vm.PhaseChanged -= OnPhaseChanged; };
    }

    // ------------------------------------------------------------------ window chrome

    private void MakeNoActivate()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }
        catch { /* purely cosmetic: worst case the window can take focus when clicked */ }
    }

    private void PlaceInitially()
    {
        if (_positioned) return;
        _positioned = true;
        var area = SystemParameters.WorkArea;
        var s = _settings.Current;
        double left, top;
        if (s.AssistantLeft is { } l && s.AssistantTop is { } t)
        {
            // clamp so a monitor that went away never leaves the assistant off-screen
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            left = Math.Min(Math.Max(l, vs.Left), vs.Right - 60);
            top = Math.Min(Math.Max(t, vs.Top), vs.Bottom - 40);
        }
        else
        {
            left = area.Right - ActualWidth - 16;
            top = area.Bottom - ActualHeight - 16;
        }
        Left = left; Top = top;
    }

    private void SavePosition()
    {
        if (!_positioned || !IsLoaded) return;
        var s = _settings.Current;
        if (s.AssistantLeft == Left && s.AssistantTop == Top) return;
        s.AssistantLeft = Left; s.AssistantTop = Top;
        _settings.Save();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { return; }
        SavePosition();
    }

    // Pill: a click expands, a drag moves.
    private void OnPillDown(object sender, MouseButtonEventArgs e)
    {
        _pillPressed = true; _pillDragged = false; _pillDown = e.GetPosition(this);
    }

    private void OnPillMove(object sender, MouseEventArgs e)
    {
        if (!_pillPressed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pillDown.X) < 5 && Math.Abs(p.Y - _pillDown.Y) < 5) return;
        _pillDragged = true; _pillPressed = false;
        try { DragMove(); } catch { }
        SavePosition();
    }

    private void OnPillUp(object sender, MouseButtonEventArgs e)
    {
        var click = _pillPressed && !_pillDragged;
        _pillPressed = false;
        if (click) _vm.ExpandCommand.Execute(null);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        // Position is saved when a drag ends (see OnDrag / OnPillMove) - not on every pixel.
    }

    // ------------------------------------------------------------------ waveform (only runs while listening)

    private void UpdateWave()
    {
        if (_vm.IsListening && IsVisible && !_vm.IsCollapsed) StartWave(); else StopWave();
    }

    private void StartWave()
    {
        if (_waveRunning) return;
        _waveRunning = true;
        _wave.Begin(this, true);
    }

    private void StopWave()
    {
        if (!_waveRunning) return;
        _waveRunning = false;
        _wave.Stop(this);
    }

    // ------------------------------------------------------------------ idle behaviour (auto collapse / hide)

    private void OnPhaseChanged(AssistantPhase before, AssistantPhase after)
    {
        var s = _settings.Current;
        if (after != AssistantPhase.Ready)
        {
            _idle.Stop();
            if (!IsVisible && s.AssistantHideWhenIdle) Show();
            if (s.AssistantAutoCollapse && _vm.IsCollapsed && after != AssistantPhase.Listening) _vm.ExpandCommand.Execute(null);
            else if (s.AssistantAutoCollapse && _vm.IsCollapsed) _vm.ExpandCommand.Execute(null);
        }
        else ScheduleIdle(after);
        UpdateWave();
    }

    private void ScheduleIdle(AssistantPhase phase)
    {
        _idle.Stop();
        if (phase != AssistantPhase.Ready) return;
        var s = _settings.Current;
        if (s.AssistantHideWhenIdle) _idle.Interval = TimeSpan.FromSeconds(s.AssistantHideAfterSeconds);
        else if (s.AssistantAutoCollapse && !_vm.IsCollapsed) _idle.Interval = TimeSpan.FromSeconds(4);
        else return;
        _idle.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        _idle.Stop();
        if (!_vm.IsReady) return;
        var s = _settings.Current;
        if (s.AssistantHideWhenIdle) Hide();
        else if (s.AssistantAutoCollapse) _vm.CollapseCommand.Execute(null);
    }
}
