using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceCommander.App.Infrastructure;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.App.ViewModels;

/// <summary>One downloadable Whisper model: install state, download / cancel / remove, "use this one" and full validation.</summary>
public sealed partial class WhisperModelItemViewModel : ObservableObject
{
    private readonly IWhisperModelManager _models;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public WhisperModelItemViewModel(WhisperModelInfo info, IWhisperModelManager models, IDialogService dialogs)
    {
        Info = info; _models = models; _dialogs = dialogs;
    }

    public WhisperModelInfo Info { get; }
    public string DisplayName => "‎" + Info.Name + "‎";
    public string SizeText => LocSource.Instance.Get("model.size", (int)Math.Round(Info.SizeBytes / 1048576.0));
    public bool IsInstalled => _models.IsInstalled(Info);
    /// <summary>True when this catalog model is the one the engine loads (selected, installed and no custom file overrides it).</summary>
    public bool IsActive => IsInstalled && _models.CustomPath == null && _models.Selected.Id == Info.Id;
    public bool IsBusy => IsDownloading || IsValidating;
    public string StatusText => IsDownloading
            ? LocSource.Instance.Get("model.downloading", (int)Math.Round(Progress * 100))
            : IsValidating
                ? LocSource.Instance.Get("whisper.validating", (int)Math.Round(Progress * 100))
                : LocSource.Instance[IsInstalled ? "model.installed" : "model.notinstalled"]
                  + (IsActive ? "  ·  " + LocSource.Instance["whisper.active"] : "");
    public string StatusBrush => IsInstalled ? "SuccessBrush" : "TextMutedBrush";
    public bool CanDownload => !IsInstalled && !IsBusy;
    public bool CanRemove => IsInstalled && !IsBusy;
    public bool CanUse => IsInstalled && !IsActive && !IsBusy;
    public bool CanValidate => IsInstalled && !IsBusy;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove), nameof(CanUse), nameof(CanValidate), nameof(IsBusy), nameof(StatusText))]
    private bool _isDownloading;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove), nameof(CanUse), nameof(CanValidate), nameof(IsBusy), nameof(StatusText))]
    private bool _isValidating;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    /// <summary>Re-reads install state and localized text (after a download, removal, selection or language switch).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Localized text for a <c>model.*</c> reason key, falling back to the raw text.</summary>
    public static string Describe(string key, string? fallback = null) =>
        LocSource.Instance.Localizer.Has(key) ? LocSource.Instance[key] : fallback ?? key;

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        Progress = 0;
        IsDownloading = true;
        try
        {
            // Created on the UI thread, so reports arrive on the UI thread.
            await _models.DownloadAsync(Info, new Progress<double>(p => Progress = p), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WhisperModelException ex)
        {
            _dialogs.Info(LocSource.Instance["whisper.title"], LocSource.Instance.Get("model.downloadfailed", Describe(ex.Key, ex.Message)));
        }
        catch (Exception ex)
        {
            _dialogs.Info(LocSource.Instance["whisper.title"], LocSource.Instance.Get("model.downloadfailed", ex.Message));
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

    [RelayCommand]
    private void Use()
    {
        _models.Select(Info.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        Progress = 0;
        IsValidating = true;
        string? error;
        try
        {
            error = await _models.ValidateAsync(_models.InstallPath(Info), new Progress<double>(p => Progress = p), _cts.Token);
        }
        catch (OperationCanceledException) { error = null; IsValidating = false; _cts?.Dispose(); _cts = null; Refresh(); return; }
        catch (Exception ex) { error = ex.Message; }
        IsValidating = false;
        _cts?.Dispose();
        _cts = null;
        var loc = LocSource.Instance;
        _dialogs.Info(loc["whisper.title"], error == null ? loc["whisper.valid"] : Describe(error));
        Refresh();
    }
}
