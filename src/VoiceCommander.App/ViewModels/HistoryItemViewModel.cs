using CommunityToolkit.Mvvm.ComponentModel;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.App.ViewModels;

/// <summary>Display wrapper for a history entry. Parents call <see cref="Refresh"/> on a language switch.</summary>
public sealed class HistoryItemViewModel : ObservableObject
{
    public HistoryItemViewModel(HistoryEntry entry) { Entry = entry; }

    public HistoryEntry Entry { get; }
    public void Refresh() => OnPropertyChanged(string.Empty);

    public bool CanRunAgain => !string.IsNullOrEmpty(Entry.CommandId);
    public bool HasCommand => !string.IsNullOrEmpty(Entry.CommandName);
    /// <summary>Source · confidence · duration, skipping parts that have no value.</summary>
    public string MetaText => string.Join("  ·  ", new[] { SourceText, Confidence, DurationText }.Where(p => p.Length > 0 && p != "—"));
    public string Time => Entry.Timestamp.ToString("HH:mm:ss");
    public string DateTimeText => Entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string Heard => Entry.RecognizedText;
    public string CommandName => string.IsNullOrEmpty(Entry.CommandName) ? "—" : Entry.CommandName!;
    public string Message => Entry.Message;
    public string Confidence => Entry.Source == TriggerSource.Voice && Entry.Confidence > 0 ? $"{Entry.Confidence:P0}" : "—";
    public string DurationText => Entry.DurationMs > 0 ? $"{Entry.DurationMs} ms" : "";
    public string OutcomeText => LocSource.Instance["hist.result." + Entry.Outcome.ToString().ToLowerInvariant()];
    public string SourceText => LocSource.Instance["hist.source." + Entry.Source.ToString().ToLowerInvariant()];
    public string OutcomeBrush => Entry.Outcome switch
    {
        ExecutionOutcome.Success => "SuccessBrush",
        ExecutionOutcome.Failed => "DangerBrush",
        ExecutionOutcome.Blocked => "WarningBrush",
        _ => "TextMutedBrush",
    };
    public string OutcomeGlyph => Entry.Outcome switch
    {
        ExecutionOutcome.Success => "",
        ExecutionOutcome.Failed => "",
        ExecutionOutcome.Blocked => "",
        ExecutionOutcome.Cancelled => "",
        _ => "",
    };
}
