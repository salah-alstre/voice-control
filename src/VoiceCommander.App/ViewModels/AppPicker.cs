using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.ViewModels;

/// <summary>Everything the Open Application selector needs. Null in tests that never render an app parameter.</summary>
public sealed record AppPickerContext(IAppRegistry Registry, IIconService Icons, IAppEditorService Editor,
    IAppLocator Locator, IDialogService Dialogs);

/// <summary>One selectable entry in the app dropdown: a registered application or the "{app}" voice variable.</summary>
public sealed partial class AppChoiceViewModel : ObservableObject
{
    public AppChoiceViewModel(string value, string name, string subtitle, bool isVoiceVariable = false)
    {
        Value = value; Name = name; Subtitle = subtitle; IsVoiceVariable = isVoiceVariable;
    }

    public string Value { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public bool IsVoiceVariable { get; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon))] private ImageSource? _icon;
    public bool HasIcon => Icon != null;
}

public enum AppSelection { None, Ok, ExecutableMissing, AppMissing }

/// <summary>Searchable application selector state for a single "Open Application" action parameter.</summary>
public sealed partial class AppSelectorState : ObservableObject
{
    private readonly AppPickerContext _ctx;
    private readonly Func<string> _getValue;
    private readonly Action<string> _setValue;
    private readonly Action _removeAction;
    private List<AppChoiceViewModel> _all = new();

    public AppSelectorState(AppPickerContext ctx, Func<string> getValue, Action<string> setValue, Action removeAction)
    {
        _ctx = ctx; _getValue = getValue; _setValue = setValue; _removeAction = removeAction;
        Reload();
    }

    public ObservableCollection<AppChoiceViewModel> Choices { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private AppChoiceViewModel? _selected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNone), nameof(IsOk), nameof(IsExeMissing), nameof(IsAppMissing), nameof(ShowSummary))]
    private AppSelection _state;

    public bool IsNone => State == AppSelection.None;
    public bool IsOk => State == AppSelection.Ok;
    public bool IsExeMissing => State == AppSelection.ExecutableMissing;
    public bool IsAppMissing => State == AppSelection.AppMissing;
    public bool ShowSummary => State is AppSelection.Ok or AppSelection.ExecutableMissing;
    public bool HasNoResults => Choices.Count == 0;

    /// <summary>The unresolved value shown on the "missing" card (hidden when it is just an id).</summary>
    public string MissingName
    {
        get
        {
            var v = (_getValue() ?? "").Trim();
            var looksLikeId = v.Length == 32 && v.All(Uri.IsHexDigit);
            return looksLikeId ? "" : v;
        }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    /// <summary>Rebuilds the list from the registry (after Add New / Locate) and re-evaluates the selection.</summary>
    public void Reload()
    {
        var loc = LocSource.Instance;
        var list = new List<AppChoiceViewModel>
        {
            new("{app}", loc["edit.app.fromvoice"], loc["edit.app.fromvoice.hint"], isVoiceVariable: true),
        };
        foreach (var a in _ctx.Registry.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var c = new AppChoiceViewModel(a.Id, a.Name, a.ExecutablePath ?? "");
            list.Add(c);
        }
        _all = list;
        ApplyFilter();
        RefreshState();
        LoadIconsAsync(list);
    }

    /// <summary>Icons come from the on-disk cache; extraction (first time only) happens off the UI thread.</summary>
    private void LoadIconsAsync(List<AppChoiceViewModel> list)
    {
        var apps = _ctx.Registry.Apps.ToDictionary(a => a.Id, a => a);
        // Cheap path first: anything already cached/decoded is applied immediately.
        Task.Run(() =>
        {
            foreach (var c in list)
            {
                if (c.IsVoiceVariable || !apps.TryGetValue(c.Value, out var app)) continue;
                string? path = null;
                try { path = _ctx.Icons.GetIconPath(app); } catch { /* an unreadable exe just keeps the letter tile */ }
                if (path == null) continue;
                UI.Post(() => c.Icon = IconCache.Load(path));
            }
        });
    }

    private void ApplyFilter()
    {
        var q = (Search ?? "").Trim();
        Choices.Clear();
        foreach (var c in _all)
        {
            if (q.Length == 0 || c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || c.Subtitle.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                Choices.Add(c);
        }
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>Resolves a stored value (id or name) to the canonical id; unresolved values are kept as typed.</summary>
    public string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var v = value.Trim();
        if (v.Equals("{app}", StringComparison.OrdinalIgnoreCase)) return "{app}";
        var app = _ctx.Registry.FindById(v) ?? _ctx.Registry.FindByName(v);
        return app?.Id ?? v;
    }

    public AppDefinition? CurrentApp()
    {
        var v = (_getValue() ?? "").Trim();
        return v.Length == 0 || v == "{app}" ? null : _ctx.Registry.FindById(v);
    }

    public void RefreshState()
    {
        var v = (_getValue() ?? "").Trim();
        if (v.Length == 0) { Selected = null; State = AppSelection.None; }
        else if (v.Equals("{app}", StringComparison.OrdinalIgnoreCase))
        { Selected = _all.FirstOrDefault(c => c.IsVoiceVariable); State = AppSelection.Ok; }
        else
        {
            var app = _ctx.Registry.FindById(v);
            if (app == null) { Selected = null; State = AppSelection.AppMissing; }
            else
            {
                Selected = _all.FirstOrDefault(c => c.Value == app.Id);
                var found = true;
                try { found = _ctx.Locator.Resolve(app) != null; } catch { found = false; }
                State = found ? AppSelection.Ok : AppSelection.ExecutableMissing;
            }
        }
        OnPropertyChanged(nameof(MissingName));
    }

    [RelayCommand]
    private void ToggleOpen()
    {
        if (!IsOpen) { Search = ""; Reload(); }
        IsOpen = !IsOpen;
    }

    [RelayCommand]
    private void Open() { if (!IsOpen) { Search = ""; Reload(); IsOpen = true; } }

    [RelayCommand]
    private void Select(AppChoiceViewModel? choice)
    {
        if (choice == null) return;
        _setValue(choice.Value);
        IsOpen = false;
        RefreshState();
    }

    [RelayCommand]
    private async Task AddNewAsync()
    {
        IsOpen = false;
        AppDefinition? created;
        try { created = await _ctx.Editor.EditAsync(null); }
        catch { return; }
        if (created == null) return;
        Reload();
        _setValue(created.Id);
        RefreshState();
    }

    /// <summary>Lets the user point a registered application at its moved executable. Never throws.</summary>
    [RelayCommand]
    private void Locate()
    {
        var app = CurrentApp();
        if (app == null) return;
        var loc = LocSource.Instance;
        var picked = _ctx.Dialogs.PickFile(loc["apps.exe.filter"] + "|*.exe|" + loc["common.all"] + "|*.*", loc["apps.locate"]);
        if (picked == null) return;
        var updated = app.Clone();
        updated.ExecutablePath = picked;
        if (string.IsNullOrWhiteSpace(updated.ProcessName))
            updated.ProcessName = System.IO.Path.GetFileNameWithoutExtension(picked);
        updated.IconPath = null;
        _ctx.Registry.AddOrUpdate(updated);
        Reload();
    }

    [RelayCommand]
    private void RemoveAction() => _removeAction();
}
