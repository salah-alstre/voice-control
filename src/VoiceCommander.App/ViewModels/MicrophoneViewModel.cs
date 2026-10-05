using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

/// <summary>One entry of the input-device picker. <see cref="Name"/> null means the Windows default device.</summary>
public sealed record DeviceOption(string? Name, string Label);

public sealed partial class MicrophoneViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IMicrophoneService _mic;
    private readonly ISpeechEngineManager _engines;
    private readonly ISpeechModelManager _models;
    private readonly IWhisperModelManager _whisper;
    private readonly IDialogService _dialogs;
    // Separate capture used only for the level test: its audio never reaches the recognizer.
    private readonly MicrophoneService _tester;
    private bool _loading;

    public MicrophoneViewModel(ISettingsService settings, IMicrophoneService mic, ISpeechEngineManager engines,
        ISpeechModelManager models, IWhisperModelManager whisper, IDialogService dialogs, ILogService log)
    {
        _settings = settings; _mic = mic; _engines = engines; _models = models; _whisper = whisper; _dialogs = dialogs;
        _tester = new MicrophoneService(log);
        _tester.LevelChanged += v => UI.Post(() => { if (IsTesting) Level = v; });
        _tester.Failed += _ => UI.Post(StopTest);

        Models = new ObservableCollection<ModelItemViewModel>(
            _models.Catalog.Select(m => new ModelItemViewModel(m, _models, _dialogs)));
        WhisperModels = new ObservableCollection<WhisperModelItemViewModel>(
            _whisper.Catalog.Select(m => new WhisperModelItemViewModel(m, _whisper, _dialogs)));
        Engines = new ObservableCollection<EngineRowViewModel>();

        RefreshDevices();
        BuildOptions();
        RefreshEngines();

        _engines.StatesChanged += OnEnginesChanged;
        _models.Changed += OnModelsChanged;
        _whisper.Changed += OnModelsChanged;
    }

    public ObservableCollection<DeviceOption> Devices { get; } = new();
    public ObservableCollection<ModelItemViewModel> Models { get; }
    public ObservableCollection<WhisperModelItemViewModel> WhisperModels { get; }
    public ObservableCollection<EngineRowViewModel> Engines { get; }
    public IReadOnlyList<FilterOption> Profiles { get; private set; } = Array.Empty<FilterOption>();
    public IReadOnlyList<FilterOption> EngineChoices { get; private set; } = Array.Empty<FilterOption>();

    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private FilterOption? _selectedProfile;
    [ObservableProperty] private FilterOption? _selectedEnglishEngine;
    [ObservableProperty] private FilterOption? _selectedArabicEngine;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private float _level;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";

    public bool HasDevices => Devices.Any(d => d.Name != null);
    public bool HasMessage => Message.Length > 0;
    public string TestText => Loc[IsTesting ? "mic.test.stop" : "mic.test"];

    private string[] WantedLanguages => _settings.Current.SpeechProfile switch
    {
        SpeechProfile.English => new[] { "en" },
        SpeechProfile.Arabic => new[] { "ar" },
        _ => new[] { "en", "ar" },
    };

    // Languages whose chosen engine is Whisper and whose state matches the given status.
    private IEnumerable<LanguageEngineState> WhisperStates(EngineStatus status) =>
        _engines.States.Where(s => WantedLanguages.Contains(s.Language) && s.Status == status
                                   && _settings.Current.ResolveEngine(s.Language) == WhisperEngine.Id);

    /// <summary>"Arabic Whisper model required [Install Model] [Use Vosk instead]".</summary>
    public bool ShowWhisperRequired => WhisperStates(EngineStatus.MissingModel).Any();
    /// <summary>"The Whisper model could not be loaded [Repair Model] [Choose Model]".</summary>
    public bool ShowWhisperFailed => WhisperStates(EngineStatus.Failed).Any();
    public string WhisperFailedDetail => string.Join(" ", WhisperStates(EngineStatus.Failed).Select(s => s.Detail).Where(d => !string.IsNullOrWhiteSpace(d)));
    public bool HasWhisperFailedDetail => WhisperFailedDetail.Length > 0;
    public string WhisperLocation => Loc.Get("whisper.location", _whisper.ModelsDirectory);
    public bool HasCustomModel => _whisper.CustomPath != null;
    public string CustomModelText => _whisper.CustomPath == null ? "" : Loc.Get("whisper.custom", _whisper.CustomPath);

    public string MismatchText => BuildMismatch();
    public bool HasMismatch => MismatchText.Length > 0;
    public string SummaryMic => SelectedDevice?.Label ?? "";
    public string SummaryProfile => SelectedProfile?.Label ?? "";
    public string SummaryEngine => string.Join("  ·  ", WantedLanguages.Select(l => l.ToUpperInvariant() + ": " + ShortEngineName(_settings.Current.ResolveEngine(l))));

    private static string ShortEngineName(string id) => id switch
    {
        WhisperEngine.Id => "Whisper",
        WindowsSpeechEngine.Id => "Windows",
        _ => "Vosk",
    };
    public string SummaryPtt => _settings.Current.ListeningMode == ListeningMode.PushToTalk ? _settings.Current.PushToTalkHotkey : Loc["mic.sum.always"];
    public string SummaryStatus => string.Join("  ·  ", Engines.Select(e => e.Language + " " + e.Status));

    // Surfaces (never silently "fixes") a mismatch between the chosen speech profile and the models that are actually usable.
    private string BuildMismatch()
    {
        var wanted = WantedLanguages;
        var lines = new List<string>();
        foreach (var s in _engines.States.Where(x => wanted.Contains(x.Language)))
        {
            var lang = Loc["lang." + s.Language];
            // Whisper has its own banner (install / use Vosk instead) and is free-form by design.
            if (_settings.Current.ResolveEngine(s.Language) == WhisperEngine.Id) continue;
            if (s.Status == EngineStatus.MissingModel) lines.Add(Loc.Get("mic.mismatch.missing", lang));
            else if (s.Status == EngineStatus.Ready && !s.UsesGrammar && s.Language == "ar") lines.Add(Loc.Get("mic.mismatch.freeform", lang));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void RaiseSummary()
    {
        foreach (var n in new[] { nameof(MismatchText), nameof(HasMismatch), nameof(SummaryMic), nameof(SummaryProfile),
                     nameof(SummaryEngine), nameof(SummaryPtt), nameof(SummaryStatus), nameof(ShowWhisperRequired),
                     nameof(ShowWhisperFailed), nameof(WhisperFailedDetail), nameof(HasWhisperFailedDetail),
                     nameof(WhisperLocation), nameof(HasCustomModel), nameof(CustomModelText) })
            OnPropertyChanged(n);
    }

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(TestText));

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        if (_loading || value == null) return;
        _settings.Current.MicrophoneName = value.Name;
        _settings.Save();
        RaiseSummary();
        if (IsTesting) { StopTest(); StartTest(); }
    }

    partial void OnSelectedProfileChanged(FilterOption? value)
    {
        if (_loading || value == null || !Enum.TryParse<SpeechProfile>(value.Key, out var profile)) return;
        _settings.Current.SpeechProfile = profile;
        _settings.Save();
        RaiseSummary();
    }

    partial void OnSelectedEnglishEngineChanged(FilterOption? value) => SetEngine("en", value);
    partial void OnSelectedArabicEngineChanged(FilterOption? value) => SetEngine("ar", value);

    // Persisted per language; SettingsApplier reloads the engines (only if the engine/model signature changed).
    private void SetEngine(string language, FilterOption? value)
    {
        if (_loading || value == null) return;
        _settings.Current.EngineByLanguage ??= new();
        _settings.Current.EngineByLanguage[language] = value.Key;
        _settings.Save();
        RaiseSummary();
    }

    private void RefreshDevices()
    {
        _loading = true;
        try
        {
            var saved = _settings.Current.MicrophoneName;
            Devices.Clear();
            Devices.Add(new DeviceOption(null, Loc["mic.default"]));
            foreach (var d in _mic.GetDevices()) Devices.Add(new DeviceOption(d.Name, d.Name));
            // A remembered microphone that is unplugged stays visible so the choice is not silently lost.
            if (!string.IsNullOrEmpty(saved) && Devices.All(d => d.Name != saved))
                Devices.Add(new DeviceOption(saved, Loc.Get("mic.notconnected", saved)));
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == (string.IsNullOrEmpty(saved) ? null : saved)) ?? Devices[0];
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(HasDevices));
    }

    private void BuildOptions()
    {
        _loading = true;
        try
        {
            Profiles = Enum.GetValues<SpeechProfile>()
                .Select(p => new FilterOption(p.ToString(), Loc["profile." + p.ToString().ToLowerInvariant()])).ToList();
            EngineChoices = new[]
            {
                new FilterOption(VoskEngine.Id, Loc["mic.engine.vosk"]),
                new FilterOption(WhisperEngine.Id, Loc["mic.engine.whisper"]),
                new FilterOption(WindowsSpeechEngine.Id, Loc["mic.engine.windows"]),
            };
            OnPropertyChanged(nameof(Profiles));
            OnPropertyChanged(nameof(EngineChoices));
            SelectedProfile = Profiles.FirstOrDefault(p => p.Key == _settings.Current.SpeechProfile.ToString());
            SelectedEnglishEngine = EngineChoices.FirstOrDefault(e => e.Key == _settings.Current.ResolveEngine("en")) ?? EngineChoices[0];
            SelectedArabicEngine = EngineChoices.FirstOrDefault(e => e.Key == _settings.Current.ResolveEngine("ar")) ?? EngineChoices[0];
        }
        finally { _loading = false; }
    }

    private void RefreshEngines()
    {
        Engines.Clear();
        foreach (var s in _engines.States) Engines.Add(new EngineRowViewModel(s));
        RaiseSummary();
    }

    private void OnEnginesChanged() => UI.Post(RefreshEngines);

    private void OnModelsChanged() => UI.Post(() =>
    {
        foreach (var m in Models) m.Refresh();
        foreach (var m in WhisperModels) m.Refresh();
        RaiseSummary();
    });

    /// <summary>Banner action: downloads the selected Whisper model (the user asked for it by clicking).</summary>
    [RelayCommand]
    private async Task InstallWhisperAsync()
    {
        var item = WhisperModels.FirstOrDefault(m => m.Info.Id == _whisper.Selected.Id) ?? WhisperModels.FirstOrDefault();
        if (item != null) await item.DownloadAsync();
    }

    /// <summary>Banner action: keeps Arabic working with Vosk (comparison/fallback) instead of Whisper.</summary>
    [RelayCommand]
    private void UseVoskInstead()
    {
        foreach (var lang in WhisperStates(EngineStatus.MissingModel).Select(s => s.Language).ToList())
        {
            _settings.Current.EngineByLanguage ??= new();
            _settings.Current.EngineByLanguage[lang] = VoskEngine.Id;
        }
        _settings.Save();
        BuildOptions();
        RaiseSummary();
    }

    /// <summary>Lets the user point Whisper at their own ggml model file.</summary>
    [RelayCommand]
    private void ChooseWhisperModel()
    {
        var path = _dialogs.PickFile("Whisper ggml model (*.bin)|*.bin|All files (*.*)|*.*", Loc["whisper.choose.hint"]);
        if (path == null) return;
        var error = _whisper.UseFile(path);
        Message = error == null ? Loc["model.downloaded"] : WhisperModelItemViewModel.Describe(error);
    }

    [RelayCommand]
    private void UseDownloadedWhisper()
    {
        _whisper.ClearCustom();
        Message = "";
    }

    /// <summary>Banner action: discards the damaged model and downloads it again.</summary>
    [RelayCommand]
    private async Task RepairWhisperAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc["whisper.repair"], Loc["whisper.loadfailed"], Loc["whisper.repair"], Loc["common.cancel"])) return;
        _whisper.ClearCustom();
        var item = WhisperModels.FirstOrDefault(m => m.Info.Id == _whisper.Selected.Id);
        if (item == null) return;
        await Task.Run(() => _whisper.Remove(item.Info));
        await item.DownloadAsync();
    }

    [RelayCommand]
    private void RefreshDeviceList()
    {
        RefreshDevices();
        Message = HasDevices ? "" : Loc["mic.nodevices"];
    }

    [RelayCommand]
    private void ToggleTest()
    {
        if (IsTesting) StopTest(); else StartTest();
    }

    private void StartTest()
    {
        var error = _tester.Start(SelectedDevice?.Name);
        if (error != null)
        {
            Message = Loc.Localizer.Has("mic." + error) ? Loc["mic." + error] : Loc["mic.microphone-failed"];
            return;
        }
        Message = "";
        IsTesting = true;
    }

    /// <summary>Stops the level test (also called when the page is left).</summary>
    public void StopTest()
    {
        if (_tester.IsCapturing) _tester.Stop();
        IsTesting = false;
        Level = 0;
    }

    /// <summary>Lets the user point a language at an already-extracted Vosk model folder.</summary>
    [RelayCommand]
    private void UseFolder(string? language)
    {
        if (string.IsNullOrEmpty(language)) return;
        var path = _dialogs.PickFolder(Loc["model.usefolder.hint"]);
        if (path == null) return;
        var error = _models.UseFolder(language, path);
        Message = error == null ? Loc["model.downloaded"] : Loc[error];
    }

    protected override void OnLanguageChanged()
    {
        var device = SelectedDevice?.Name;
        RefreshDevices();
        BuildOptions();
        RefreshEngines();
        foreach (var m in Models) m.Refresh();
        foreach (var m in WhisperModels) m.Refresh();
        Message = "";
        base.OnLanguageChanged();
        _ = device;
    }

    public override void Dispose()
    {
        StopTest();
        _tester.Dispose();
        _engines.StatesChanged -= OnEnginesChanged;
        _models.Changed -= OnModelsChanged;
        _whisper.Changed -= OnModelsChanged;
        base.Dispose();
    }
}
