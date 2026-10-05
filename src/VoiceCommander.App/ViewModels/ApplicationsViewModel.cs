using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.ViewModels;

/// <summary>An application found by auto-detect, with a tick box for "add selected".</summary>
public sealed partial class DetectedItemViewModel : ObservableObject
{
    public DetectedItemViewModel(DetectedApp app) { App = app; }
    public DetectedApp App { get; }
    public string Name => App.Name;
    public string Path => App.ExecutablePath;
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>A row in the application list. Actions are delegated to the page.</summary>
public sealed partial class AppItemViewModel : ObservableObject
{
    private readonly ApplicationsViewModel _parent;

    public AppItemViewModel(AppDefinition app, ApplicationsViewModel parent, bool missing)
    {
        _parent = parent;
        App = app;
        Missing = missing;
    }

    public AppDefinition App { get; }
    public string Id => App.Id;
    public string Name => App.Name;
    public string PathText => App.ExecutablePath;
    public string AliasesText => App.Aliases is { Count: > 0 } ? string.Join(", ", App.Aliases) : "";
    public bool HasAliases => AliasesText.Length > 0;
    public bool IsBuiltIn => App.IsBuiltIn;
    public bool Missing { get; }
    public string MissingText => LocSource.Instance["apps.notfound"];
    public string TestLabel => LocSource.Instance["common.test"];
    public string LocateLabel => LocSource.Instance["apps.locate"];
    public string Initial => App.Name.Length > 0 ? App.Name[..1].ToUpperInvariant() : "?";
    public bool IsUri => LaunchTarget.IsUri(App.ExecutablePath);
    /// <summary>Badge text: launcher link vs. program file.</summary>
    public string KindText => LocSource.Instance[IsUri ? "apps.kind.uri" : "apps.kind.exe"];
    public string ProcessText => string.IsNullOrWhiteSpace(App.ProcessName) ? "" : App.ProcessName + (App.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "" : ".exe");

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon))] private ImageSource? _icon;
    public bool HasIcon => Icon != null;

    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusBrush = "TextMutedBrush";
    [ObservableProperty] private bool _testing;

    [NotifyPropertyChangedFor(nameof(HasResult))]
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _resultBrush = "TextMutedBrush";
    public bool HasResult => ResultText.Length > 0;

    public void SetResult(string text, string brush) { ResultText = text; ResultBrush = brush; }

    [RelayCommand] private Task TestAsync() => _parent.TestAsync(this);
    [RelayCommand] private Task EditAsync() => _parent.EditAsync(this);
    [RelayCommand] private Task DeleteAsync() => _parent.DeleteAsync(this);
    [RelayCommand] private void Locate() => _parent.Locate(this);
}

/// <summary>The Applications page: the registry that voice commands refer to.</summary>
public sealed partial class ApplicationsViewModel : ViewModelBase
{
    private readonly IAppRegistry _registry;
    private readonly IAppDetector _detector;
    private readonly IAppController _controller;
    private readonly IAppLocator _locator;
    private readonly IIconService _icons;
    private readonly IAppEditorService _editor;
    private readonly IDialogService _dialogs;
    private readonly System.Threading.Timer _statusTimer;
    private readonly Dictionary<string, (string Text, string Brush)> _lastResults = new();
    private int _reloadVersion;

