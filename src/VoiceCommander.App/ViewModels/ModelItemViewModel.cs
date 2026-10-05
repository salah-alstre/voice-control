using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

/// <summary>One downloadable speech model: shows install state and runs download / cancel / remove.</summary>
public sealed partial class ModelItemViewModel : ObservableObject
{
    private readonly ISpeechModelManager _models;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public ModelItemViewModel(SpeechModelInfo info, ISpeechModelManager models, IDialogService dialogs)
    {
        Info = info; _models = models; _dialogs = dialogs;
    }

    public SpeechModelInfo Info { get; }
    public string Name => Info.Name;
    /// <summary>Model names are English; LRM marks keep the trailing ")" in place inside right-to-left text.</summary>
    public string DisplayName => "‎" + Info.Name + "‎";
    public string LanguageText => LocSource.Instance["lang." + Info.Language];
    public string SizeText => LocSource.Instance.Get("model.size", (int)Math.Round(Info.SizeBytes / 1048576.0));
    public bool IsInstalled => _models.IsInstalled(Info);
    public string StatusText => IsDownloading
        ? LocSource.Instance.Get("model.downloading", (int)Math.Round(Progress * 100))
        : LocSource.Instance[IsInstalled ? "model.installed" : "model.notinstalled"];
    public string StatusBrush => IsInstalled ? "SuccessBrush" : "TextMutedBrush";
    public bool CanDownload => !IsInstalled && !IsDownloading;
    public bool CanRemove => IsInstalled && !IsDownloading;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove), nameof(StatusText))]
    private bool _isDownloading;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    /// <summary>Re-reads install state and localized text (after a download, removal or language switch).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading) return;
        _cts = new CancellationTokenSource();
        Progress = 0;
        IsDownloading = true;
        try
        {
            // Created on the UI thread, so reports arrive on the UI thread.
            await _models.DownloadAsync(Info, new Progress<double>(p => Progress = p), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dialogs.Info(LocSource.Instance["model.title"], LocSource.Instance.Get("model.downloadfailed", ex.Message));
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task RemoveAsync()
    {
        var loc = LocSource.Instance;
        if (!await _dialogs.ConfirmAsync(loc["model.remove"], loc.Get("model.remove.body", Info.Name),
                loc["model.remove"], loc["common.cancel"], danger: true)) return;
        await Task.Run(() => _models.Remove(Info));
        Refresh();
    }
}
