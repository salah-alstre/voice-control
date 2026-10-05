using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.ViewModels;

/// <summary>One recordable hotkey: its text plus the latest validation message.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    public HotkeyRowViewModel(HotkeyKind kind, string labelKey)
    {
        Kind = kind; LabelKey = labelKey;
    }

    public HotkeyKind Kind { get; }
    public string LabelKey { get; }
    public string Label => LocSource.Instance[LabelKey];
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _statusBrush = "TextMutedBrush";
    internal int Version;
}

/// <summary>
/// Settings page. It only edits <see cref="ISettingsService.Current"/> and saves; <see cref="SettingsApplier"/>
/// turns the change into runtime effects (language, theme, hotkeys, startup, listening).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    // Wake / similarity "on" values; the matcher treats the number as the allowed edit-distance fraction.
    private const double SimilarityOn = 0.15;

    private readonly ISettingsService _settings;
    private readonly IHotkeyService _hotkeys;
    private readonly IImportExportService _io;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly IDialogService _dialogs;
    private readonly IOnboardingService _onboarding;
    private readonly IPttSoundService _sounds;
    private readonly IAssistantController _assistant;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, IHotkeyService hotkeys, IImportExportService io,
        ICommandRepository commands, IAppRegistry apps, IDialogService dialogs, IOnboardingService onboarding,
        IPttSoundService sounds, IAssistantController assistant)
    {
        _onboarding = onboarding; _sounds = sounds; _assistant = assistant;
        _settings = settings; _hotkeys = hotkeys; _io = io; _commands = commands; _apps = apps; _dialogs = dialogs;
        _settings.Changed += OnSettingsChanged;

        Hotkeys = new ObservableCollection<HotkeyRowViewModel>
        {
            new(HotkeyKind.PushToTalk, "set.hotkey.ptt"),
            new(HotkeyKind.ToggleListening, "set.hotkey.toggle"),
            new(HotkeyKind.OpenApp, "set.hotkey.open"),
        };
        foreach (var h in Hotkeys) h.PropertyChanged += OnHotkeyChanged;

        BuildOptions();
        Load();
    }

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; }
    public IReadOnlyList<FilterOption> Languages { get; private set; } = Array.Empty<FilterOption>();
    public IReadOnlyList<FilterOption> Themes { get; private set; } = Array.Empty<FilterOption>();
    public IReadOnlyList<FilterOption> Modes { get; private set; } = Array.Empty<FilterOption>();
    public IReadOnlyList<FilterOption> Formats { get; private set; } = Array.Empty<FilterOption>();

    [ObservableProperty] private FilterOption? _selectedLanguage;
    [ObservableProperty] private FilterOption? _selectedTheme;
    [ObservableProperty] private FilterOption? _selectedMode;
    [ObservableProperty] private FilterOption? _selectedFormat;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private bool _resumeListening;
    [ObservableProperty] private bool _wakeEnabled;
    [ObservableProperty] private string _wakePhrase = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConfidenceText))] private double _confidence;
    [ObservableProperty] private bool _allowSimilarity;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CooldownText))] private double _cooldown;
    [ObservableProperty] private bool _allowPower;
    [ObservableProperty] private bool _developerMode;
    [ObservableProperty] private string _screenshotFolder = "";

    // Push-to-talk sounds
    [ObservableProperty] private bool _pttSoundsEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PttVolumeText))] private double _pttSoundVolume;

    // Floating assistant
    [ObservableProperty] private bool _assistantEnabled;
    [ObservableProperty] private bool _assistantAlwaysOnTop;
    [ObservableProperty] private bool _assistantStartWithApp;
    [ObservableProperty] private bool _assistantShowRecognizedText;
    [ObservableProperty] private bool _assistantShowExecutionStatus;
    [ObservableProperty] private bool _assistantAutoCollapse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AssistantOpacityText))] private double _assistantOpacity;
    [ObservableProperty] private bool _assistantHideWhenIdle;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AssistantHideAfterText))] private double _assistantHideAfterSeconds;

    public string PttVolumeText => $"‎{Math.Round(PttSoundVolume * 100)}%";
    public string AssistantOpacityText => $"‎{Math.Round(AssistantOpacity * 100)}%";
    public string AssistantHideAfterText => $"‎{Math.Round(AssistantHideAfterSeconds)} s";

    public string ConfidenceText => $"‎{Math.Round(Confidence * 100)}%";
    public string CooldownText => $"‎{Math.Round(Cooldown)} ms";
    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return Loc.Get("set.version", v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}");
        }
    }

    // ---- loading -----------------------------------------------------------------------------------------

    private void BuildOptions()
    {
        var loc = Loc;
        var lang = SelectedLanguage?.Key; var theme = SelectedTheme?.Key; var mode = SelectedMode?.Key; var fmt = SelectedFormat?.Key;
        Languages = new[] { new FilterOption("en", loc["lang.en"]), new FilterOption("ar", loc["lang.ar"]) };
        Themes = new[]
        {
            new FilterOption(nameof(AppTheme.Dark), loc["set.theme.dark"]),
            new FilterOption(nameof(AppTheme.Light), loc["set.theme.light"]),
            new FilterOption(nameof(AppTheme.System), loc["set.theme.system"]),
        };
        Modes = new[]
        {
            new FilterOption(nameof(ListeningMode.PushToTalk), loc["set.mode.ptt"]),
            new FilterOption(nameof(ListeningMode.AlwaysListening), loc["set.mode.always"]),
        };
        Formats = new[]
        {
            new FilterOption(nameof(ScreenshotFormat.Png), "PNG"),
            new FilterOption(nameof(ScreenshotFormat.Jpeg), "JPEG"),
            new FilterOption(nameof(ScreenshotFormat.Bmp), "BMP"),
        };
        OnPropertyChanged(nameof(Languages)); OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(Modes)); OnPropertyChanged(nameof(Formats));

        // The option lists were replaced, so re-point the selections at the new instances.
        _loading = true;
        try
        {
            if (lang != null) SelectedLanguage = Languages.FirstOrDefault(o => o.Key == lang);
            if (theme != null) SelectedTheme = Themes.FirstOrDefault(o => o.Key == theme);
            if (mode != null) SelectedMode = Modes.FirstOrDefault(o => o.Key == mode);
            if (fmt != null) SelectedFormat = Formats.FirstOrDefault(o => o.Key == fmt);
        }
        finally { _loading = false; }
    }

    private void Load()
    {
        var s = _settings.Current;
        _loading = true;
        try
        {
            SelectedLanguage = Languages.FirstOrDefault(o => o.Key == s.Language) ?? Languages[0];
            SelectedTheme = Themes.FirstOrDefault(o => o.Key == s.Theme.ToString());
            SelectedMode = Modes.FirstOrDefault(o => o.Key == s.ListeningMode.ToString());
            SelectedFormat = Formats.FirstOrDefault(o => o.Key == s.ScreenshotFormat.ToString());
            StartWithWindows = s.StartWithWindows;
            StartMinimized = s.StartMinimized;
            CloseToTray = s.CloseToTray;
            ShowNotifications = s.ShowNotifications;
            ResumeListening = s.ResumeListeningOnStart;
            WakeEnabled = s.WakePhraseEnabled;
            WakePhrase = s.WakePhrase;
            Confidence = s.ConfidenceThreshold;
            AllowSimilarity = s.SimilarityTolerance > 0;
            Cooldown = s.CommandCooldownMs;
            AllowPower = !(s.DisableShutdownCommands && s.DisableRestartCommands && s.DisableSignOutCommands && s.DisableSleepCommands);
            DeveloperMode = s.DeveloperMode;
            ScreenshotFolder = s.EffectiveScreenshotDirectory;
            PttSoundsEnabled = s.PttSoundsEnabled;
            PttSoundVolume = s.PttSoundVolume;
            AssistantEnabled = s.AssistantEnabled;
            AssistantAlwaysOnTop = s.AssistantAlwaysOnTop;
            AssistantStartWithApp = s.AssistantStartWithApp;
            AssistantShowRecognizedText = s.AssistantShowRecognizedText;
            AssistantShowExecutionStatus = s.AssistantShowExecutionStatus;
            AssistantAutoCollapse = s.AssistantAutoCollapse;
            AssistantOpacity = s.AssistantOpacity;
            AssistantHideWhenIdle = s.AssistantHideWhenIdle;
            AssistantHideAfterSeconds = s.AssistantHideAfterSeconds;
            Hotkeys[0].Text = s.PushToTalkHotkey;
            Hotkeys[1].Text = s.ToggleListeningHotkey;
            Hotkeys[2].Text = s.OpenAppHotkey;
        }
        finally { _loading = false; }
        _ = ValidateHotkeysAsync(save: false);
    }

    // ---- simple settings ---------------------------------------------------------------------------------

    private void Commit(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(_settings.Current);
        _settings.Save();
    }

    partial void OnSelectedLanguageChanged(FilterOption? value) { if (value != null) Commit(s => s.Language = value.Key); }
    partial void OnSelectedThemeChanged(FilterOption? value)
    {
        if (value != null && Enum.TryParse<AppTheme>(value.Key, out var t)) Commit(s => s.Theme = t);
    }
    partial void OnSelectedModeChanged(FilterOption? value)
    {
        if (value != null && Enum.TryParse<ListeningMode>(value.Key, out var m)) Commit(s => s.ListeningMode = m);
    }
    partial void OnSelectedFormatChanged(FilterOption? value)
    {
        if (value != null && Enum.TryParse<ScreenshotFormat>(value.Key, out var f)) Commit(s => s.ScreenshotFormat = f);
    }
    partial void OnStartWithWindowsChanged(bool value) => Commit(s => s.StartWithWindows = value);
    partial void OnStartMinimizedChanged(bool value) => Commit(s => s.StartMinimized = value);
    partial void OnCloseToTrayChanged(bool value) => Commit(s => { s.CloseToTray = value; s.MinimizeToTray = value; });
    partial void OnShowNotificationsChanged(bool value) => Commit(s => s.ShowNotifications = value);
    partial void OnResumeListeningChanged(bool value) => Commit(s => s.ResumeListeningOnStart = value);
    partial void OnWakeEnabledChanged(bool value) => Commit(s => s.WakePhraseEnabled = value);
    partial void OnWakePhraseChanged(string value)
    {
        // An empty phrase would make a required wake phrase impossible to say, so keep the previous one.
        if (!string.IsNullOrWhiteSpace(value)) Commit(s => s.WakePhrase = value.Trim());
    }
    partial void OnConfidenceChanged(double value) => Commit(s => s.ConfidenceThreshold = Math.Round(value, 2));
    partial void OnAllowSimilarityChanged(bool value) => Commit(s => s.SimilarityTolerance = value ? SimilarityOn : 0);
    partial void OnCooldownChanged(double value) => Commit(s => s.CommandCooldownMs = (int)Math.Round(value / 100) * 100);
    partial void OnAllowPowerChanged(bool value) => Commit(s =>
    {
        s.DisableShutdownCommands = s.DisableRestartCommands = s.DisableSignOutCommands = s.DisableSleepCommands = !value;
    });
    partial void OnDeveloperModeChanged(bool value) => Commit(s => s.DeveloperMode = value);

    partial void OnPttSoundsEnabledChanged(bool value) => Commit(s => s.PttSoundsEnabled = value);
    partial void OnPttSoundVolumeChanged(double value) => Commit(s => s.PttSoundVolume = Math.Round(Math.Clamp(value, 0, 1), 2));
    partial void OnAssistantEnabledChanged(bool value) => Commit(s => s.AssistantEnabled = value);
    partial void OnAssistantAlwaysOnTopChanged(bool value) => Commit(s => s.AssistantAlwaysOnTop = value);
    partial void OnAssistantStartWithAppChanged(bool value) => Commit(s => s.AssistantStartWithApp = value);
    partial void OnAssistantShowRecognizedTextChanged(bool value) => Commit(s => s.AssistantShowRecognizedText = value);
    partial void OnAssistantShowExecutionStatusChanged(bool value) => Commit(s => s.AssistantShowExecutionStatus = value);
    partial void OnAssistantAutoCollapseChanged(bool value) => Commit(s => s.AssistantAutoCollapse = value);
    partial void OnAssistantOpacityChanged(double value) => Commit(s => s.AssistantOpacity = Math.Round(Math.Clamp(value, 0.3, 1), 2));
    partial void OnAssistantHideWhenIdleChanged(bool value) => Commit(s => s.AssistantHideWhenIdle = value);
    partial void OnAssistantHideAfterSecondsChanged(double value) => Commit(s => s.AssistantHideAfterSeconds = (int)Math.Clamp(Math.Round(value), 2, 600));

    [RelayCommand] private void TestStartSound() => _sounds.Preview(PttCue.Start);
    [RelayCommand] private void TestStopSound() => _sounds.Preview(PttCue.Stop);
    [RelayCommand] private void OpenAssistant() => _assistant.Open();

    // The assistant can be closed from its own window; keep the "enabled" switch honest.
    private void OnSettingsChanged()
    {
        UI.Post(() =>
        {
            var want = _settings.Current.AssistantEnabled;
            if (AssistantEnabled == want) return;
            _loading = true;
            try { AssistantEnabled = want; } finally { _loading = false; }
        });
    }

    // ---- screenshots -------------------------------------------------------------------------------------

    [RelayCommand]
    private void BrowseScreenshotFolder()
    {
        var picked = _dialogs.PickFolder(Loc["set.screenshot.folder"]);
        if (string.IsNullOrWhiteSpace(picked)) return;
        _settings.Current.ScreenshotDirectory = picked;
        _settings.Save();
        ScreenshotFolder = _settings.Current.EffectiveScreenshotDirectory;
    }

    [RelayCommand]
    private void ResetScreenshotFolder()
    {
        _settings.Current.ScreenshotDirectory = null;
        _settings.Save();
        ScreenshotFolder = _settings.Current.EffectiveScreenshotDirectory;
    }

    // ---- hotkeys -----------------------------------------------------------------------------------------

    private void OnHotkeyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(HotkeyRowViewModel.Text)) return;
        _ = ValidateHotkeysAsync(save: true);
    }

    /// <summary>
    /// Validates every row (empty = off, invalid, duplicated inside this page, taken by another program) and, when
    /// <paramref name="save"/> is set, stores the ones that are fine. A rejected box keeps its text and an explanation
    /// but the previous working shortcut stays active.
    /// </summary>
    private async Task ValidateHotkeysAsync(bool save)
    {
        var rows = Hotkeys.ToList();
        var versions = rows.Select(r => ++r.Version).ToList();
        var texts = rows.Select(r => r.Text.Trim()).ToList();
        var problems = new HotkeyProblem[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            if (texts[i].Length == 0) { problems[i] = HotkeyProblem.Empty; continue; }
            var dup = false;
            for (var j = 0; j < rows.Count; j++)
                if (j != i && string.Equals(Canonical(texts[j]), Canonical(texts[i]), StringComparison.OrdinalIgnoreCase)) dup = true;
            problems[i] = dup ? HotkeyProblem.Duplicate : await _hotkeys.CheckAsync(texts[i]);
        }

        // A newer edit started while we were checking: let that run finish the job.
        if (rows.Where((r, i) => r.Version != versions[i]).Any()) return;

        for (var i = 0; i < rows.Count; i++) ShowProblem(rows[i], problems[i]);
        if (!save) return;

        var s = _settings.Current;
        var changed = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!IsAcceptable(rows[i].Kind, problems[i])) continue;
            changed |= Store(s, rows[i].Kind, texts[i]);
        }
        if (changed) _settings.Save();
    }

    private static string Canonical(string text) =>
        KeyNames.TryParse(text, out var m, out var vk) ? KeyNames.Format(m, vk) : text;

    // Push-to-talk uses a low-level hook, so another program holding the key is only a warning for it.
    private static bool IsAcceptable(HotkeyKind kind, HotkeyProblem p) =>
        p is HotkeyProblem.None or HotkeyProblem.Empty || (kind == HotkeyKind.PushToTalk && p == HotkeyProblem.InUse);

    private static bool Store(AppSettings s, HotkeyKind kind, string text)
    {
        switch (kind)
        {
            case HotkeyKind.PushToTalk: if (s.PushToTalkHotkey == text) return false; s.PushToTalkHotkey = text; return true;
            case HotkeyKind.ToggleListening: if (s.ToggleListeningHotkey == text) return false; s.ToggleListeningHotkey = text; return true;
            default: if (s.OpenAppHotkey == text) return false; s.OpenAppHotkey = text; return true;
        }
    }

    private void ShowProblem(HotkeyRowViewModel row, HotkeyProblem p)
    {
        var loc = Loc;
        (row.Status, row.StatusBrush) = p switch
        {
            HotkeyProblem.Empty => (loc["set.hotkey.off"], "TextMutedBrush"),
            HotkeyProblem.Invalid => (loc["set.hotkey.invalid"], "DangerBrush"),
            HotkeyProblem.Duplicate => (loc["set.hotkey.duplicate"], "DangerBrush"),
            HotkeyProblem.InUse => (loc["set.hotkey.taken"], row.Kind == HotkeyKind.PushToTalk ? "WarningBrush" : "DangerBrush"),
            _ => (loc["set.hotkey.ok"], "SuccessBrush"),
        };
    }

    // ---- data --------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Export()
    {
        var path = _dialogs.PickSaveFile(Loc["io.filter"] + "|*.voicecommander", "my-commands.voicecommander", Loc["set.export"]);
        if (path == null) return;
        try
        {
            _io.Export(path);
            _dialogs.Info(Loc["set.export"], Loc.Get("io.export.done", _commands.Commands.Count, path));
        }
        catch (Exception ex)
        {
            _dialogs.Info(Loc["set.export"], Loc.Get("io.export.failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var title = Loc["io.import.title"];
        var path = _dialogs.PickFile(Loc["io.filter"] + "|*.voicecommander", title);
        if (path == null) return;

        ImportPreview preview;
        try { preview = _io.Preview(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _dialogs.Info(title, Loc["io.import.invalid"]);
            return;
        }

        var message = Loc.Get("io.import.summary", preview.Commands.Count, preview.Applications.Count);
        if (preview.Skipped > 0) message += "\n" + Loc.Get("io.import.skipped", preview.Skipped);

        var choice = await _dialogs.ChoiceAsync(title, message, Loc["io.import.merge"], Loc["io.import.replace"], Loc["common.cancel"]);
        if (choice == 0) return;

        if (choice == 2 && !await _dialogs.ConfirmAsync(title, Loc["set.import.replace.warn"],
                Loc["io.import.replace"], Loc["common.cancel"], danger: true)) return;

        var result = _io.Apply(preview, choice == 2 ? ImportMode.Replace : ImportMode.Merge);
        _dialogs.Info(title, Loc.Get("io.import.done", result.Commands));
    }

    [RelayCommand] private Task RunSetupGuide() => _onboarding.ShowAsync();
    [RelayCommand] private void OpenDataFolder() => OpenFolder(AppPaths.Root);
    [RelayCommand] private void OpenLogsFolder() => OpenFolder(AppPaths.LogsDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* Explorer missing or blocked: nothing useful to show */ }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc["set.reset"], Loc["set.reset.body"], Loc["set.reset"], Loc["common.cancel"], danger: true)) return;
        var old = _settings.Current;
        // Keep what is about the machine rather than preference: finished onboarding and downloaded model folders/files.
        _settings.Replace(new AppSettings
        {
            OnboardingCompleted = old.OnboardingCompleted, ModelPaths = old.ModelPaths,
            WhisperModelId = old.WhisperModelId, WhisperModelPath = old.WhisperModelPath,
        });
        BuildOptions();
        Load();
        OnPropertyChanged(string.Empty);
    }

    // ---- language ----------------------------------------------------------------------------------------

    protected override void OnLanguageChanged()
    {
        BuildOptions();
        foreach (var h in Hotkeys) h.RefreshLabel();
        _ = ValidateHotkeysAsync(save: false);
        OnPropertyChanged(nameof(VersionText));
        base.OnLanguageChanged();
    }

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        foreach (var h in Hotkeys) h.PropertyChanged -= OnHotkeyChanged;
        base.Dispose();
    }
}
