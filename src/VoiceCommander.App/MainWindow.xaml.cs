using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.App.ViewModels;
using VoiceCommander.Core.Services;

namespace VoiceCommander.App;

public partial class MainWindow : WindowBase
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, ISettingsService settings)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        var s = settings.Current;
        if (s.WindowWidth is double w && s.WindowHeight is double h && w >= MinWidth && h >= MinHeight)
        {
            Width = Math.Min(w, SystemParameters.WorkArea.Width);
            Height = Math.Min(h, SystemParameters.WorkArea.Height);
        }
    }

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavItemViewModel item }) _vm.Selected = item;
    }
}
