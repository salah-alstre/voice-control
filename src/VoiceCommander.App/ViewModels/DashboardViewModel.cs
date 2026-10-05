using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

public sealed class EngineRowViewModel
{
    public EngineRowViewModel(LanguageEngineState state) { State = state; }
    public LanguageEngineState State { get; }
    public string Language => LocSource.Instance["lang." + State.Language];
    public string Status => LocSource.Instance["engine." + State.Status.ToString().ToLowerInvariant()]
        + (State.Status == EngineStatus.Ready ? " · " + LocSource.Instance[State.UsesGrammar ? "engine.grammar.on" : "engine.grammar.off"] : "");
    public string Detail => State.Detail ?? "";
    public bool HasDetail => !string.IsNullOrEmpty(State.Detail);
    public string Brush => State.Status switch
    {
        EngineStatus.Ready => "SuccessBrush",
        EngineStatus.Loading or EngineStatus.NotLoaded => "TextMutedBrush",
        EngineStatus.MissingModel => "WarningBrush",
        _ => "DangerBrush",
    };
}

/// <summary>
/// Dashboard: one voice status card driven by <see cref="IAssistantStateService"/> (the same state the floating assistant
/// shows), a few stats and the recent activity. Developer diagnostics live on the Developer page.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IAssistantStateService _state;
    private readonly IListeningService _listening;
    private readonly ISpeechEngineManager _engines;
    private readonly IRecognitionPipeline _pipeline;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly IRecoveryService _recovery;
    private readonly IAssistantController _assistant;
    private readonly MainViewModel _main;
    private AssistantSnapshot _snap;

    public DashboardViewModel(IAssistantStateService state, IListeningService listening, ISpeechEngineManager engines,
        IRecognitionPipeline pipeline, ICommandRepository commands, IAppRegistry apps, IHistoryService history,
        ISettingsService settings, IRecoveryService recovery, IAssistantController assistant, MainViewModel main)
    {
        _state = state; _listening = listening; _engines = engines; _pipeline = pipeline; _commands = commands; _apps = apps;
        _history = history; _settings = settings; _recovery = recovery; _assistant = assistant; _main = main;
        _snap = state.Current;

        _state.Changed += s => UI.Post(() => Apply(s));
        _listening.StateChanged += () => UI.Post(RefreshHeader);
        _engines.StatesChanged += () => UI.Post(RefreshSetup);
        _pipeline.Event += e => UI.Post(() => OnPipeline(e));
        _commands.Changed += () => UI.Post(RefreshCounts);
        _apps.Changed += () => UI.Post(RefreshCounts);
        _history.Added += e => UI.Post(() => { AddRecent(e); RefreshCounts(); });
        _history.Cleared += () => UI.Post(() => { Recent.Clear(); RefreshCounts(); });
        _settings.Changed += () => UI.Post(() => { RefreshHeader(); RaiseCard(); });

        foreach (var e in _history.Entries.TakeLast(6).Reverse()) Recent.Add(new HistoryItemViewModel(e));
        RefreshHeader(); RefreshSetup(); RefreshCounts();
    }

    public ObservableCollection<HistoryItemViewModel> Recent { get; } = new();

    [ObservableProperty] private int _commandCount;
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private int _todayCount;
    [ObservableProperty] private bool _needsSetup;
    [ObservableProperty] private RecoveryAction? _pendingRecovery;

    // ---- header -------------------------------------------------------------------------------------------------------
    public bool IsEnabled => _listening.IsEnabled;
    public bool IsListening => _listening.State == ListeningState.Listening;
    public bool IsPushToTalk => _settings.Current.ListeningMode == ListeningMode.PushToTalk;
    public string ToggleText => Loc[IsEnabled ? "dash.stop" : "dash.start"];
    public string MicrophoneText => string.IsNullOrEmpty(_settings.Current.MicrophoneName) ? Loc["dash.mic.default"] : _settings.Current.MicrophoneName!;
    public string StatusWord => Loc[IsListening ? "dash.status.listening" : "dash.status.standby"];
    public string StatusDot => IsListening ? "●" : "○";
    public string StatusDotBrush => IsListening ? "SuccessBrush" : "TextMutedBrush";

    // ---- voice status card ---------------------------------------------------------------------------------------------
    public AssistantPhase Phase => _snap.Phase;
    public bool CardListening => _snap.Phase == AssistantPhase.Listening;
    public bool CardReady => _snap.Phase == AssistantPhase.Ready;
    public bool CardSuccess => _snap.Phase == AssistantPhase.Success;
    public bool CardProblem => _snap.Phase is AssistantPhase.NoMatch or AssistantPhase.Error;
    public bool CardBusy => _snap.Phase is AssistantPhase.Processing or AssistantPhase.Executing;
    public bool HasRecovery => PendingRecovery != null && _snap.Phase == AssistantPhase.Error;
    public string RecoveryLabel => PendingRecovery?.Label ?? "";

    public string CardTitle
    {
        get
        {
            switch (_snap.Phase)
            {
                case AssistantPhase.Listening: return Loc["dash.card.listening"];
                case AssistantPhase.Processing: return Loc["dash.card.processing"];
                case AssistantPhase.Executing: return Loc.Get("dash.card.executing", Subject);
                case AssistantPhase.Success: return Loc["dash.card.completed"];
                case AssistantPhase.NoMatch: return Loc["dash.card.nomatch"];
                case AssistantPhase.Error:
                    if (!string.IsNullOrEmpty(_snap.ErrorKey) && Loc.Localizer.Has(_snap.ErrorKey!)) return Loc[_snap.ErrorKey!];
                    return string.IsNullOrWhiteSpace(_snap.Message) ? Loc["dash.card.error"] : _snap.Message!;
                default:
                    return _listening.State switch
                    {
                        ListeningState.Loading => Loc["dash.state.loading"],
                        ListeningState.Stopped => Loc["dash.state.stopped"],
                        ListeningState.Error => !string.IsNullOrEmpty(_listening.ErrorKey) && Loc.Localizer.Has(_listening.ErrorKey!) ? Loc[_listening.ErrorKey!] : Loc["dash.state.error"],
                        _ => Loc["dash.card.ready"],
                    };
            }
        }
    }

    public string CardSubtitle
    {
        get
        {
            switch (_snap.Phase)
            {
                case AssistantPhase.Listening: return Loc["dash.speaking"];
                case AssistantPhase.Success: return Subject;
                case AssistantPhase.Ready:
                    if (_listening.State is ListeningState.Loading or ListeningState.Stopped or ListeningState.Error) return "";
                    return IsPushToTalk ? Loc.Get("dash.card.hint", PrettyHotkey(_settings.Current.PushToTalkHotkey)) : Loc["dash.card.hint.always"];
                case AssistantPhase.NoMatch:
                case AssistantPhase.Error:
                    return string.IsNullOrWhiteSpace(_snap.Message) || _snap.Phase == AssistantPhase.Error ? "" : _snap.Message!;
                default: return "";
            }
        }
    }

    public bool HasSubtitle => CardSubtitle.Length > 0;

    /// <summary>Heard: "Open Discord" - visible from the moment speech was recognized until the card returns to Ready.</summary>
    public string HeardLine => Loc.Get("dash.card.heard", _snap.HeardText);
    public bool ShowHeard => _snap.HeardText.Length > 0 && _snap.Phase is not (AssistantPhase.Ready or AssistantPhase.Listening);

    public ImageSource? CardIcon => _snap.Phase is AssistantPhase.Executing or AssistantPhase.Success or AssistantPhase.Error
        ? IconCache.Load(_snap.TargetIconPath) : null;
    public bool HasCardIcon => CardIcon != null;

    public string CardBrush => _snap.Phase switch
    {
        AssistantPhase.Listening or AssistantPhase.Success => "SuccessBrush",
        AssistantPhase.Processing or AssistantPhase.Executing => "AccentBrush",
        AssistantPhase.NoMatch => "WarningBrush",
        AssistantPhase.Error => "DangerBrush",
        _ => _listening.State == ListeningState.Error ? "DangerBrush" : "TextMutedBrush",
    };

    private string Subject => !string.IsNullOrEmpty(_snap.CommandName) ? _snap.CommandName! : _snap.TargetName ?? "";

    // ---- plumbing -----------------------------------------------------------------------------------------------------
    private void Apply(AssistantSnapshot s)
    {
        _snap = s;
        if (s.Phase is AssistantPhase.Ready or AssistantPhase.Listening or AssistantPhase.Processing) PendingRecovery = null;
        RaiseCard();
    }

    private void RaiseCard()
    {
        foreach (var n in new[]
        {
            nameof(Phase), nameof(CardListening), nameof(CardReady), nameof(CardSuccess), nameof(CardProblem), nameof(CardBusy),
            nameof(CardTitle), nameof(CardSubtitle), nameof(HasSubtitle), nameof(HeardLine), nameof(ShowHeard),
            nameof(CardIcon), nameof(HasCardIcon), nameof(CardBrush), nameof(HasRecovery), nameof(RecoveryLabel),
        }) OnPropertyChanged(n);
    }

    partial void OnPendingRecoveryChanged(RecoveryAction? value) { OnPropertyChanged(nameof(HasRecovery)); OnPropertyChanged(nameof(RecoveryLabel)); }

    private void RefreshHeader()
    {
        foreach (var n in new[]
        {
            nameof(IsEnabled), nameof(IsListening), nameof(IsPushToTalk), nameof(ToggleText), nameof(MicrophoneText),
            nameof(StatusWord), nameof(StatusDot), nameof(StatusDotBrush),
        }) OnPropertyChanged(n);
        RaiseCard();
    }

    private void RefreshSetup() =>
        NeedsSetup = _engines.States.Count > 0 && !_engines.IsReady && _engines.States.All(s => s.Status == EngineStatus.MissingModel);

    private void RefreshCounts()
    {
        CommandCount = _commands.ActiveCommands.Count;
        AppCount = _apps.Apps.Count;
        TodayCount = _history.Entries.Count(e => e.Timestamp.ToLocalTime().Date == DateTime.Today);
    }

    private void AddRecent(HistoryEntry e)
    {
        Recent.Insert(0, new HistoryItemViewModel(e));
        while (Recent.Count > 6) Recent.RemoveAt(Recent.Count - 1);
    }

    private void OnPipeline(PipelineEvent e)
    {
        if (e.Stage == PipelineStage.Heard) PendingRecovery = null;
        else if (e.Stage == PipelineStage.Executed) PendingRecovery = e.Report?.Recovery;
    }

    protected override void OnLanguageChanged()
    {
        foreach (var r in Recent) r.Refresh();
        base.OnLanguageChanged();
    }

    private static string PrettyHotkey(string hotkey) =>
        string.Join(" + ", hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    // ---- commands -----------------------------------------------------------------------------------------------------
    [RelayCommand] private void ToggleListening() => _listening.Toggle();
    [RelayCommand] private void OpenAssistant() => _assistant.Open();
    [RelayCommand] private void OpenSetup() => _main.Navigate("microphone");
    [RelayCommand] private void OpenHistory() => _main.Navigate("history");

    [RelayCommand]
    private void RunRecovery()
    {
        if (PendingRecovery != null && _recovery.Run(PendingRecovery)) PendingRecovery = null;
    }

    public void PushToTalkDown() => _listening.PushToTalkDown();
    public void PushToTalkUp() => _listening.PushToTalkUp();
}
