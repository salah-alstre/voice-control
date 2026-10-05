using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;
using System.Windows.Media;

namespace VoiceCommander.App.ViewModels;

/// <summary>Edits one application registry entry. Saving writes straight to the registry.</summary>
public sealed partial class AppEditorViewModel : ViewModelBase
{
    private readonly AppDefinition _original;
    private readonly IAppRegistry _registry;
    private readonly IAppController _controller;
    private readonly IDialogService _dialogs;
    private readonly IIconService? _icons;
    private int _iconVersion;

    public AppEditorViewModel(AppDefinition? existing, string? presetPath, IAppRegistry registry, IAppController controller, IDialogService dialogs,
        IIconService? icons = null)
    {
        _registry = registry; _controller = controller; _dialogs = dialogs; _icons = icons;
        IsNew = existing == null;
        _original = existing?.Clone() ?? new AppDefinition { Id = Guid.NewGuid().ToString("N") };

        _name = _original.Name;
        _path = presetPath ?? _original.ExecutablePath ?? "";
        _arguments = _original.Arguments ?? "";
        _processName = _original.ProcessName ?? "";
        _workingDirectory = _original.WorkingDirectory ?? "";
        _iconPath = _original.IconPath ?? "";
        _aliasesText = string.Join(Environment.NewLine, _original.Aliases ?? new List<string>());
        _singleInstance = _original.SingleInstance;
        _forceClose = _original.ForceClose;
        if (presetPath != null) GuessFromPath(presetPath);
        RefreshPreview();
    }

