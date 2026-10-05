using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.ViewModels;

public sealed class OptionItem
{
    public OptionItem(string value, string label) { Value = value; Label = label; }
    public string Value { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

/// <summary>Editable phrase row.</summary>
public sealed partial class PhraseItem : ObservableObject
{
    [ObservableProperty] private string _text = "";
    public PhraseItem(string text) { _text = text; }
}

/// <summary>One parameter of an action step, rendered generically from its <see cref="ParameterDefinition"/>.</summary>
public sealed partial class ParamViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public ParamViewModel(ParameterDefinition def, string value, IReadOnlyList<OptionItem> appOptions, IDialogService dialogs,
        AppPickerContext? picker = null, Action? removeAction = null)
    {
        Definition = def; _dialogs = dialogs; _value = value;
        Options = def.Kind switch
        {
            ParameterKind.App => appOptions,
            ParameterKind.Choice => (def.Choices ?? Array.Empty<ChoiceOption>())
                .Select(c => new OptionItem(c.Value, LocSource.Instance[c.LabelKey])).ToList(),
            _ => Array.Empty<OptionItem>(),
        };
        if (def.Kind == ParameterKind.Choice && string.IsNullOrEmpty(_value)) _value = def.Default;
        if (def.Kind == ParameterKind.App && picker != null)
        {
            Selector = new AppSelectorState(picker, () => Value, v => Value = v, removeAction ?? (() => { }));
            _value = Selector.Normalize(_value);
            Selector.RefreshState();
        }
    }

    public ParameterDefinition Definition { get; }
    public IReadOnlyList<OptionItem> Options { get; }
    /// <summary>Searchable application selector, present for App parameters when the editor has a registry.</summary>
    public AppSelectorState? Selector { get; }
    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) => Selector?.RefreshState();
    public bool IsApp => Selector != null;

    public string Label => LocSource.Instance[Definition.LabelKey];
    public void RaiseAll() => OnPropertyChanged(string.Empty);
    public string Hint => Definition.HintKey != null && LocSource.Instance.Localizer.Has(Definition.HintKey) ? LocSource.Instance[Definition.HintKey] : "";
    public bool HasHint => Hint.Length > 0;
    public bool IsList => Definition.Kind == ParameterKind.Choice || (Definition.Kind == ParameterKind.App && Selector == null);
    public bool IsFolder => Definition.Kind == ParameterKind.Folder;
    public bool IsBool => Definition.Kind == ParameterKind.Bool;
    public bool IsText => !IsList && !IsBool && !IsApp;
    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set { Value = value ? "true" : "false"; OnPropertyChanged(); }
    }

    [RelayCommand]
    private void Browse()
    {
        var p = _dialogs.PickFolder(Label);
        if (p != null) Value = p;
    }
}

/// <summary>One step in the macro list.</summary>
public sealed partial class StepViewModel : ObservableObject
{
    private readonly CommandEditorViewModel _owner;
    private readonly IActionHandlerRegistry _registry;
    private readonly IReadOnlyList<OptionItem> _appOptions;
    private readonly IDialogService _dialogs;
    private readonly AppPickerContext? _picker;

    public StepViewModel(CommandEditorViewModel owner, IActionHandlerRegistry registry, IReadOnlyList<OptionItem> appOptions,
        IDialogService dialogs, ActionStep step, AppPickerContext? picker = null)
    {
        _owner = owner; _registry = registry; _appOptions = appOptions; _dialogs = dialogs; _picker = picker;
        Descriptor = registry.Descriptors.FirstOrDefault(d => d.Type == step.Type);
        Type = step.Type;
        Build(step.Parameters);
    }

    public ActionDescriptor? Descriptor { get; private set; }
    public string Type { get; private set; }
    public ObservableCollection<ParamViewModel> Params { get; } = new();

    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _testBrush = "TextMutedBrush";
    [ObservableProperty] private bool _testing;

    public string Title => Descriptor == null ? Type : LocSource.Instance[Descriptor.NameKey];
    public string Glyph => Descriptor?.Icon ?? "";
    public string StepLabel => LocSource.Instance.Get("edit.step", Index + 1);
    public bool IsDangerous => Descriptor?.IsDangerous == true;
    public bool HasParams => Params.Count > 0;
    public bool HasResult => TestResult.Length > 0;

    public int Number => Index + 1;

