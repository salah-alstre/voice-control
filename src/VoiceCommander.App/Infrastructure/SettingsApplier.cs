using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.Infrastructure;

/// <summary>
/// Single place that turns a settings change into runtime effects: language/RTL, theme, global hotkeys, microphone and
/// engine reloads, start-with-Windows. Pages only edit and save settings; they never reach into these services.
/// </summary>
public sealed class SettingsApplier
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _loc;
    private readonly IHotkeyService _hotkeys;
    private readonly IListeningService _listening;
    private readonly IStartupService _startup;
    private readonly ILogService _log;
    private string _hotkeySig = "";
    private int _pending;

    public SettingsApplier(ISettingsService settings, ILocalizer loc, IHotkeyService hotkeys, IListeningService listening,
        IStartupService startup, ILogService log)
    {
        _settings = settings; _loc = loc; _hotkeys = hotkeys; _listening = listening; _startup = startup; _log = log;
    }

    /// <summary>Applies language and theme synchronously (call before the first window exists).</summary>
    public void ApplyLookAndFeel()
    {
        var s = _settings.Current;
        if (_loc.Language != s.Language) _loc.SetLanguage(s.Language);
        ThemeService.Apply(s.Theme);
    }

    public void Start()
    {
        _settings.Changed += () => UI.Post(OnChanged);
    }

    private void OnChanged()
    {
        ApplyLookAndFeel();
        // Debounce: sliders and hotkey boxes can save several times in a row.
        var ticket = Interlocked.Increment(ref _pending);
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            if (ticket != Volatile.Read(ref _pending)) return;
            try { await ApplyRuntimeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(LogChannel.App, "Applying settings failed", ex); }
        });
    }

    public async Task ApplyRuntimeAsync()
    {
        var s = _settings.Current;
        var sig = $"{s.PushToTalkHotkey}|{s.ToggleListeningHotkey}|{s.OpenAppHotkey}";
        if (sig != _hotkeySig)
        {
            _hotkeySig = sig;
            await _hotkeys.ApplyAsync(s.PushToTalkHotkey, s.ToggleListeningHotkey, s.OpenAppHotkey).ConfigureAwait(false);
        }
        if (_startup.IsEnabled != s.StartWithWindows) _startup.SetEnabled(s.StartWithWindows, startMinimized: true);
        await _listening.ApplySettingsAsync().ConfigureAwait(false);
    }
}
