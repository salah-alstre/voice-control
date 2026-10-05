using Microsoft.Extensions.DependencyInjection;
using VoiceCommander.App.ViewModels;
using VoiceCommander.App.Views;
using VoiceCommander.Core.Services;

namespace VoiceCommander.App.Infrastructure;

/// <summary>Owns the lifetime of the floating mini assistant. At most one window exists; it shares the app-wide state service.</summary>
public interface IAssistantController : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Close();
    void Toggle();
}

public sealed class AssistantController : IAssistantController
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private FloatingAssistantWindow? _window;
    private AssistantViewModel? _vm;
    private bool _syncing, _disposed;

    public AssistantController(IServiceProvider services, ISettingsService settings)
    {
        _services = services; _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsOpen => _window != null;

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Open()
    {
        if (_disposed) return;
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            UI.Post(Open);
            return;
        }
        if (_window != null)
        {
            if (!_window.IsVisible) _window.Show();
            Persist(true);
            return;
        }

        _vm = ActivatorUtilities.CreateInstance<AssistantViewModel>(_services);
        _vm.OpenSettingsRequested += () => ShowMain("settings");
        _vm.LocateRequested += () => ShowMain("applications");
        _vm.CloseRequested += () => { Close(); };
        _window = new FloatingAssistantWindow(_vm, _settings);
        _window.Closed += OnWindowClosed;
        _window.Show();
        Persist(true);
    }

    public void Close()
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            UI.Post(Close);
            return;
        }
        var w = _window;
        Persist(false);
        if (w == null) return;
        w.Close(); // OnWindowClosed releases the view-model
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window != null) _window.Closed -= OnWindowClosed;
        _window = null;
        _vm?.Dispose();
        _vm = null;
    }

    private void OnSettingsChanged()
    {
        if (_disposed) return;
        UI.Post(() =>
        {
            if (_disposed || _syncing) return;
            var want = _settings.Current.AssistantEnabled;
            if (want && _window == null) Open();
            else if (!want && _window != null) Close();
        });
    }

    private void Persist(bool enabled)
    {
        if (_settings.Current.AssistantEnabled == enabled) return;
        _syncing = true;
        try { _settings.Current.AssistantEnabled = enabled; _settings.Save(); }
        finally { _syncing = false; }
    }

    private static void ShowMain(string page)
    {
        if (System.Windows.Application.Current is not App app) return;
        app.ShowWindow();
        App.Services.GetService<MainViewModel>()?.Navigate(page);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        try { _window?.Close(); } catch { }
    }
}
