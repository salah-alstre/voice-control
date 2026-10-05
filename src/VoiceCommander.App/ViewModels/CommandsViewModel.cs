using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.ViewModels;

public sealed record FilterOption(string Key, string Label);

/// <summary>A category chip (All / Applications / Audio / Windows / Custom / Workflows). Exactly one is selected.</summary>
public sealed partial class ChipOptionViewModel : ObservableObject
{
    private readonly Action<ChipOptionViewModel> _onSelected;

    public ChipOptionViewModel(string key, string label, bool selected, Action<ChipOptionViewModel> onSelected)
    {
        Key = key; Label = label; _isSelected = selected; _onSelected = onSelected;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) { if (value) _onSelected(this); }
}

/// <summary>One enable/disable switch for a built-in command pack.</summary>
public sealed partial class PackItemViewModel : ObservableObject
{
    private readonly ICommandRepository _repo;
    private readonly string _id;
    private bool _busy;

    public PackItemViewModel(CommandPack pack, ICommandRepository repo, string name, string description, int count, string countText)
    {
        _repo = repo; _id = pack.Id;
        Icon = pack.Icon; Name = name; Description = description; CountText = countText;
        _enabled = repo.IsPackEnabled(pack.Id);
    }

    public string Icon { get; }
    public string Name { get; }
    public string Description { get; }
    public string CountText { get; }

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (_busy) return;
        _busy = true;
        try { _repo.SetPackEnabled(_id, value); }
        finally { _busy = false; }
    }
}

/// <summary>A row in the command list. Wraps a command from the repository; edits go through the parent.</summary>
public sealed partial class CommandItemViewModel : ObservableObject
{
    private readonly CommandsViewModel _parent;

    public CommandItemViewModel(VoiceCommand command, CommandsViewModel parent, string name, string description,
        string phrases, string actions, bool dangerous, string? duplicate, string category, ImageSource? icon)
    {
        _parent = parent;
        CategoryText = category; AppIcon = icon;
        Command = command;
        Name = name; Description = description; PhrasesText = phrases; ActionsText = actions;
        IsDangerous = dangerous; DuplicateText = duplicate;
        _enabled = command.Enabled;
    }

    public VoiceCommand Command { get; }
    public string Id => Command.Id;
    public string Name { get; }
    public string Description { get; }
    public string PhrasesText { get; }
    public string ActionsText { get; }
    public bool IsDangerous { get; }
    public string CategoryText { get; }
    /// <summary>Real icon of the application the command opens (cached); null falls back to the glyph tile.</summary>
    public ImageSource? AppIcon { get; }
    public bool HasAppIcon => AppIcon != null;
    public bool ShowGlyph => AppIcon == null;
    public bool IsBuiltIn => Command.IsBuiltIn;
    public bool IsCustom => !Command.IsBuiltIn;
    public string Glyph => string.IsNullOrEmpty(Command.Icon) ? "" : Command.Icon;
    public string TestLabel => _parent.TestLabel;
    public string? StepsText => Command.IsWorkflow ? LocSource.Instance.Get("wf.steps", Command.Actions.Count) : null;
    public bool HasSteps => Command.IsWorkflow;
    public string? DuplicateText { get; }
    public bool HasDuplicate => !string.IsNullOrEmpty(DuplicateText);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _testing;
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _resultBrush = "TextMutedBrush";
    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    partial void OnEnabledChanged(bool value)
    {
        _parent.SetEnabled(this, value);
    }

    public void SetResult(string text, string brush) { ResultText = text; ResultBrush = brush; }

    [RelayCommand] private Task TestAsync() => _parent.TestAsync(this);
    [RelayCommand] private Task EditAsync() => _parent.EditAsync(this);
    [RelayCommand] private Task DeleteAsync() => _parent.DeleteAsync(this);
    [RelayCommand] private void Duplicate() => _parent.Duplicate(this);
}

/// <summary>Lists every command (built-in and custom) with search, filters, packs and per-row test/edit/delete.</summary>
public partial class CommandsViewModel : ViewModelBase
{
    private readonly ICommandRepository _repo;
    private readonly IActionHandlerRegistry _registry;
    private readonly IRecognitionPipeline _pipeline;
    private readonly ICommandEditorService _editor;
    private readonly IDialogService _dialogs;
    private readonly IAppRegistry _apps;
    private readonly IIconService _icons;
    private readonly bool _workflowsOnly;
    private readonly Dictionary<string, (string Text, string Brush)> _lastResults = new();
    private List<CommandItemViewModel> _all = new();
    private bool _loading;

