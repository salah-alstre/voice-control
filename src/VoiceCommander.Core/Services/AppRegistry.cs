using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Services;

public interface IAppRegistry
{
    IReadOnlyList<AppDefinition> Apps { get; }
    event Action? Changed;
    AppDefinition? FindById(string? id);
    AppDefinition? FindByName(string spoken);
    void AddOrUpdate(AppDefinition app);
    bool Remove(string id);
    void ReplaceAll(IEnumerable<AppDefinition> apps);
}

/// <summary>User-visible application registry. Commands reference apps by Id only; never by free text from speech.</summary>
public sealed class AppRegistry : IAppRegistry
{
    private readonly ILogService _log;
    private readonly object _gate = new();
    private List<AppDefinition> _apps;

    public AppRegistry(ILogService log)
    {
        _log = log;
        var loaded = JsonStore.Load<List<AppDefinition>>(AppPaths.ApplicationsFile, log);
        _apps = loaded ?? BuiltInApps.Create();
        if (loaded == null) Persist();
    }

    public IReadOnlyList<AppDefinition> Apps { get { lock (_gate) return _apps.ToList(); } }
    public event Action? Changed;

    public AppDefinition? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _apps.FirstOrDefault(a => a.Id == id);
    }

    public AppDefinition? FindByName(string spoken)
    {
        var n = TextNormalizer.Normalize(spoken);
        lock (_gate)
            return _apps.FirstOrDefault(a => TextNormalizer.Normalize(a.Name) == n
                || a.Aliases.Any(x => TextNormalizer.Normalize(x) == n));
    }

    public void AddOrUpdate(AppDefinition app)
    {
        lock (_gate)
        {
            var i = _apps.FindIndex(a => a.Id == app.Id);
            if (i >= 0) _apps[i] = app; else _apps.Add(app);
            Persist();
        }
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _apps.RemoveAll(a => a.Id == id) > 0;
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void ReplaceAll(IEnumerable<AppDefinition> apps)
    {
        lock (_gate) { _apps = apps.ToList(); Persist(); }
        Changed?.Invoke();
    }

    private void Persist()
    {
        try { JsonStore.Save(AppPaths.ApplicationsFile, _apps); }
        catch (Exception ex) { _log.Error(LogChannel.App, "Failed to save applications", ex); }
    }
}

/// <summary>Stable-Id starter applications. Paths are resolved at launch time by <see cref="Windows.AppLocator"/>.</summary>
public static class BuiltInApps
{
    public static List<AppDefinition> Create() => new()
    {
        App("app.notepad", "Notepad", "notepad.exe", "notepad", "notepad", "المفكرة", "نوت باد"),
        App("app.calculator", "Calculator", "calc.exe", "CalculatorApp", "calculator", "الحاسبة", "آلة حاسبة"),
        App("app.paint", "Paint", "mspaint.exe", "mspaint", "paint", "الرسام", "بينت"),
        App("app.taskmanager", "Task Manager", "taskmgr.exe", "Taskmgr", "task manager", "مدير المهام"),
        App("app.chrome", "Chrome", "chrome.exe", "chrome", "chrome", "google chrome", "كروم", "جوجل كروم"),
        App("app.edge", "Edge", "msedge.exe", "msedge", "edge", "microsoft edge", "ايدج"),
        App("app.firefox", "Firefox", "firefox.exe", "firefox", "firefox", "فايرفوكس"),
        App("app.discord", "Discord", "Discord.exe", "Discord", "discord", "ديسكورد"),
        App("app.spotify", "Spotify", "Spotify.exe", "Spotify", "spotify", "سبوتيفاي"),
        App("app.steam", "Steam", "steam.exe", "steam", "steam", "ستيم"),
        App("app.vscode", "Visual Studio Code", "Code.exe", "Code", "vs code", "vscode", "visual studio code", "code"),
        App("app.vlc", "VLC", "vlc.exe", "vlc", "vlc", "في ال سي"),
        App("app.obs", "OBS Studio", "obs64.exe", "obs64", "obs", "obs studio"),
        App("app.explorer", "File Explorer", "explorer.exe", "explorer", "file explorer", "explorer", "مستكشف الملفات"),
    };

    private static AppDefinition App(string id, string name, string exe, string process, params string[] aliases) => new()
    {
        Id = id, Name = name, ExecutablePath = exe, ProcessName = process, IsBuiltIn = true,
        Aliases = aliases.Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList(),
        // Discord/Spotify/etc. keep running in the tray and ignore WM_CLOSE.
        ForceClose = id is "app.discord" or "app.spotify" or "app.steam",
        SingleInstance = id is "app.discord" or "app.spotify" or "app.steam" or "app.taskmanager",
    };
}
