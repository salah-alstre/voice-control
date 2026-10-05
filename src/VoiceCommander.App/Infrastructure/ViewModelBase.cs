using CommunityToolkit.Mvvm.ComponentModel;

namespace VoiceCommander.App.Infrastructure;

public abstract class ViewModelBase : ObservableObject, IDisposable
{
    protected ViewModelBase()
    {
        LocSource.Instance.LanguageChanged += OnLanguageChangedInternal;
    }

    public LocSource Loc => LocSource.Instance;

    private void OnLanguageChangedInternal(object? sender, EventArgs e) => OnLanguageChanged();

    /// <summary>Refreshes every computed string. Override to rebuild cached localized text first.</summary>
    protected virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);

    public virtual void Dispose()
    {
        LocSource.Instance.LanguageChanged -= OnLanguageChangedInternal;
        GC.SuppressFinalize(this);
    }
}
