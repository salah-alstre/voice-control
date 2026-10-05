using System.Windows;
using System.Windows.Threading;

namespace VoiceCommander.App.Infrastructure;

/// <summary>Dispatcher helpers. Core raises most events from background threads.</summary>
public static class UI
{
    private static Dispatcher Dispatcher => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static void Post(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.HasShutdownStarted) return;
        if (d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var d = Dispatcher;
        return d.CheckAccess() ? Task.FromResult(func()) : d.InvokeAsync(func).Task;
    }
}
