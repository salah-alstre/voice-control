using System.Windows;
using System.Windows.Data;

namespace VoiceCommander.App.Infrastructure;

/// <summary>Base for every window: theme background, live RTL flow direction, dark title bar.</summary>
public class WindowBase : Window
{
    public WindowBase()
    {
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        SetBinding(FlowDirectionProperty, new Binding(nameof(LocSource.FlowDirection)) { Source = LocSource.Instance, Mode = BindingMode.OneWay });
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
    }
}
