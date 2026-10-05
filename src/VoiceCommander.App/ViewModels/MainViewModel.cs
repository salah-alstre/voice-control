using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(string key, string glyph, Type viewModelType)
    {
        Key = key; Glyph = glyph; ViewModelType = viewModelType;
    }

    public string Key { get; }
    public string Glyph { get; }
    public Type ViewModelType { get; }
    public string Title => LocSource.Instance["nav." + Key];
    public bool IsDeveloper => Key == "developer";
    public void Refresh() => OnPropertyChanged(nameof(Title));

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, ViewModelBase> _pages = new();

    private readonly IListeningService _listening;

    public MainViewModel(IServiceProvider services, ISettingsService settings, IListeningService listening)
    {
        _services = services;
        _settings = settings;
        _listening = listening;

        Items = new ObservableCollection<NavItemViewModel>
        {
            new("dashboard", "", typeof(DashboardViewModel)),
            new("commands", "", typeof(CommandsViewModel)),
            new("workflows", "", typeof(WorkflowsViewModel)),
            new("applications", "", typeof(ApplicationsViewModel)),
            new("history", "", typeof(HistoryViewModel)),
            new("microphone", "", typeof(MicrophoneViewModel)),
            new("settings", "", typeof(SettingsViewModel)),
            new("developer", "", typeof(DeveloperViewModel)),
        };
        TopItems = Items.Where(i => i.Key is not ("settings" or "developer")).ToList();
        BottomItems = Items.Where(i => i.Key is "developer" or "settings").ToList();
        UpdateDeveloperVisibility();
        _settings.Changed += () => UI.Post(() => { UpdateDeveloperVisibility(); RefreshMic(); });
        _listening.StateChanged += () => UI.Post(RefreshMic);
    }

    public ObservableCollection<NavItemViewModel> Items { get; }

    /// <summary>Main navigation (top of the sidebar).</summary>
    public IReadOnlyList<NavItemViewModel> TopItems { get; }

    /// <summary>Developer (when enabled) and Settings, pinned to the bottom of the sidebar.</summary>
    public IReadOnlyList<NavItemViewModel> BottomItems { get; }

    // ---- compact mic status (sidebar footer) ------------------------------------------------------------------------
    public bool IsListening => _listening.State == ListeningState.Listening;
    public string MicStatusText => Loc[IsListening ? "dash.status.listening" : "dash.status.standby"];
    public string MicStatusBrush => IsListening ? "SuccessBrush" : "TextMutedBrush";
    public string MicName => string.IsNullOrEmpty(_settings.Current.MicrophoneName) ? Loc["dash.mic.default"] : _settings.Current.MicrophoneName!;

    private void RefreshMic()
    {
        OnPropertyChanged(nameof(IsListening)); OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(MicStatusBrush)); OnPropertyChanged(nameof(MicName));
    }

    [ObservableProperty] private NavItemViewModel? _selected;
    [ObservableProperty] private ViewModelBase? _current;

    partial void OnSelectedChanged(NavItemViewModel? value)
    {
        foreach (var i in Items) i.IsSelected = ReferenceEquals(i, value);
        if (value == null) return;
        if (!_pages.TryGetValue(value.Key, out var vm))
        {
            vm = (ViewModelBase)_services.GetService(value.ViewModelType)!;
            _pages[value.Key] = vm;
        }
        Current = vm;
    }

    /// <summary>Selects the page and returns the view model (created on first use).</summary>
    public void Navigate(string key)
    {
        var item = Items.FirstOrDefault(i => i.Key == key && i.IsVisible);
        if (item != null) Selected = item;
    }

    public void Start() => Navigate("dashboard");

    private void UpdateDeveloperVisibility()
    {
        foreach (var i in Items.Where(i => i.IsDeveloper))
        {
            i.IsVisible = _settings.Current.DeveloperMode;
            if (!i.IsVisible && Selected == i) Navigate("dashboard");
        }
    }

    protected override void OnLanguageChanged()
    {
        foreach (var i in Items) i.Refresh();
        RefreshMic();
        base.OnLanguageChanged();
    }
}
