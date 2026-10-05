using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using VoiceCommander.Core.Localization;

namespace VoiceCommander.App.Infrastructure;

/// <summary>
/// Bridge between the Core localizer and WPF. XAML binds to <c>[key]</c> on this object; a language switch raises
/// <c>Item[]</c> so every bound string (and the flow direction) refreshes immediately.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private ILocalizer? _loc;
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised on the UI thread after the language changed. View models use this to refresh computed strings.</summary>
    public event EventHandler? LanguageChanged;

    public void Init(ILocalizer loc)
    {
        _loc = loc;
        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != "Item[]") return;
            UI.Post(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
                LanguageChanged?.Invoke(this, EventArgs.Empty);
            });
        };
    }

    public ILocalizer Localizer => _loc ?? throw new InvalidOperationException("Localizer not initialised");

    public string this[string key] => _loc?[key] ?? key;
    public string Get(string key, params object?[] args) => _loc?.Get(key, args) ?? key;
    public FlowDirection FlowDirection => _loc?.IsRtl == true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
}

/// <summary><c>{loc:Loc nav.dashboard}</c> — a live-updating localized string.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