    public bool IsNew { get; }
    public AppDefinition? Result { get; private set; }
    public string WindowTitle => Loc[IsNew ? "apps.edit.new" : "apps.edit.edit"];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _path;
    [ObservableProperty] private string _arguments;
    [ObservableProperty] private string _processName;
    [ObservableProperty] private string _workingDirectory;
    [ObservableProperty] private string _iconPath;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _aliasesText;
    [ObservableProperty] private bool _singleInstance;
    [ObservableProperty] private bool _forceClose;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _errorText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTestResult))] private string _testResult = "";
    [ObservableProperty] private string _testBrush = "TextMutedBrush";
    [ObservableProperty] private bool _testing;

    public bool HasError => ErrorText.Length > 0;
    public bool HasTestResult => TestResult.Length > 0;

    public bool HasIcon => Icon != null;
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(IconPath);
    public bool IsUri => LaunchTarget.IsUri(Path?.Trim().Trim('"'));
    public bool IsMissing => !string.IsNullOrWhiteSpace(Path) && !IsUri && !TargetExists(Path);
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
    /// <summary>Short status next to the path: launcher link, program, or not found.</summary>
    public string KindText => string.IsNullOrWhiteSpace(Path) ? "" : IsUri ? Loc["apps.kind.uri"] : IsMissing ? Loc["apps.kind.missing"] : Loc["apps.kind.exe"];

    private static bool TargetExists(string p)
    {
        var s = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
        // A bare name (e.g. notepad.exe) is resolved through PATH at launch time; only rooted paths are checked here.
        return !System.IO.Path.IsPathRooted(s) || System.IO.File.Exists(s);
    }

    partial void OnPathChanged(string value)
    {
        ErrorText = "";
        OnPropertyChanged(nameof(IsUri)); OnPropertyChanged(nameof(IsMissing)); OnPropertyChanged(nameof(KindText));
        RefreshPreview();
    }

    partial void OnIconPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasCustomIcon));
        RefreshPreview();
    }

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    /// <summary>Re-resolves the preview icon off the UI thread. Icon extraction is cached by the icon service.</summary>
    private void RefreshPreview()
    {
        if (_icons == null) return;
        var version = ++_iconVersion;
        var probe = Build();
        Task.Run(() =>
        {
            ImageSource? img = null;
            try
            {
                var file = _icons.GetIconPath(probe);
                if (file != null) img = IconCache.Load(file);
            }
            catch { }
            UI.Post(() => { if (version == _iconVersion) Icon = img; });
        });
    }

    partial void OnNameChanged(string value) { ErrorText = ""; OnPropertyChanged(nameof(Initial)); }

    private void GuessFromPath(string path)
    {
        // Never overwrite what the user already typed.
        var file = System.IO.Path.GetFileNameWithoutExtension(path.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(file) || LaunchTarget.IsUri(path.Trim().Trim('"'))) return;
        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = file;
        if (string.IsNullOrWhiteSpace(Name)) Name = DetectDisplayName(path, file);
    }

    /// <summary>Prefers the product name embedded in the executable ("Google Chrome") over the bare file name.</summary>
    private static string DetectDisplayName(string path, string fileStem)
    {
        try
        {
            var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (System.IO.File.Exists(p))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(p);
                var n = vi.ProductName?.Trim();
                if (string.IsNullOrWhiteSpace(n)) n = vi.FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(n) && n.Length <= 48) return n;
            }
        }
        catch { }
        return fileStem.Length > 0 ? char.ToUpperInvariant(fileStem[0]) + fileStem[1..] : fileStem;
    }

    [RelayCommand]
    private void BrowseWorkingDirectory()
    {
        var p = _dialogs.PickFolder(Loc["apps.workdir"]);
        if (p != null) WorkingDirectory = p;
    }

    [RelayCommand]
    private void ChangeIcon()
    {
        var p = _dialogs.PickFile(Loc["apps.icon.filter"] + "|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.exe;*.dll|" + Loc["common.all"] + "|*.*", Loc["apps.icon"]);
        if (p != null) IconPath = p;
    }

    [RelayCommand]
    private void ResetIcon() => IconPath = "";

    [RelayCommand]
    private void Browse()
    {
        var picked = _dialogs.PickFile(Loc["apps.exe.filter"] + "|*.exe|" + Loc["common.all"] + "|*.*", Loc["apps.path"]);
        if (picked == null) return;
        // A process name that merely mirrored the old file name follows the new file.
        var oldStem = System.IO.Path.GetFileNameWithoutExtension(Path.Trim().Trim('"'));
        if (string.Equals(ProcessName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase), oldStem, StringComparison.OrdinalIgnoreCase)) ProcessName = "";
        Path = picked;
        GuessFromPath(picked); // fills the name and process only where they are still blank
    }

    private AppDefinition Build()
    {
        var a = _original.Clone();
        a.Name = Name.Trim();
        a.ExecutablePath = Path.Trim().Trim('"');
        a.Arguments = Arguments.Trim();
        a.ProcessName = ProcessName.Trim();
        a.WorkingDirectory = WorkingDirectory.Trim().Trim('"');
        a.IconPath = IconPath.Trim().Trim('"');
        a.SingleInstance = SingleInstance;
        a.ForceClose = ForceClose;
        a.Aliases = AliasesText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return a;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(Path)) { ShowTest(Loc["apps.validation.path"], "DangerBrush"); return; }
        Testing = true;
        try
        {
            var probe = Build();
            probe.SingleInstance = false; // a test should always try to start the program
            var r = await _controller.OpenAsync(probe);
            if (r.Success) ShowTest(Loc["apps.testok"], "SuccessBrush");
            else if (r.Status == AppControlStatus.NotFound) ShowTest(Loc["apps.notfound"], "DangerBrush");
            else ShowTest(Loc.Get("apps.testfail", r.Detail), "DangerBrush");
        }
        finally { Testing = false; }
    }

    private void ShowTest(string text, string brush) { TestResult = text; TestBrush = brush; }

    public bool TrySave()
    {
        var a = Build();
        if (a.Name.Length == 0) { ErrorText = Loc["apps.validation.name"]; return false; }
        if (a.ExecutablePath.Length == 0) { ErrorText = Loc["apps.validation.path"]; return false; }
        if (_registry.Apps.Any(x => x.Id != a.Id && string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
        { ErrorText = Loc["apps.validation.dup"]; return false; }

        _registry.AddOrUpdate(a);
        Result = a;
        return true;
    }
}