    partial void OnIndexChanged(int value) { OnPropertyChanged(nameof(StepLabel)); OnPropertyChanged(nameof(Number)); }
    partial void OnTestResultChanged(string value) => OnPropertyChanged(nameof(HasResult));

    private void Build(IDictionary<string, string>? values)
    {
        Params.Clear();
        if (Descriptor == null) return;
        foreach (var p in Descriptor.Parameters)
        {
            var v = values != null && values.TryGetValue(p.Key, out var x) ? x : p.Default;
            if (p.Kind == ParameterKind.App) v = ResolveAppValue(v);
            var pvm = new ParamViewModel(p, v, _appOptions, _dialogs, _picker, () => _owner.RemoveStep(this));
            pvm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParamViewModel.Value)) _owner.Touch(); };
            Params.Add(pvm);
        }
        OnPropertyChanged(nameof(HasParams));
    }

    /// <summary>Stored values may be an app id, an app name or the {app} variable; the picker works on ids.</summary>
    private string ResolveAppValue(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        var hit = _appOptions.FirstOrDefault(o => o.Value.Equals(v, StringComparison.OrdinalIgnoreCase))
                  ?? _appOptions.FirstOrDefault(o => o.Label.Equals(v, StringComparison.OrdinalIgnoreCase));
        return hit?.Value ?? v;
    }

    public ActionStep ToStep()
    {
        var step = new ActionStep(Type);
        foreach (var p in Params) step.Parameters[p.Definition.Key] = (p.Value ?? "").Trim();
        return step;
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var p in Params) p.RaiseAll();
    }

    [RelayCommand] private void MoveUp() => _owner.Move(this, -1);
    [RelayCommand] private void MoveDown() => _owner.Move(this, +1);
    [RelayCommand] private void Remove() => _owner.RemoveStep(this);
    [RelayCommand] private void Duplicate() => _owner.DuplicateStep(this);

    /// <summary>First application-selector parameter, when this is an Open Application style action.</summary>
    public AppSelectorState? AppSelector => Params.Select(p => p.Selector).FirstOrDefault(s => s != null);
    public bool HasAppSelector => AppSelector != null;
    public bool HasOtherParams => Params.Any(p => !p.IsApp);

    [RelayCommand]
    private async Task TestAsync()
    {
        Testing = true; TestResult = LocSource.Instance["cmd.testing"]; TestBrush = "TextMutedBrush";
        try
        {
            var report = await _owner.RunAsync(new List<ActionStep> { ToStep() });
            TestResult = report.Message;
            TestBrush = report.Success ? "SuccessBrush" : "DangerBrush";
        }
        finally { Testing = false; }
    }
}

public sealed partial class CommandEditorViewModel : ViewModelBase
{
    private readonly ICommandRepository _repo;
    private readonly IAppRegistry _apps;
    private readonly IActionHandlerRegistry _registry;
    private readonly ICommandExecutor _executor;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _loc;
    private readonly VoiceCommand? _original;
    private readonly IReadOnlyList<OptionItem> _appOptions;

    public CommandEditorViewModel(VoiceCommand? existing, ICommandRepository repo, IAppRegistry apps,
        IActionHandlerRegistry registry, ICommandExecutor executor, IDialogService dialogs, ILocalizer loc,
        bool workflowHint = false, AppPickerContext? picker = null)
    {
        _picker = picker;
        _repo = repo; _apps = apps; _registry = registry; _executor = executor; _dialogs = dialogs; _loc = loc;
        _original = existing;
        IsNew = existing == null;
        _workflowHint = workflowHint;

        var opts = new List<OptionItem> { new("{app}", "{app}  —  " + Loc["edit.app.fromvoice"]) };
        opts.AddRange(apps.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a => new OptionItem(a.Id, a.Name)));
        _appOptions = opts;

