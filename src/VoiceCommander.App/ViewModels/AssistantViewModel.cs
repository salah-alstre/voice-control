using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

/// <summary>
/// View-model of the floating mini assistant. It only renders <see cref="IAssistantStateService"/> - the same state the
/// dashboard shows - and owns no microphone, speech engine or pipeline of its own.
/// </summary>
public sealed partial class AssistantViewModel : ViewModelBase
{
    private readonly IAssistantStateService _state;
    private readonly ISettingsService _settings;
    private readonly IListeningService _listening;
    private readonly IRecoveryService _recovery;
    private AssistantSnapshot _snap;
    private readonly Action<AssistantSnapshot> _onState;
    private readonly Action _onSettings;
    private bool _disposed;

    public AssistantViewModel(IAssistantStateService state, ISettingsService settings, IListeningService listening, IRecoveryService recovery)
    {
        _state = state; _settings = settings; _listening = listening; _recovery = recovery;
        _snap = state.Current;
        _onState = s => UI.Post(() => { if (!_disposed) Apply(s); });
        _onSettings = () => UI.Post(() => { if (!_disposed) RefreshSettings(); });
        _state.Changed += _onState;
        _settings.Changed += _onSettings;
        _isCollapsed = settings.Current.AssistantCollapsed;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PillText))] private bool _isCollapsed;

    public AssistantPhase Phase => _snap.Phase;
    public bool IsListening => _snap.Phase == AssistantPhase.Listening;
    public bool IsReady => _snap.Phase == AssistantPhase.Ready;
    public bool IsBusy => _snap.Phase is AssistantPhase.Processing or AssistantPhase.Executing;
    public bool IsSuccess => _snap.Phase == AssistantPhase.Success;
    public bool IsProblem => _snap.Phase is AssistantPhase.NoMatch or AssistantPhase.Error;
    public bool CanLocate => _snap.Phase == AssistantPhase.Error && _snap.CanLocate;

    public bool Topmost => _settings.Current.AssistantAlwaysOnTop;
    public double WindowOpacity => _settings.Current.AssistantOpacity;

    public string Title => Loc["assistant.title"];

    /// <summary>The big line: what the assistant is doing.</summary>
    public string PrimaryText
    {
        get
        {
            var showExec = _settings.Current.AssistantShowExecutionStatus;
            switch (_snap.Phase)
            {
                case AssistantPhase.Listening: return Loc["assistant.listening"];
                case AssistantPhase.Processing:
                    return _snap.HeardText.Length > 0 && ShowHeard ? Loc["assistant.matching"] : Loc["assistant.processing"];
                case AssistantPhase.Executing:
                    if (!showExec) return Loc["assistant.processing"];
                    return IsAppTarget ? Loc.Get("assistant.opening", _snap.TargetName!) : Loc.Get("assistant.running", _snap.TargetName ?? _snap.CommandName ?? "");
                case AssistantPhase.Success:
                    if (!showExec) return Loc["assistant.done"];
                    if (IsAppTarget) return Loc.Get("assistant.opened", _snap.TargetName!);
                    return Loc.Get("assistant.success", Clean(_snap.Message) is { Length: > 0 } m ? m : (_snap.TargetName ?? _snap.CommandName ?? ""));
                case AssistantPhase.NoMatch: return Loc["assistant.nomatch"];
                case AssistantPhase.Error:
                    if (!string.IsNullOrEmpty(_snap.ErrorKey) && Loc.Localizer.Has(_snap.ErrorKey!)) return Loc[_snap.ErrorKey!];
                    return string.IsNullOrWhiteSpace(_snap.Message) ? Loc["assistant.error"] : _snap.Message!;
                default: return Loc["assistant.ready"];
            }
        }
    }

    /// <summary>The small muted line under it.</summary>
    public string SecondaryText
    {
        get
        {
            switch (_snap.Phase)
            {
                case AssistantPhase.Ready:
                    if (_settings.Current.ListeningMode == ListeningMode.PushToTalk)
                        return Loc.Get("assistant.hint", PrettyHotkey(_settings.Current.PushToTalkHotkey));
                    return Loc["assistant.hint.always"];
                case AssistantPhase.Listening:
                    return _settings.Current.ListeningMode == ListeningMode.PushToTalk ? Loc["assistant.speaknow"] : Loc["assistant.hint.always"];
                case AssistantPhase.Processing: return Loc["assistant.processing.sub"];
                case AssistantPhase.Executing:
                    return ShowCommandName ? _snap.CommandName! : "";
                case AssistantPhase.Success: return ShowCommandName ? _snap.CommandName! : "";
                case AssistantPhase.Error:
                    return CanLocate ? "" : (string.IsNullOrEmpty(_snap.ErrorKey) ? "" : "");
                default: return "";
            }
        }
    }

    public bool HasSecondary => SecondaryText.Length > 0;

    /// <summary>"Heard: Open CS2" - shown while the recognized text is relevant and the user wants to see it.</summary>
    public string HeardLine => ShowHeard ? Loc.Get("assistant.heard", _snap.HeardText) : "";
    public bool ShowHeard => _settings.Current.AssistantShowRecognizedText && _snap.HeardText.Length > 0
                             && _snap.Phase is not (AssistantPhase.Ready or AssistantPhase.Listening);

    private bool ShowCommandName => _snap.Phase is AssistantPhase.Executing or AssistantPhase.Success
                                    && !string.IsNullOrEmpty(_snap.CommandName)
                                    && !string.Equals(_snap.CommandName, _snap.TargetName, StringComparison.OrdinalIgnoreCase);

    private bool IsAppTarget => !string.IsNullOrEmpty(_snap.TargetName) && !string.Equals(_snap.TargetName, _snap.CommandName, StringComparison.OrdinalIgnoreCase)
                                || !string.IsNullOrEmpty(_snap.TargetIconPath);

    /// <summary>Icon of the application being acted on (cached PNG); null shows the microphone glyph.</summary>
    public ImageSource? Icon => _snap.Phase is AssistantPhase.Executing or AssistantPhase.Success or AssistantPhase.Error
        ? IconCache.Load(_snap.TargetIconPath) : null;
    public bool HasIcon => Icon != null;

    public string PillText => _snap.Phase switch
    {
        AssistantPhase.Listening => Loc["assistant.listening"],
        AssistantPhase.Processing => Loc["assistant.processing"],
        AssistantPhase.Executing => Loc["assistant.pill.working"],
        AssistantPhase.Success => "✓",
        AssistantPhase.NoMatch => "?",
        AssistantPhase.Error => "!",
        _ => Loc["assistant.ready"],
    };

    /// <summary>Theme brush key for the status dot / accent of the current phase.</summary>
    public string StatusBrush => _snap.Phase switch
    {
        AssistantPhase.Listening => "SuccessBrush",
        AssistantPhase.Processing or AssistantPhase.Executing => "AccentBrush",
        AssistantPhase.Success => "SuccessBrush",
        AssistantPhase.NoMatch => "WarningBrush",
        AssistantPhase.Error => "DangerBrush",
        _ => "TextMutedBrush",
    };

    public string LocateLabel => Loc["assistant.locate"];
    public string CollapseTip => Loc["assistant.collapse"];
    public string ExpandTip => Loc["assistant.expand"];
    public string CloseTip => Loc["assistant.close"];
    public string SettingsTip => Loc["assistant.settings"];

    public event Action? OpenSettingsRequested;
    public event Action? CloseRequested;

    private void Apply(AssistantSnapshot s)
    {
        var before = _snap;
        _snap = s;
        RaiseAll();
        if (before.Phase != s.Phase) PhaseChanged?.Invoke(before.Phase, s.Phase);
    }

    /// <summary>Raised (UI thread) when the phase changes, for window-level behaviour (auto collapse, waveform, hide timers).</summary>
    public event Action<AssistantPhase, AssistantPhase>? PhaseChanged;

    private void RefreshSettings()
    {
        OnPropertyChanged(nameof(Topmost)); OnPropertyChanged(nameof(AlwaysOnTop)); OnPropertyChanged(nameof(WindowOpacity));
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var n in new[]
        {
            nameof(Phase), nameof(IsListening), nameof(IsReady), nameof(IsBusy), nameof(IsSuccess), nameof(IsProblem), nameof(CanLocate),
            nameof(PrimaryText), nameof(SecondaryText), nameof(HasSecondary), nameof(HeardLine), nameof(ShowHeard),
            nameof(Icon), nameof(HasIcon), nameof(PillText), nameof(StatusBrush),
        }) OnPropertyChanged(n);
    }

    protected override void OnLanguageChanged() => OnPropertyChanged(string.Empty);

    partial void OnIsCollapsedChanged(bool value)
    {
        if (_settings.Current.AssistantCollapsed == value) return;
        _settings.Current.AssistantCollapsed = value;
        _settings.Save();
    }

    [RelayCommand] private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;
    [RelayCommand] private void Expand() => IsCollapsed = false;
    [RelayCommand] private void Collapse() => IsCollapsed = true;
    [RelayCommand] private void OpenSettings() => OpenSettingsRequested?.Invoke();
    [RelayCommand] private void Close() => CloseRequested?.Invoke();

    /// <summary>Takes the user to the Applications page, where the missing executable can be located.</summary>
    [RelayCommand] private void Locate() => LocateRequested?.Invoke();

    public event Action? LocateRequested;

    [RelayCommand]
    private void ToggleAlwaysOnTop()
    {
        _settings.Current.AssistantAlwaysOnTop = !_settings.Current.AssistantAlwaysOnTop;
        _settings.Save();
    }

    public bool AlwaysOnTop => _settings.Current.AssistantAlwaysOnTop;

    private static string Clean(string? message) => (message ?? "").Trim().TrimEnd('.', '。');

    /// <summary>"Ctrl+Space" -> "Ctrl + Space".</summary>
    private static string PrettyHotkey(string hotkey) => string.Join(" + ", hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    public override void Dispose()
    {
        _disposed = true;
        _state.Changed -= _onState;
        _settings.Changed -= _onSettings;
        base.Dispose();
    }
}