    protected CommandsViewModel(ICommandRepository repo, IActionHandlerRegistry registry, IRecognitionPipeline pipeline,
        ICommandEditorService editor, IDialogService dialogs, IAppRegistry apps, IIconService icons, bool workflowsOnly)
    {
        _repo = repo; _registry = registry; _pipeline = pipeline; _editor = editor; _dialogs = dialogs;
        _apps = apps; _icons = icons;
        _workflowsOnly = workflowsOnly;
        BuildFilters();
        _selectedFilter = Filters[0];
        _selectedGroup = Groups[0];
        _repo.Changed += OnRepoChanged;
        Reload();
    }

    public CommandsViewModel(ICommandRepository repo, IActionHandlerRegistry registry, IRecognitionPipeline pipeline,
        ICommandEditorService editor, IDialogService dialogs, IAppRegistry apps, IIconService icons)
        : this(repo, registry, pipeline, editor, dialogs, apps, icons, false) { }

    public ObservableCollection<CommandItemViewModel> Items { get; } = new();
    public ObservableCollection<PackItemViewModel> Packs { get; } = new();
    public IReadOnlyList<FilterOption> Filters { get; private set; } = Array.Empty<FilterOption>();
    public IReadOnlyList<FilterOption> Groups { get; private set; } = Array.Empty<FilterOption>();
    public IReadOnlyList<ChipOptionViewModel> Chips { get; private set; } = Array.Empty<ChipOptionViewModel>();
    public bool ShowChips => !_workflowsOnly;
    public bool ShowPacks => !_workflowsOnly;
    public bool IsWorkflowPage => _workflowsOnly;
    public string PageTitle => Loc[_workflowsOnly ? "wf.title" : "cmd.title"];
    public string PageSubtitle => Loc[_workflowsOnly ? "wf.subtitle" : "cmd.subtitle"];
    public string NewLabel => Loc[_workflowsOnly ? "wf.new" : "cmd.new"];
    public string EmptyText => Loc[_workflowsOnly ? "wf.empty" : "cmd.empty"];
    internal string TestLabel => Loc[_workflowsOnly ? "wf.run" : "common.test"];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private FilterOption _selectedFilter;
    [ObservableProperty] private FilterOption _selectedGroup;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _packsExpanded;
    [NotifyPropertyChangedFor(nameof(HasDuplicates))]
    [ObservableProperty] private string _duplicatesText = "";
    public bool HasDuplicates => !string.IsNullOrEmpty(DuplicatesText);
    public bool IsEmpty => Items.Count == 0;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(FilterOption value) => ApplyFilter();
    partial void OnSelectedGroupChanged(FilterOption value) => ApplyFilter();

    private void BuildFilters()
    {
        Filters = new List<FilterOption>
        {
            new("all", Loc["cmd.filter.all"]),
            new("enabled", Loc["cmd.filter.enabled"]),
            new("disabled", Loc["cmd.filter.disabled"]),
            new("builtin", Loc["cmd.filter.builtin"]),
            new("custom", Loc["cmd.filter.custom"]),
            new("dangerous", Loc["cmd.filter.dangerous"]),
        };
        Groups = new List<FilterOption>
        {
            new("all", Loc["cmd.group.all"]),
            new(nameof(CommandCategory.Applications), Loc["group.apps"]),
            new(nameof(CommandCategory.System), Loc["group.system"]),
            new(nameof(CommandCategory.Audio), Loc["group.audio"]),
            new(nameof(CommandCategory.Custom), Loc["cmd.custom"]),
            new("workflows", Loc["nav.workflows"]),
        };
        // Spec order: All / Applications / Audio / Windows / Custom / Workflows.
        var order = new[] { "all", nameof(CommandCategory.Applications), nameof(CommandCategory.Audio),
            nameof(CommandCategory.System), nameof(CommandCategory.Custom), "workflows" };
        var current = SelectedGroup?.Key ?? "all";
        Chips = order.Select(k => new ChipOptionViewModel(k, k == "all" ? Loc["cmd.filter.all"] : Groups.First(g => g.Key == k).Label,
            k == current, OnChipSelected)).ToList();
    }

    private void OnChipSelected(ChipOptionViewModel chip)
    {
        var g = Groups.FirstOrDefault(x => x.Key == chip.Key);
        if (g != null && !ReferenceEquals(g, SelectedGroup)) SelectedGroup = g;
    }

