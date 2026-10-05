using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.App.ViewModels;
using VoiceCommander.App.Views;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\VoiceCommander.SingleInstance";
    private const string ShowEventName = "Local\\VoiceCommander.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private IServiceProvider _services = null!;
    private MainWindow? _window;
    private TrayService _tray = null!;
    private bool _exiting;

    public static IServiceProvider Services => ((App)Current)._services;

    protected override void OnStartup(StartupEventArgs e)
    {
        try { OnStartupCore(e); }
        catch (Exception ex) { TryLog("Fatal error during startup", ex); throw; }
    }

    /// <summary>Developer-only: renders the UI to PNGs against an isolated temp data folder (see RenderHarness).</summary>
    private void RunRenderShots(string dir)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.IO.Directory.CreateDirectory(dir);
        AppPaths.Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VoiceCommander-shots-" + Guid.NewGuid().ToString("N")[..8]);
        AppPaths.EnsureCreated();
        _services = AppHost.Build(s =>
        {
            var old = s.Last(d => d.ServiceType == typeof(IAssistantStateService));
            s.Remove(old);
            s.AddSingleton<IAssistantStateService>(RenderHarness.Fake);
        });
        var loc = _services.GetRequiredService<ILocalizer>();
        LocSource.Instance.Init(loc);
        _services.GetRequiredService<SettingsApplier>().ApplyLookAndFeel();
        _ = Task.Run(async () =>
        {
            try { await await Dispatcher.InvokeAsync(() => RenderHarness.RunAsync(_services, dir)); }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "_harness_error.txt"), ex.ToString()); }
            finally { await Dispatcher.InvokeAsync(Shutdown); }
        });
    }

    /// <summary>
    /// Developer-only: like <see cref="RunRenderShots"/> but with the tidy release data set. <paramref name="dataRoot"/> is the
    /// (neutral) data folder to use, so on-screen paths do not reveal a user name; it must be empty or throw-away.
    /// </summary>
    private void RunReleaseShots(string dir, string dataRoot)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.IO.Directory.CreateDirectory(dir);
        AppPaths.Root = dataRoot;
        AppPaths.EnsureCreated();
        _services = AppHost.Build(s =>
        {
            var old = s.Last(d => d.ServiceType == typeof(IAssistantStateService));
            s.Remove(old);
            s.AddSingleton<IAssistantStateService>(RenderHarness.Fake);
            s.Remove(s.Last(d => d.ServiceType == typeof(IListeningService)));
            s.AddSingleton<IListeningService, RenderHarness.FakeListening>();
        });
        LocSource.Instance.Init(_services.GetRequiredService<ILocalizer>());
        _services.GetRequiredService<SettingsApplier>().ApplyLookAndFeel();
        _ = Task.Run(async () =>
        {
            try { await await Dispatcher.InvokeAsync(() => RenderHarness.RunReleaseAsync(_services, dir)); }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "_harness_error.txt"), ex.ToString()); }
            finally { await Dispatcher.InvokeAsync(Shutdown); }
        });
    }

    private void OnStartupCore(StartupEventArgs e)
    {
        base.OnStartup(e);

        var relIdx = Array.FindIndex(e.Args, a => a.Equals("--render-release-shots", StringComparison.OrdinalIgnoreCase));
        if (relIdx >= 0 && relIdx + 2 < e.Args.Length)
        {
            RunReleaseShots(e.Args[relIdx + 1], e.Args[relIdx + 2]);
            return;
        }

        var shotsIdx = Array.FindIndex(e.Args, a => a.Equals("--render-shots", StringComparison.OrdinalIgnoreCase));
        if (shotsIdx >= 0 && shotsIdx + 1 < e.Args.Length)
        {
            RunRenderShots(e.Args[shotsIdx + 1]);
            return;
        }

        _mutex = new Mutex(true, MutexName, out var first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // Another instance is running: ask it to show its window and quit.
            try { _showEvent.Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => TryLog("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { TryLog("Unobserved task exception", a.Exception); a.SetObserved(); };

        AppPaths.EnsureCreated();
        _services = AppHost.Build();

        var loc = _services.GetRequiredService<ILocalizer>();
        var settings = _services.GetRequiredService<ISettingsService>();
        LocSource.Instance.Init(loc);
        _services.GetRequiredService<IStartupService>();
        _services.GetRequiredService<IHotkeyService>();
        _services.GetRequiredService<IListeningService>();
        _services.GetRequiredService<IAssistantStateService>().Start();
        _services.GetRequiredService<IPttSoundService>().Start();
        var applier = _services.GetRequiredService<SettingsApplier>();
        applier.ApplyLookAndFeel();
        applier.Start();

        _tray = _services.GetRequiredService<TrayService>();
        _tray.OpenRequested += () => UI.Post(ShowWindow);
        _tray.ExitRequested += () => UI.Post(ExitApp);
        _tray.Start();

        var main = _services.GetRequiredService<MainViewModel>();

        _window = new MainWindow(main, settings);
        _window.Closing += OnWindowClosing;
        MainWindow = _window;

        var startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)
                             || (settings.Current.StartMinimized && settings.Current.OnboardingCompleted);
        main.Start();
        if (!startMinimized) _window.Show();

        var assistant = _services.GetRequiredService<IAssistantController>();
        if (settings.Current.AssistantStartWithApp) assistant.Open();

        // First run: the setup guide opens once the main window is up (also when started minimized, so the user is never left blind).
        if (!settings.Current.OnboardingCompleted)
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { _services.GetService<ILogService>()?.Info(LogChannel.App, "Opening setup guide (first run)"); await _services.GetRequiredService<IOnboardingService>().ShowAsync(); }
                catch (Exception ex) { TryLog("Setup guide failed to open", ex); }
            }, DispatcherPriority.ApplicationIdle);

        // Listen for "show yourself" signals from a second launch.
        var waiter = new Thread(() =>
        {
            while (!_exiting)
            {
                try { if (_showEvent!.WaitOne(500)) UI.Post(ShowWindow); }
                catch { return; }
            }
        }) { IsBackground = true, Name = "single-instance-signal" };
        waiter.Start();

        var hotkeys = _services.GetRequiredService<IHotkeyService>();
        hotkeys.OpenAppPressed += () => UI.Post(ShowWindow);

        _ = StartEnginesAsync(applier, settings);
    }

    private async Task StartEnginesAsync(SettingsApplier applier, ISettingsService settings)
    {
        try
        {
            await applier.ApplyRuntimeAsync();
            await _services.GetRequiredService<IListeningService>().StartAsync();
        }
        catch (Exception ex) { TryLog("Startup of speech engines failed", ex); }
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; _window.Topmost = false; // bring in front of other windows
        _window.Focus();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        var s = _services.GetRequiredService<ISettingsService>();
        SaveWindowSize(s);
        if (s.Current.CloseToTray)
        {
            e.Cancel = true;
            _window!.Hide();
            if (s.Current.ShowNotifications) _tray.Notify("Voice Commander", LocSource.Instance["tray.minimized"]);
        }
        else ExitApp();
    }

    private void SaveWindowSize(ISettingsService s)
    {
        if (_window is not { WindowState: WindowState.Normal }) return;
        s.Current.WindowWidth = _window.Width; s.Current.WindowHeight = _window.Height;
        try { JsonStore.Save(AppPaths.SettingsFile, s.Current); } catch { }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { _services.GetService<IAssistantController>()?.Dispose(); } catch { }
        try { _services.GetService<IPttSoundService>()?.Dispose(); } catch { }
        try { _services.GetService<IAssistantStateService>()?.Dispose(); } catch { }
        try { _services.GetService<IListeningService>()?.Dispose(); } catch { }
        try { _services.GetService<IHotkeyService>()?.Dispose(); } catch { }
        try { _tray.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("Unhandled UI exception", e.Exception);
        e.Handled = true; // a failed handler must never take the whole app (and the microphone) down
        try
        {
            _services.GetService<IDialogService>()?.Info(LocSource.Instance["common.error"],
                e.Exception.Message);
        }
        catch { }
    }

    private void TryLog(string message, Exception? ex)
    {
        try { _services?.GetService<ILogService>()?.Error(LogChannel.App, message, ex); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
