using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;

namespace VoiceCommander.App.ViewModels;

/// <summary>Local history page: every phrase heard and what happened. Text only — audio is never stored.</summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly IHistoryService _history;
    private readonly IRecognitionPipeline _pipeline;
    private readonly IDialogService _dialogs;
    private readonly List<HistoryItemViewModel> _all = new();

    public HistoryViewModel(IHistoryService history, IRecognitionPipeline pipeline, IDialogService dialogs)
    {
        _history = history; _pipeline = pipeline; _dialogs = dialogs;
        BuildFilters();
        _selectedFilter = Filters[0];
        foreach (var e in _history.Entries) _all.Add(new HistoryItemViewModel(e));
        _history.Added += OnAdded;
        _history.Cleared += OnCleared;
        ApplyFilter();
    }

    public IReadOnlyList<FilterOption> Filters { get; private set; } = Array.Empty<FilterOption>();
    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private FilterOption _selectedFilter;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasEntries;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(FilterOption value) => ApplyFilter();

    private void BuildFilters() => Filters = new List<FilterOption>
    {
        new("all", Loc["hist.filter.all"]),
        new("success", Loc["hist.filter.success"]),
        new("failed", Loc["hist.filter.failed"]),
        new("nomatch", Loc["hist.filter.nomatch"]),
    };

    private void OnAdded(HistoryEntry e) => UI.Post(() =>
    {
        var row = new HistoryItemViewModel(e);
        _all.Add(row);
        if (_all.Count > 500) _all.RemoveAt(0);
        ApplyFilter();
    });

    private void OnCleared() => UI.Post(() => { _all.Clear(); ApplyFilter(); });

    private bool Matches(HistoryItemViewModel r)
    {
        var o = r.Entry.Outcome;
        var ok = SelectedFilter.Key switch
        {
            "success" => o == ExecutionOutcome.Success,
            "failed" => o is ExecutionOutcome.Failed or ExecutionOutcome.Blocked or ExecutionOutcome.Cancelled,
            "nomatch" => o == ExecutionOutcome.NoMatch,
            _ => true,
        };
        if (!ok) return false;
        var q = Search.Trim();
        if (q.Length == 0) return true;
        return r.Heard.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (r.Entry.CommandName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Message.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        Items.Clear();
        // Newest first.
        for (var i = _all.Count - 1; i >= 0; i--)
            if (Matches(_all[i])) Items.Add(_all[i]);
        HasEntries = _all.Count > 0;
        IsEmpty = Items.Count == 0;
        CountText = Loc.Get("hist.count", Items.Count);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (_all.Count == 0) return;
        var yes = await _dialogs.ConfirmAsync(Loc["hist.clear"], Loc["hist.clear.body"], Loc["common.delete"], Loc["common.cancel"], danger: true);
        if (yes) _history.Clear();
    }

    /// <summary>Re-runs the entry's command through the same pipeline as the Test button.</summary>
    [RelayCommand]
    private async Task RunAgainAsync(HistoryItemViewModel? row)
    {
        if (row == null || string.IsNullOrEmpty(row.Entry.CommandId)) return;
        try { await Task.Run(() => _pipeline.TestAsync(row.Entry.CommandId!)); }
        catch (Exception) { /* the pipeline records failures in history itself */ }
    }

    protected override void OnLanguageChanged()
    {
        var key = SelectedFilter.Key;
        BuildFilters();
        OnPropertyChanged(nameof(Filters));
        SelectedFilter = Filters.First(f => f.Key == key);
        foreach (var r in _all) r.Refresh();
        CountText = Loc.Get("hist.count", Items.Count);
        base.OnLanguageChanged();
    }

    public override void Dispose()
    {
        _history.Added -= OnAdded;
        _history.Cleared -= OnCleared;
        base.Dispose();
    }
}