    public ApplicationsViewModel(IAppRegistry registry, IAppDetector detector, IAppController controller, IAppLocator locator,
        IIconService icons, IAppEditorService editor, IDialogService dialogs)
    {
        _registry = registry; _detector = detector; _controller = controller; _locator = locator;
        _icons = icons; _editor = editor; _dialogs = dialogs;
        _registry.Changed += OnRegistryChanged;
        Reload();
        _statusTimer = new System.Threading.Timer(_ => RefreshStatus(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
    }

    public ObservableCollection<AppItemViewModel> Items { get; } = new();
    public ObservableCollection<DetectedItemViewModel> Detected { get; } = new();

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showDetect;
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private string _detectText = "";
    public bool IsEmpty => Items.Count == 0;
    public bool HasDetected => Detected.Count > 0;

    partial void OnSearchChanged(string value) => Reload();

    private void OnRegistryChanged() => UI.Post(Reload);
    protected override void OnLanguageChanged() { base.OnLanguageChanged(); Reload(); }

    private void Reload()
    {
        var q = Search.Trim();
        var apps = _registry.Apps
            .Where(a => q.Length == 0
                || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.Aliases.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase))
                || (a.ExecutablePath ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Items.Clear();
        var version = ++_reloadVersion;
        var rows = new List<AppItemViewModel>();
        foreach (var a in apps)
        {
            var row = new AppItemViewModel(a, this, missing: false);
            rows.Add(row);
        }
        foreach (var row in rows) Items.Add(row);
        CountText = Loc.Get("apps.count", _registry.Apps.Count);
        OnPropertyChanged(nameof(IsEmpty));

        // Resolving paths, loading icons and checking processes touches disk and the process table, so keep it off the UI thread.
        Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (version != _reloadVersion) return;
                var missing = SafeResolve(row.App) == null;
                string? iconPath = null;
                try { iconPath = _icons.GetIconPath(row.App); } catch { }
                var running = SafeRunning(row.App);
                UI.Post(() =>
                {
                    if (version != _reloadVersion) return;
                    ApplyProbe(row, missing, iconPath, running);
                });
            }
        });
    }

    private AppItemViewModel? Replace(AppItemViewModel row, bool missing)
    {
        // "Missing" is fixed per row instance, so swap the row to refresh it.
        var i = Items.IndexOf(row);
        if (i < 0) return null;
        var fresh = new AppItemViewModel(row.App, this, missing) { Icon = row.Icon, Running = row.Running };
        fresh.StatusText = row.StatusText; fresh.StatusBrush = row.StatusBrush;
        Items[i] = fresh;
        return fresh;
    }

    private void ApplyProbe(AppItemViewModel row, bool missing, string? iconPath, bool running)
    {
        var target = row;
        if (missing) target = Replace(row, true) ?? row;
        if (iconPath != null) target.Icon = LoadIcon(iconPath);
        SetStatus(target, running, missing);
        if (_lastResults.TryGetValue(target.Id, out var r)) target.SetResult(r.Text, r.Brush);
    }

    private void SetStatus(AppItemViewModel row, bool running, bool missing)
    {
        row.Running = running;
        if (missing) { row.StatusText = ""; return; }
        row.StatusText = Loc[running ? "apps.running" : "apps.stopped"];
        row.StatusBrush = running ? "SuccessBrush" : "TextMutedBrush";
    }

    private static ImageSource? LoadIcon(string path) => IconCache.Load(path);

    private ResolvedLaunch? SafeResolve(AppDefinition a) { try { return _locator.Resolve(a); } catch { return null; } }
    private bool SafeRunning(AppDefinition a) { try { return _controller.IsRunning(a); } catch { return false; } }

    private void RefreshStatus()
    {
        var snapshot = Items.ToList();
        if (snapshot.Count == 0) return;
        var states = snapshot.Select(r => (r, SafeRunning(r.App))).ToList();
        UI.Post(() =>
        {
            foreach (var (row, running) in states)
                if (Items.Contains(row) && !row.Missing && row.Running != running) SetStatus(row, running, false);
        });
    }

    [RelayCommand]
    private async Task AddAsync() => await _editor.EditAsync(null);

    internal Task EditAsync(AppItemViewModel row) => _editor.EditAsync(row.App);

    internal async Task DeleteAsync(AppItemViewModel row)
    {
        if (!await _dialogs.ConfirmAsync(Loc["apps.delete.title"], Loc.Get("apps.delete.body", row.Name),
                Loc["common.delete"], Loc["common.cancel"], danger: true)) return;
        _lastResults.Remove(row.Id);
        _registry.Remove(row.Id);
    }

    internal async Task TestAsync(AppItemViewModel row)
    {
        if (row.Testing) return;
        row.Testing = true;
        try
        {
            var r = await _controller.OpenAsync(row.App);
            (string text, string brush) res = r.Success ? (Loc.Get("apps.testok"), "SuccessBrush")
                : r.Status == AppControlStatus.NotFound ? (Loc["apps.notfound"], "DangerBrush")
                : (Loc.Get("apps.testfail", r.Detail), "DangerBrush");
            _lastResults[row.Id] = res;
            row.SetResult(res.text, res.brush);
            if (r.Success) SetStatus(row, true, false);
        }
        finally { row.Testing = false; }
    }

    internal void Locate(AppItemViewModel row)
    {
        var picked = _dialogs.PickFile(Loc["apps.exe.filter"] + "|*.exe|" + Loc["common.all"] + "|*.*", Loc["apps.locate"]);
        if (picked == null) return;
        var updated = row.App.Clone();
        updated.ExecutablePath = picked;
        if (string.IsNullOrWhiteSpace(updated.ProcessName))
            updated.ProcessName = System.IO.Path.GetFileNameWithoutExtension(picked);
        _registry.AddOrUpdate(updated);
        _lastResults[updated.Id] = (Loc["apps.located"], "SuccessBrush");
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        ShowDetect = true;
        IsDetecting = true;
        DetectText = Loc["apps.detecting"];
        Detected.Clear();
        OnPropertyChanged(nameof(HasDetected));
        try
        {
            var found = await Task.Run(() => _detector.DetectAsync());
            var known = _registry.Apps.ToList();
            bool Known(DetectedApp d) => known.Any(k =>
                string.Equals(k.ExecutablePath, d.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k.Name, d.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var d in found.Where(d => !Known(d)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                Detected.Add(new DetectedItemViewModel(d) { IsSelected = false });
            DetectText = Detected.Count > 0 ? Loc.Get("apps.detect.found", Detected.Count) : Loc["apps.detect.none"];
        }
        catch (Exception ex)
        {
            DetectText = Loc.Get("apps.testfail", ex.Message);
        }
        finally
        {
            IsDetecting = false;
            OnPropertyChanged(nameof(HasDetected));
        }
    }

    [RelayCommand]
    private void AddSelected()
    {
        var chosen = Detected.Where(d => d.IsSelected).ToList();
        if (chosen.Count == 0) return;
        foreach (var d in chosen)
        {
            _registry.AddOrUpdate(new AppDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = d.Name,
                ExecutablePath = d.Path,
                ProcessName = d.App.ProcessName,
            });
            Detected.Remove(d);
        }
        DetectText = Loc.Get("apps.added", chosen.Count);
        OnPropertyChanged(nameof(HasDetected));
    }

    [RelayCommand]
    private void SelectAllDetected()
    {
        var all = Detected.All(d => d.IsSelected);
        foreach (var d in Detected) d.IsSelected = !all;
    }

    [RelayCommand]
    private void CloseDetect()
    {
        ShowDetect = false;
        Detected.Clear();
        OnPropertyChanged(nameof(HasDetected));
    }

    public override void Dispose()
    {
        _registry.Changed -= OnRegistryChanged;
        _statusTimer.Dispose();
        _reloadVersion++;
        base.Dispose();
    }
}
