using System.Windows;
using Microsoft.Win32;
using VoiceCommander.Core.Pipeline;

namespace VoiceCommander.App.Infrastructure;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string? yes = null, string? no = null, bool danger = false);
    void Info(string title, string message);
    /// <summary>Three-way choice. Returns 1 for <paramref name="first"/>, 2 for <paramref name="second"/>, 0 for cancel.</summary>
    Task<int> ChoiceAsync(string title, string message, string first, string second, string cancel);
    string? PickFile(string filter, string? title = null);
    string? PickSaveFile(string filter, string defaultName, string? title = null);
    string? PickFolder(string? title = null);
    Window? Owner { get; }
}

/// <summary>Thin wrapper over the WPF dialogs so view models stay testable and never touch Window directly.</summary>
public sealed class DialogService : IDialogService, IConfirmationService
{
    public Window? Owner
    {
        get
        {
            var app = System.Windows.Application.Current;
            if (app == null) return null;
            return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                   ?? (app.MainWindow is { IsVisible: true } m ? m : null);
        }
    }

    public Task<bool> ConfirmAsync(string title, string message) => ConfirmAsync(title, message, null, null, false);

    public Task<bool> ConfirmAsync(string title, string message, string? yes, string? no, bool danger) =>
        UI.InvokeAsync(() =>
        {
            var dlg = new Dialogs.ConfirmDialog(title, message, yes, no, danger);
            var owner = Owner;
            if (owner != null && !ReferenceEquals(owner, dlg)) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dlg.ShowDialog() == true;
        });

    public Task<int> ChoiceAsync(string title, string message, string first, string second, string cancel) =>
        UI.InvokeAsync(() =>
        {
            var dlg = new Dialogs.ConfirmDialog(title, message, first, cancel, false);
            dlg.UseAlternative(second);
            var owner = Owner;
            if (owner != null) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return dlg.Choice;
        });

    public void Info(string title, string message) =>
        UI.InvokeAsync(() =>
        {
            var dlg = new Dialogs.ConfirmDialog(title, message, LocSource.Instance["common.ok"], null, false);
            var owner = Owner;
            if (owner != null) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return true;
        });

    public string? PickFile(string filter, string? title = null)
    {
        var d = new OpenFileDialog { Filter = filter, Title = title ?? "", CheckFileExists = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? PickSaveFile(string filter, string defaultName, string? title = null)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = defaultName, Title = title ?? "", OverwritePrompt = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? PickFolder(string? title = null)
    {
        var d = new OpenFolderDialog { Title = title ?? "" };
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }
}
