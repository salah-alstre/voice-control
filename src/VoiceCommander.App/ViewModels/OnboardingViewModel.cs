using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

/// <summary>
/// First-run setup guide. Every choice is written straight to the real settings (the same ones the Settings page edits),
/// so closing the window at any point leaves the app in a consistent, usable state.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    public const int StepCount = 8; // welcome, language, profile, model, mic, mode, try, done

    private readonly ISettingsService _settings;
    private readonly IMicrophoneService _mic;
    private readonly ISpeechModelManager _models;
    private readonly IDialogService _dialogs;
    private readonly IRecognitionPipeline _pipeline;
    // Separate capture used only for the level meter: its audio never reaches the recognizer.
    private readonly MicrophoneService _tester;
    private bool _loading;

    public OnboardingViewModel(ISettingsService settings, IMicrophoneService mic, ISpeechModelManager models,
        IDialogService dialogs, IRecognitionPipeline pipeline, ILogService log)
    {
        _settings = settings; _mic = mic; _models = models; _dialogs = dialogs; _pipeline = pipeline;
        _tester = new MicrophoneService(log);
        _tester.LevelChanged += v => UI.Post(() => { if (IsStep(4)) Level = v; });
        _tester.Failed += _ => UI.Post(() => { StopMeter(); MicMessage = Loc["mic.microphone-failed"]; });

        Models = new ObservableCollection<ModelItemViewModel>(
            _models.Catalog.Select(m => new ModelItemViewModel(m, _models, _dialogs)));
        _models.Changed += OnModelsChanged;
        RefreshDevices();
        RefreshModelFilter();
    }

    /// <summary>Raised when the wizard should close (finished, or skipped on the last step).</summary>
    public event Action? CloseRequested;

    public ObservableCollection<DeviceOption> Devices { get; } = new();
    public ObservableCollection<ModelItemViewModel> Models { get; }
    public ObservableCollection<ModelItemViewModel> VisibleModels { get; } = new();

    [ObservableProperty] private int _step;
    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private float _level;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMicMessage))] private string _micMessage = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasModelMessage))] private string _modelMessage = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTryResult))] private string _tryResult = "";
    [ObservableProperty] private bool _isTrying;

    public bool HasMicMessage => MicMessage.Length > 0;
    public bool HasModelMessage => ModelMessage.Length > 0;
    public bool HasTryResult => TryResult.Length > 0;

    public bool IsStep(int n) => Step == n;
    public bool IsWelcome => Step == 0;
    public bool IsLanguageStep => Step == 1;
    public bool IsProfileStep => Step == 2;
    public bool IsModelStep => Step == 3;
    public bool IsMicStep => Step == 4;
    public bool IsModeStep => Step == 5;
    public bool IsTryStep => Step == 6;
    public bool IsDoneStep => Step == 7;

    public bool CanGoBack => Step > 0;
    public bool IsLast => Step == StepCount - 1;
    public bool CanSkipModel => IsModelStep;

    private static readonly string[] Keys = { "welcome", "language", "profile", "model", "mic", "mode", "try", "done" };
    public string StepKey => Keys[Math.Clamp(Step, 0, Keys.Length - 1)];
    public string StepTitle => Step == 0 ? Loc["onb.title"] : Loc["onb." + StepKey + ".title"];
    public string StepBody => Step == 6
        ? Loc.Get("onb.try.body", _settings.Current.PushToTalkHotkey)
        : Loc["onb." + StepKey + ".body"];
    public string StepCounter => Loc.Get("onb.step", Step + 1, StepCount);
    public string NextText => Loc[IsLast ? "common.finish" : "common.next"];
    public double Progress => (Step + 1) / (double)StepCount;

    // ---- choices (radio buttons read these, commands write them) -----------------------------------------

    public bool IsEnglishUi => _settings.Current.Language == "en";
    public bool IsArabicUi => _settings.Current.Language == "ar";
    public bool IsProfileEnglish => _settings.Current.SpeechProfile == SpeechProfile.English;
    public bool IsProfileArabic => _settings.Current.SpeechProfile == SpeechProfile.Arabic;
    public bool IsProfileMixed => _settings.Current.SpeechProfile == SpeechProfile.Mixed;
    public bool IsPushToTalk => _settings.Current.ListeningMode == ListeningMode.PushToTalk;
    public bool IsAlwaysListening => _settings.Current.ListeningMode == ListeningMode.AlwaysListening;

    [RelayCommand]
    private void SetLanguage(string? code)
    {
        if (code is not ("en" or "ar") || _settings.Current.Language == code) return;
        _settings.Current.Language = code;
        // The applier switches the UI language right away; the localized strings refresh through OnLanguageChanged.
        _settings.Save();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void SetProfile(string? name)
    {
        if (!Enum.TryParse<SpeechProfile>(name, out var profile)) return;
        _settings.Current.SpeechProfile = profile;
        _settings.Save();
        RefreshModelFilter();
        OnPropertyChanged(nameof(IsProfileEnglish)); OnPropertyChanged(nameof(IsProfileArabic)); OnPropertyChanged(nameof(IsProfileMixed));
    }

    [RelayCommand]
    private void SetMode(string? name)
    {
        if (!Enum.TryParse<ListeningMode>(name, out var mode)) return;
        _settings.Current.ListeningMode = mode;
        _settings.Save();
        OnPropertyChanged(nameof(IsPushToTalk)); OnPropertyChanged(nameof(IsAlwaysListening));
    }

    // ---- navigation --------------------------------------------------------------------------------------

    partial void OnStepChanged(int oldValue, int newValue)
    {
        if (oldValue == 4) StopMeter();
        if (newValue == 4) StartMeter();
        if (newValue == 6) TryResult = "";
        foreach (var p in new[]
        {
            nameof(IsWelcome), nameof(IsLanguageStep), nameof(IsProfileStep), nameof(IsModelStep), nameof(IsMicStep),
            nameof(IsModeStep), nameof(IsTryStep), nameof(IsDoneStep), nameof(CanGoBack), nameof(IsLast), nameof(CanSkipModel),
            nameof(StepKey), nameof(StepTitle), nameof(StepBody), nameof(StepCounter), nameof(NextText), nameof(Progress),
        }) OnPropertyChanged(p);
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLast) { Finish(); return; }
        Step++;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0) Step--;
    }

    /// <summary>Marks the guide as seen (finished or dismissed) so it does not reappear on the next start.</summary>
    public void Finish()
    {
        MarkSeen();
        CloseRequested?.Invoke();
    }

    /// <summary>Same as <see cref="Finish"/> without asking the window to close (used while it is already closing).</summary>
    public void MarkSeen()
    {
        StopMeter();
        if (!_settings.Current.OnboardingCompleted)
        {
            _settings.Current.OnboardingCompleted = true;
            _settings.Save();
        }
    }

    // ---- model step --------------------------------------------------------------------------------------

    private void RefreshModelFilter()
    {
        var profile = _settings.Current.SpeechProfile;
        VisibleModels.Clear();
        foreach (var m in Models)
        {
            var wanted = profile == SpeechProfile.Mixed
                || (profile == SpeechProfile.English && m.Info.Language == "en")
                || (profile == SpeechProfile.Arabic && m.Info.Language == "ar");
            if (wanted) VisibleModels.Add(m);
        }
    }

    private void OnModelsChanged() => UI.Post(() => { foreach (var m in Models) m.Refresh(); });

    [RelayCommand]
    private void UseFolder(string? language)
    {
        if (string.IsNullOrEmpty(language)) return;
        var path = _dialogs.PickFolder(Loc["model.usefolder.hint"]);
        if (path == null) return;
        var error = _models.UseFolder(language, path);
        ModelMessage = error == null ? Loc["model.downloaded"] : Loc[error];
    }

    // ---- microphone step ---------------------------------------------------------------------------------

    private void RefreshDevices()
    {
        _loading = true;
        try
        {
            var saved = _settings.Current.MicrophoneName;
            Devices.Clear();
            Devices.Add(new DeviceOption(null, Loc["mic.default"]));
            foreach (var d in _mic.GetDevices()) Devices.Add(new DeviceOption(d.Name, d.Name));
            if (!string.IsNullOrEmpty(saved) && Devices.All(d => d.Name != saved))
                Devices.Add(new DeviceOption(saved, Loc.Get("mic.notconnected", saved)));
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == (string.IsNullOrEmpty(saved) ? null : saved)) ?? Devices[0];
        }
        finally { _loading = false; }
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        if (_loading || value == null) return;
        _settings.Current.MicrophoneName = value.Name;
        _settings.Save();
        if (IsStep(4)) { StopMeter(); StartMeter(); }
    }

    private void StartMeter()
    {
        if (_tester.IsCapturing) return;
        var error = _tester.Start(SelectedDevice?.Name);
        if (error != null)
        {
            MicMessage = Loc.Localizer.Has("mic." + error) ? Loc["mic." + error] : Loc["mic.microphone-failed"];
            return;
        }
        MicMessage = Devices.Any(d => d.Name != null) ? "" : Loc["mic.nodevices"];
    }

    private void StopMeter()
    {
        if (_tester.IsCapturing) _tester.Stop();
        Level = 0;
    }

    // ---- try step ----------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task TryItAsync()
    {
        if (IsTrying) return;
        IsTrying = true;
        try
        {
            // Same path as a spoken phrase, minus the microphone: the matcher finds the registry command and executes it.
            var result = await _pipeline.ProcessAsync("open notepad", 1.0, TriggerSource.Developer);
            TryResult = result.Report?.Message
                        ?? (string.IsNullOrEmpty(result.Message) ? Loc["onb.try.nocommand"] : result.Message);
        }
        catch (Exception ex) { TryResult = ex.Message; }
        finally { IsTrying = false; }
    }

    protected override void OnLanguageChanged()
    {
        _loading = true;
        try { RefreshDevices(); }
        finally { _loading = false; }
        foreach (var m in Models) m.Refresh();
        MicMessage = ""; ModelMessage = "";
        base.OnLanguageChanged();
    }

    public override void Dispose()
    {
        StopMeter();
        _tester.Dispose();
        _models.Changed -= OnModelsChanged;
        base.Dispose();
    }
}
