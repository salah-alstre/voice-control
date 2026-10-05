using System.Collections.Specialized;
using System.Windows.Controls;

namespace VoiceCommander.App.Views;

public partial class DeveloperView : UserControl
{
    public DeveloperView()
    {
        InitializeComponent();
        // Keep the newest log line in view.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }
}