        TypeOptions = registry.Descriptors
            .Select(d => new ActionTypeItem(d.Type, Loc[d.NameKey], Loc[d.GroupKey], d.Icon))
            .OrderBy(t => t.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _selectedType = TypeOptions.FirstOrDefault(t => t.Type == ActionTypes.OpenApp) ?? TypeOptions.FirstOrDefault();

        Categories = new List<OptionItem>
        {
            new(nameof(CommandCategory.Custom), Loc["cmd.custom"]),
            new(nameof(CommandCategory.Applications), Loc["group.apps"]),
            new(nameof(CommandCategory.System), Loc["group.system"]),
            new(nameof(CommandCategory.Audio), Loc["group.audio"]),
        };

        var c = existing ?? new VoiceCommand { Category = CommandCategory.Custom, Icon = "" };
        _name = existing == null ? "" : CommandExecutor.DisplayName(existing, loc);
        _originalDisplayName = _name;
        _category = c.Category.ToString();
        _enabled = c.Enabled;
        foreach (var p in c.Phrases.Where(p => !string.IsNullOrWhiteSpace(p))) Phrases.Add(new PhraseItem(p));
        foreach (var a in c.Actions) Steps.Add(new StepViewModel(this, registry, _appOptions, dialogs, a, picker));
        Renumber();

        // Keep the warning/error text live as the form is edited.
        foreach (var p in Phrases) p.PropertyChanged += (_, _) => Touch();
        Phrases.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (PhraseItem p in e.NewItems) p.PropertyChanged += (_, _) => Touch();
            Touch();
        };
        Steps.CollectionChanged += (_, _) => Touch();
        _ready = true;
    }

    private bool _ready;
    private readonly AppPickerContext? _picker;

    partial void OnNameChanged(string value) => Touch();
    partial void OnCategoryChanged(string value) => Touch();

    /// <summary>Refreshes the duplicate warning always, and the error list only if one is already showing.</summary>
    public void Touch()
    {
        if (!_ready) return;
        var draft = BuildDraft();
        UpdateDuplicateWarning(draft);
        if (!HasError) return;
        var errs = Problems(draft);
        ErrorText = errs.Count == 0 ? "" : Loc["edit.invalid"] + "\n" + string.Join("\n", errs);
    }

    private readonly string _originalDisplayName;
    private readonly bool _workflowHint;

    public sealed record ActionTypeItem(string Type, string Name, string Group, string Icon)
    {
        public string Label => Group + "  ›  " + Name;
    }

    public bool IsNew { get; }
    public string WindowTitle => Loc[IsNew ? (_workflowHint ? "wf.new" : "edit.title.new") : "edit.title.edit"];
    public bool IsBuiltIn => _original?.IsBuiltIn == true;
    public IReadOnlyList<ActionTypeItem> TypeOptions { get; }
    public IReadOnlyList<OptionItem> Categories { get; }
    public ObservableCollection<PhraseItem> Phrases { get; } = new();
    public ObservableCollection<StepViewModel> Steps { get; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _category;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ActionTypeItem? _selectedType;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _testBrush = "TextMutedBrush";
    [ObservableProperty] private bool _testing;

    public bool HasError => ErrorText.Length > 0;
    public bool HasWarning => WarningText.Length > 0;
    public bool HasTestResult => TestResult.Length > 0;
    public bool NoSteps => Steps.Count == 0;
    public VoiceCommand? Result { get; private set; }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnWarningTextChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnTestResultChanged(string value) => OnPropertyChanged(nameof(HasTestResult));

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var s in Steps) s.Refresh();
    }

    /// <summary>Text typed into the "add phrase" box; committed by Enter, the Add button or Save.</summary>
    [ObservableProperty] private string _newPhrase = "";
    public bool NoPhrases => Phrases.Count == 0;

    [RelayCommand]
    private void AddPhrase() => CommitPendingPhrase();

    /// <summary>Moves the typed text into the chip list. Commas/new lines split multiple phrases. Returns true if anything was added.</summary>
    private bool CommitPendingPhrase()
    {
        var added = false;
        foreach (var raw in (NewPhrase ?? "").Split(new[] { ',', '،', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length == 0 || Phrases.Any(p => string.Equals(p.Text.Trim(), t, StringComparison.OrdinalIgnoreCase))) continue;
            Phrases.Add(new PhraseItem(t));
            added = true;
        }
        NewPhrase = "";
        OnPropertyChanged(nameof(NoPhrases));
        return added;
    }

    [RelayCommand]
    private void RemovePhrase(PhraseItem? p)
    {
        if (p == null) return;
        Phrases.Remove(p);
        OnPropertyChanged(nameof(NoPhrases));
    }

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedType == null) return;
        Steps.Add(new StepViewModel(this, _registry, _appOptions, _dialogs, new ActionStep(SelectedType.Type), _picker));
        Renumber();
    }

    public void RemoveStep(StepViewModel s) { Steps.Remove(s); Renumber(); }

    public void DuplicateStep(StepViewModel s)
    {
        var i = Steps.IndexOf(s);
        if (i < 0) return;
        Steps.Insert(i + 1, new StepViewModel(this, _registry, _appOptions, _dialogs, s.ToStep(), _picker));
        Renumber();
    }

    public void Move(StepViewModel s, int delta)
    {
        var i = Steps.IndexOf(s); var j = i + delta;
        if (i < 0 || j < 0 || j >= Steps.Count) return;
        Steps.Move(i, j);
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Index = i;
            Steps[i].CanMoveUp = i > 0;
            Steps[i].CanMoveDown = i < Steps.Count - 1;
        }
        OnPropertyChanged(nameof(NoSteps));
    }

    private List<string> PhraseList() =>
        Phrases.Select(p => (p.Text ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Builds the command from the form. Keeps the original identity, pack and name key where the user did not change them.</summary>
    private VoiceCommand BuildDraft()
    {
        var c = _original?.Clone() ?? new VoiceCommand { Icon = "" };
        var typedName = (Name ?? "").Trim();
        if (!string.IsNullOrEmpty(c.NameKey) && typedName == _originalDisplayName) { /* untouched built-in name stays localized */ }
        else { c.Name = typedName; c.NameKey = ""; }
        c.Category = Enum.TryParse<CommandCategory>(Category, out var cat) ? cat : CommandCategory.Custom;
        c.Enabled = Enabled;
        c.Phrases = PhraseList();
        c.Actions = Steps.Select(s => s.ToStep()).ToList();
        return c;
    }

    private List<string> Problems(VoiceCommand draft)
    {
        var msgs = _repo.Validate(draft).Select(k => Loc[k]).ToList();
        foreach (var s in Steps)
        {
            if (s.Descriptor == null) continue;
            foreach (var p in s.Params)
            {
                var v = (p.Value ?? "").Trim();
                if (p.Selector is { IsAppMissing: true } && !msgs.Contains(Loc["edit.app.missing.error"])) msgs.Add(Loc["edit.app.missing.error"]);
                if (p.Definition.Required && v.Length == 0 && !msgs.Contains(Loc["validation.param"])) msgs.Add(Loc["validation.param"]);
                if (p.Definition.Kind == ParameterKind.Hotkey && v.Length > 0 && !KeyNames.TryParse(v, out _, out _)
                    && !msgs.Contains(Loc["validation.hotkey"])) msgs.Add(Loc["validation.hotkey"]);
            }
        }
        return msgs.Distinct().ToList();
    }

    /// <summary>Warn (never block) when a phrase is already used by another enabled command.</summary>
    private void UpdateDuplicateWarning(VoiceCommand draft)
    {
        var others = _repo.Commands.Where(c => c.Id != draft.Id).Append(draft);
        var dup = CommandMatcher.FindDuplicates(others)
            .Where(d => d.Commands.Any(c => c.Id == draft.Id) && d.Commands.Count > 1).ToList();
        WarningText = string.Join("\n", dup.Select(d => Loc.Get("cmd.duplicate", d.Phrase)));
    }

    public Task<ExecutionReport> RunAsync(List<ActionStep> steps)
    {
        var temp = new VoiceCommand { Id = "editor-test", Name = Name ?? "", Actions = steps, Phrases = new List<string> { "test" } };
        return _executor.ExecuteAsync(temp, 50, _apps.Apps.FirstOrDefault(), TriggerSource.Test);
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        CommitPendingPhrase();
        var draft = BuildDraft();
        var errs = Problems(draft);
        if (errs.Count > 0) { ErrorText = string.Join("\n", errs); return; }
        ErrorText = "";
        Testing = true; TestResult = Loc["cmd.testing"]; TestBrush = "TextMutedBrush";
        try
        {
            var r = await _executor.ExecuteAsync(draft, 50, _apps.Apps.FirstOrDefault(), TriggerSource.Test);
            TestResult = r.Message; TestBrush = r.Success ? "SuccessBrush" : "DangerBrush";
        }
        finally { Testing = false; }
    }

    /// <summary>Validates and saves. Returns true when the dialog may close.</summary>
    public bool TrySave()
    {
        CommitPendingPhrase();
        var draft = BuildDraft();
        var errs = Problems(draft);
        UpdateDuplicateWarning(draft);
        if (errs.Count > 0) { ErrorText = Loc["edit.invalid"] + "\n" + string.Join("\n", errs); return false; }
        ErrorText = "";
        _repo.AddOrUpdate(draft);
        Result = draft;
        return true;
    }
}