    private void OnRepoChanged() => UI.Post(Reload);

    protected override void OnLanguageChanged()
    {
        var f = SelectedFilter.Key; var g = SelectedGroup.Key;
        BuildFilters();
        OnPropertyChanged(nameof(Filters)); OnPropertyChanged(nameof(Groups)); OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(NewLabel)); OnPropertyChanged(nameof(EmptyText));
        _loading = true;
        SelectedFilter = Filters.First(x => x.Key == f);
        SelectedGroup = Groups.First(x => x.Key == g);
        _loading = false;
        Reload();
    }

    private string Describe(VoiceCommand c)
    {
        if (!string.IsNullOrEmpty(c.DescriptionKey) && Loc.Localizer.Has(c.DescriptionKey)) return Loc[c.DescriptionKey];
        return c.Description ?? "";
    }

    private string CategoryLabel(CommandCategory c) => c switch
    {
        CommandCategory.Applications => Loc["group.apps"],
        CommandCategory.System => Loc["group.system"],
        CommandCategory.Audio => Loc["group.audio"],
        _ => Loc["cmd.custom"],
    };

    /// <summary>Icon of the first application the command's steps reference; extracted once and cached by <see cref="IconCache"/>.</summary>
    private ImageSource? AppIconFor(VoiceCommand c)
    {
        try
        {
            var raw = c.Actions.FirstOrDefault(a => a.Parameters.ContainsKey("app"))?.Get("app", "");
            if (string.IsNullOrEmpty(raw) || raw == "{app}") return null;
            var app = _apps.FindById(raw) ?? _apps.FindByName(raw);
            return app == null ? null : IconCache.Load(_icons.GetIconPath(app));
        }
        catch { return null; }
    }

    private string ActionsSummary(VoiceCommand c)
    {
        var names = c.Actions.Select(a => _registry.Find(a.Type)?.Descriptor is { } d ? Loc[d.NameKey] : a.Type).ToList();
        return names.Count == 0 ? "—" : string.Join("  →  ", names);
    }

    private string PhrasesSummary(VoiceCommand c)
    {
        // WPF's bidi ignores the newer isolate characters, so each phrase is fenced with a direction mark of the UI
        // language (RLM in Arabic, LRM in English); mixed English/Arabic lists then read in order, first phrase first.
        var mark = Loc.Localizer.IsRtl ? "‏" : "‎";
        var shown = c.Phrases.Take(3).Select(p => mark + "“" + p + "”" + mark);
        var more = c.Phrases.Count - 3;
        return string.Join("   ", shown) + (more > 0 ? "   ‎+" + more + "‎" : "");
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            var source = _repo.Commands.Where(c => !_workflowsOnly || c.IsWorkflow).ToList();
            var dupes = CommandMatcher.FindDuplicates(source.Where(c => _repo.ActiveCommands.Contains(c)));
            var dupeByCommand = new Dictionary<string, string>();
            foreach (var d in dupes)
                foreach (var c in d.Commands)
                    dupeByCommand.TryAdd(c.Id, Loc.Get("cmd.duplicate", d.Phrase));
            DuplicatesText = dupes.Count == 0 ? "" : string.Join("\n", dupes.Take(3).Select(d => Loc.Get("cmd.duplicate", d.Phrase)));

            _all = source.Select(c =>
            {
                var dangerous = c.Actions.Any(a => _registry.Find(a.Type)?.Descriptor.IsDangerous == true);
                var vm = new CommandItemViewModel(c, this, CommandExecutor.DisplayName(c, Loc.Localizer), Describe(c),
                    PhrasesSummary(c), ActionsSummary(c), dangerous, dupeByCommand.GetValueOrDefault(c.Id),
                    CategoryLabel(c.Category), AppIconFor(c));
                if (_lastResults.TryGetValue(c.Id, out var r)) vm.SetResult(r.Text, r.Brush);
                return vm;
            }).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            Packs.Clear();
            if (!_workflowsOnly)
                foreach (var p in _repo.Packs)
                {
                    var n = _repo.Commands.Count(c => c.PackId == p.Id);
                    Packs.Add(new PackItemViewModel(p, _repo, Loc[p.NameKey], Loc[p.DescriptionKey], n, Loc.Get("cmd.packs.count", n)));
                }
        }
        finally { _loading = false; }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_loading) return;
        var q = (Search ?? "").Trim();
        var filter = SelectedFilter.Key; var group = SelectedGroup.Key;
        IEnumerable<CommandItemViewModel> q2 = _all;
        if (q.Length > 0)
        {
            var norm = TextNormalizer.Normalize(q);
            q2 = q2.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || i.Command.Phrases.Any(p => p.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || (norm.Length > 0 && TextNormalizer.Normalize(p).Contains(norm))));
        }
        q2 = filter switch
        {
            "enabled" => q2.Where(i => i.Enabled),
            "disabled" => q2.Where(i => !i.Enabled),
            "builtin" => q2.Where(i => i.IsBuiltIn),
            "custom" => q2.Where(i => i.IsCustom),
            "dangerous" => q2.Where(i => i.IsDangerous),
            _ => q2,
        };
        if (group == "workflows") q2 = q2.Where(i => i.Command.IsWorkflow);
        else if (group != "all") q2 = q2.Where(i => i.Command.Category.ToString() == group);

        Items.Clear();
        foreach (var i in q2) Items.Add(i);
        CountText = $"{Items.Count} / {_all.Count}";
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ---- row operations -------------------------------------------------------------------------

    internal void SetEnabled(CommandItemViewModel row, bool enabled)
    {
        var copy = row.Command.Clone();
        copy.Enabled = enabled;
        _repo.AddOrUpdate(copy);
    }

    internal async Task TestAsync(CommandItemViewModel row)
    {
        if (row.Testing) return;
        row.Testing = true;
        row.SetResult(Loc["cmd.testing"], "TextMutedBrush");
        try
        {
            var report = await _pipeline.TestAsync(row.Id);
            if (report == null) { row.SetResult("", "TextMutedBrush"); return; }
            var brush = report.Outcome switch
            {
                ExecutionOutcome.Success => "SuccessBrush",
                ExecutionOutcome.Blocked => "WarningBrush",
                ExecutionOutcome.Cancelled => "TextMutedBrush",
                _ => "DangerBrush",
            };
            var text = report.Message;
            _lastResults[row.Id] = (text, brush);
            row.SetResult(text, brush);
        }
        finally { row.Testing = false; }
    }

    [RelayCommand]
    public async Task NewAsync()
    {
        var saved = await _editor.EditAsync(null, _workflowsOnly);
        if (saved != null) Reload();
    }

    internal async Task EditAsync(CommandItemViewModel row)
    {
        var saved = await _editor.EditAsync(row.Command, _workflowsOnly);
        if (saved != null) Reload();
    }

    internal void Duplicate(CommandItemViewModel row)
    {
        var copy = row.Command.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Loc.Get("cmd.copyname", row.Name);
        copy.NameKey = null; copy.DescriptionKey = null; copy.PackId = null; copy.IsBuiltIn = false;
        copy.Description = row.Description;
        // The copy shares the original's phrases; keep it disabled until they are edited so the two don't collide.
        copy.Enabled = false;
        _repo.AddOrUpdate(copy);
    }

    internal async Task DeleteAsync(CommandItemViewModel row)
    {
        var ok = await _dialogs.ConfirmAsync(Loc["cmd.delete.title"], Loc.Get("cmd.delete.body", row.Name),
            Loc["common.delete"], Loc["common.cancel"], danger: true);
        if (!ok) return;
        _lastResults.Remove(row.Id);
        _repo.Remove(row.Id);
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        var ok = await _dialogs.ConfirmAsync(Loc["cmd.restoredefaults"], Loc["cmd.restoredefaults.body"],
            Loc["cmd.restoredefaults"], Loc["common.cancel"], danger: false);
        if (ok) _repo.RestoreBuiltIns();
    }

    [RelayCommand] private void TogglePacks() => PacksExpanded = !PacksExpanded;

    public override void Dispose()
    {
        _repo.Changed -= OnRepoChanged;
        base.Dispose();
    }
}

/// <summary>Same list, restricted to multi-step commands (workflows).</summary>
public sealed class WorkflowsViewModel : CommandsViewModel
{
    public WorkflowsViewModel(ICommandRepository repo, IActionHandlerRegistry registry, IRecognitionPipeline pipeline,
        ICommandEditorService editor, IDialogService dialogs, IAppRegistry apps, IIconService icons)
        : base(repo, registry, pipeline, editor, dialogs, apps, icons, true) { }
}
