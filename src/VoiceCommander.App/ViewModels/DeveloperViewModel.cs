using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

public sealed record EngineRow(string Language, string Status, string Detail);

/// <summary>
/// Hidden developer page (Settings > Developer mode). Simulates phrases through the real pipeline, shows what the
/// matcher decides without running anything, and mirrors raw engine output and the live log.
/// </summary>
public sealed partial class DeveloperViewModel : ViewModelBase
{
    private const int MaxLines = 300;

    private readonly IRecognitionPipeline _pipeline;
    private readonly ICommandMatcher _matcher;
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly ISettingsService _settings;
    private readonly ISpeechEngineManager _engines;
    private readonly ILogService _log;

    public DeveloperViewModel(IRecognitionPipeline pipeline, ICommandMatcher matcher, ICommandRepository commands, IAppRegistry apps,
        ISettingsService settings, ISpeechEngineManager engines, ILogService log)
    {
        _pipeline = pipeline; _matcher = matcher; _commands = commands; _apps = apps;
        _settings = settings; _engines = engines; _log = log;

        _engines.Recognized += OnRaw;
        _engines.StatesChanged += OnStatesChanged;
        _log.Written += OnLog;
        _pipeline.Event += OnPipelineEvent;
        RefreshEngines();
    }

    public ObservableCollection<DetailRow> Details { get; } = new();
    public ObservableCollection<EngineRow> Engines { get; } = new();
    public ObservableCollection<string> RawLines { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private string _phrase = "";
    [ObservableProperty] private double _confidence = 1.0;
    [ObservableProperty] private bool _asVoice = true;
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private bool _isBusy;

    public string ConfidenceText => $"{Math.Round(Confidence * 100)}%";
    public bool HasDetails => Details.Count > 0;

    partial void OnConfidenceChanged(double value) => OnPropertyChanged(nameof(ConfidenceText));

    /// <summary>Runs the phrase through the whole pipeline exactly like speech (wake phrase, confidence, cooldown) or as direct input.</summary>
    [RelayCommand]
    private async Task Simulate()
    {
        var text = Phrase.Trim();
        if (text.Length == 0 || IsBusy) return;
        IsBusy = true;
        try
        {
            Analyze();
            var source = AsVoice ? TriggerSource.Voice : TriggerSource.Developer;
            var result = await Task.Run(() => _pipeline.ProcessAsync(text, Confidence, source));
            Outcome = $"{result.Stage}" + (string.IsNullOrEmpty(result.Message) ? "" : " · " + result.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Dry run: shows how the matcher reads the phrase. Nothing is executed.</summary>
    [RelayCommand]
    private void Analyze()
    {
        var text = Phrase.Trim();
        Details.Clear();
        if (text.Length == 0) { OnPropertyChanged(nameof(HasDetails)); return; }

        var normalized = TextNormalizer.Normalize(text);
        var numbers = NumberParser.ReplaceNumberWords(normalized);
        Details.Add(new DetailRow(Loc["dev.d.input"], text));
        Details.Add(new DetailRow(Loc["dev.d.normalized"], normalized));
        if (numbers != normalized) Details.Add(new DetailRow(Loc["dev.d.numbers"], numbers));

        var blockers = _commands.Commands.Where(c => !c.Enabled && CommandMatcher.IsDangerous(c));
        var evaluation = _matcher.Evaluate(text, _commands.ActiveCommands.Concat(blockers).ToList(), _apps.Apps, _settings.Current.SimilarityTolerance);
        var diag = evaluation.Diagnostics;
        var match = evaluation.Result;
        Details.Insert(0, new DetailRow(Loc["dev.d.original"], diag.OriginalText));
        Details.Add(new DetailRow(Loc["dev.d.speechconf"], $"{Math.Round(Confidence * 100)}%"));
        Details.Add(new DetailRow(Loc["dev.d.decision"], diag.Decision.ToString()));
        Details.Add(new DetailRow(Loc["dev.d.reason"], diag.Reason));
        Details.Add(new DetailRow(Loc["dev.d.best"], diag.BestCommandId == null ? "-" : $"{diag.BestCommandId} · {diag.BestPhrase} · {diag.BestScore:0.00}"));
        Details.Add(new DetailRow(Loc["dev.d.second"], diag.SecondBestCommandId == null ? "-" : $"{diag.SecondBestCommandId} · {diag.SecondBestPhrase} · {diag.SecondBestScore:0.00}"));
        if (match == null)
        {
            Details.Add(new DetailRow(Loc["dev.d.result"], Loc["dev.d.nomatch"]));
        }
        else
        {
            Details.Add(new DetailRow(Loc["dev.d.command"], CommandExecutor.DisplayName(match.Command, Loc.Localizer)));
            Details.Add(new DetailRow(Loc["dev.d.id"], match.Command.Id));
            Details.Add(new DetailRow(Loc["dev.d.phrase"], match.MatchedPhrase));
            Details.Add(new DetailRow(Loc["dev.d.kind"], match.IsExact ? Loc["dev.d.exact"] : Loc["dev.d.similar"]));
            Details.Add(new DetailRow(Loc["dev.d.score"], match.Score.ToString("0.00")));
            if (match.Number != null) Details.Add(new DetailRow(Loc["dev.d.number"], match.Number.Value.ToString()));
            if (match.App != null) Details.Add(new DetailRow(Loc["dev.d.app"], match.App.Name));
            Details.Add(new DetailRow(Loc["dev.d.actions"], match.Command.Actions.Count.ToString()));
        }
        Details.Add(new DetailRow(Loc["dev.d.threshold"], $"{Math.Round(_settings.Current.ConfidenceThreshold * 100)}%"));
        Details.Add(new DetailRow(Loc["dev.d.tolerance"], _settings.Current.SimilarityTolerance <= 0 ? Loc["dev.d.off"] : _settings.Current.SimilarityTolerance.ToString("0.00")));
        OnPropertyChanged(nameof(HasDetails));
    }

    [RelayCommand] private void ClearLog() => LogLines.Clear();
    [RelayCommand] private void ClearRaw() => RawLines.Clear();

    [RelayCommand]
    private async Task ReloadEngines() => await _engines.ReloadAsync();

    private void OnRaw(SpeechResult r) => UI.Post(() =>
        Append(RawLines, $"{DateTime.Now:HH:mm:ss}  [{r.Language}]  {Math.Round(r.Confidence * 100)}%  \"{r.Text}\""));

    private void OnLog(LogChannel channel, string line) => UI.Post(() => Append(LogLines, $"{channel,-7} {line}"));

    private void OnPipelineEvent(PipelineEvent e)
    {
        if (e.Stage == PipelineStage.Executed && e.Report != null)
            UI.Post(() => Outcome = $"{e.Report.Outcome} · {e.Report.Message} ({e.Report.DurationMs} ms, {e.Report.StepsCompleted}/{e.Report.StepsTotal})");
    }

    private void OnStatesChanged() => UI.Post(RefreshEngines);

    private void RefreshEngines()
    {
        Engines.Clear();
        foreach (var s in _engines.States)
            Engines.Add(new EngineRow(s.Language.ToUpperInvariant(), s.Status + (s.UsesGrammar ? " · grammar" : ""), s.Detail ?? ""));
    }

    private static void Append(ObservableCollection<string> list, string line)
    {
        list.Add(line);
        while (list.Count > MaxLines) list.RemoveAt(0);
    }

    protected override void OnLanguageChanged()
    {
        if (Details.Count > 0) Analyze();
        base.OnLanguageChanged();
    }

    public override void Dispose()
    {
        _engines.Recognized -= OnRaw;
        _engines.StatesChanged -= OnStatesChanged;
        _log.Written -= OnLog;
        _pipeline.Event -= OnPipelineEvent;
        base.Dispose();
    }
}
