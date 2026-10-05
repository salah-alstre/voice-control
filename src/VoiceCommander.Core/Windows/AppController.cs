using System.ComponentModel;
using System.Diagnostics;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Windows;

public enum AppControlStatus { Ok, NotFound, NotRunning, Failed }
public sealed record AppControlResult(AppControlStatus Status, string Detail = "")
{
    public bool Success => Status == AppControlStatus.Ok;
}

public interface IAppController
{
    Task<AppControlResult> OpenAsync(AppDefinition app);
    Task<AppControlResult> CloseAsync(AppDefinition app);
    Task<AppControlResult> RestartAsync(AppDefinition app);
    AppControlResult Focus(AppDefinition app);
    AppControlResult Minimize(AppDefinition app);
    AppControlResult Maximize(AppDefinition app);
    bool IsRunning(AppDefinition app);
}

/// <summary>Launches and manages registered applications. Only registry-defined targets are ever started.</summary>
public sealed class AppController : IAppController
{
    private readonly IAppLocator _locator;
    private readonly ILogService _log;

    public AppController(IAppLocator locator, ILogService log) { _locator = locator; _log = log; }

    private static string ProcName(AppDefinition a) =>
        !string.IsNullOrWhiteSpace(a.ProcessName) ? a.ProcessName
        : Path.GetFileNameWithoutExtension(a.ExecutablePath ?? "");

    private static Process[] Running(AppDefinition a)
    {
        var name = ProcName(a);
        return string.IsNullOrWhiteSpace(name) ? Array.Empty<Process>() : Process.GetProcessesByName(name);
    }

    public bool IsRunning(AppDefinition app)
    {
        var ps = Running(app);
        var any = ps.Length > 0;
        foreach (var p in ps) p.Dispose();
        return any;
    }

    public async Task<AppControlResult> OpenAsync(AppDefinition app)
    {
        // Single-instance apps are brought forward instead of duplicated.
        if (app.SingleInstance && IsRunning(app))
        {
            var f = Focus(app);
            if (f.Success) return new AppControlResult(AppControlStatus.Ok, "already running");
        }

        var target = _locator.Resolve(app);
        if (target == null)
        {
            _log.Warn(LogChannel.Command, $"App not found: {app.Name} ({app.ExecutablePath})");
            return new AppControlResult(AppControlStatus.NotFound);
        }

        try
        {
            var psi = new ProcessStartInfo(target.FileName, target.Arguments) { UseShellExecute = true };
            var wd = Environment.ExpandEnvironmentVariables(app.WorkingDirectory ?? "").Trim('"');
            if (!string.IsNullOrWhiteSpace(wd) && Directory.Exists(wd)) psi.WorkingDirectory = wd;
            using var p = Process.Start(psi);
            _log.Info(LogChannel.Command, $"Started {app.Name}: {target.FileName} {target.Arguments}");
            await Task.CompletedTask;
            return new AppControlResult(AppControlStatus.Ok);
        }
        catch (Win32Exception ex)
        {
            _log.Error(LogChannel.Command, $"Could not start {app.Name}", ex);
            return new AppControlResult(AppControlStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(LogChannel.Command, $"Could not start {app.Name}", ex);
            return new AppControlResult(AppControlStatus.Failed, ex.Message);
        }
    }

    public async Task<AppControlResult> CloseAsync(AppDefinition app)
    {
        var procs = Running(app);
        if (procs.Length == 0) return new AppControlResult(AppControlStatus.NotRunning);
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (!app.ForceClose && p.CloseMainWindow()) continue;
                    if (app.ForceClose || p.MainWindowHandle == IntPtr.Zero) p.Kill(true);
                }
                catch (Exception ex) { _log.Warn(LogChannel.Command, $"Close {p.ProcessName}: {ex.Message}"); }
            }
            // give graceful closes a moment; escalate if the user opted into force close
            for (int i = 0; i < 20 && IsRunning(app); i++) await Task.Delay(100);
            return new AppControlResult(AppControlStatus.Ok);
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public async Task<AppControlResult> RestartAsync(AppDefinition app)
    {
        if (IsRunning(app))
        {
            var forced = new AppDefinition { Id = app.Id, Name = app.Name, ProcessName = app.ProcessName, ExecutablePath = app.ExecutablePath, ForceClose = true };
            await CloseAsync(app.ForceClose ? app : forced);
            for (int i = 0; i < 30 && IsRunning(app); i++) await Task.Delay(100);
        }
        return await OpenAsync(app);
    }

    private AppControlResult WithWindow(AppDefinition app, Func<IntPtr, bool> act)
    {
        var pids = Running(app).Select(p => { var id = p.Id; p.Dispose(); return (uint)id; }).ToHashSet();
        if (pids.Count == 0) return new AppControlResult(AppControlStatus.NotRunning);
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return new AppControlResult(AppControlStatus.NotRunning, "no window");
        return act(found) ? new AppControlResult(AppControlStatus.Ok) : new AppControlResult(AppControlStatus.Failed);
    }

    public AppControlResult Focus(AppDefinition app) => WithWindow(app, h =>
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        // Pressing and releasing Alt lets SetForegroundWindow succeed from a background process.
        KeyboardInjector.TapAlt();
        Native.BringWindowToTop(h);
        return Native.SetForegroundWindow(h);
    });

    public AppControlResult Minimize(AppDefinition app) => WithWindow(app, h => { Native.ShowWindow(h, Native.SW_MINIMIZE); return true; });
    public AppControlResult Maximize(AppDefinition app) => WithWindow(app, h => { Native.ShowWindow(h, Native.SW_SHOWMAXIMIZED); return Native.SetForegroundWindow(h) || true; });
}
