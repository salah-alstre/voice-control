using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;

namespace VoiceCommander.App.Infrastructure;

/// <summary>Runs the "[Locate Discord]" style recovery buttons that failed actions offer.</summary>
public interface IRecoveryService
{
    /// <summary>Returns true when the problem was fixed (the user pointed at the executable).</summary>
    bool Run(RecoveryAction recovery);
}

public sealed class RecoveryService : IRecoveryService
{
    private readonly IAppRegistry _apps;
    private readonly IDialogService _dialogs;

    public RecoveryService(IAppRegistry apps, IDialogService dialogs) { _apps = apps; _dialogs = dialogs; }

    public bool Run(RecoveryAction recovery)
    {
        if (recovery.Kind != RecoveryKind.LocateApp) return false;
        var app = _apps.FindById(recovery.TargetId);
        if (app == null) return false;
        var path = _dialogs.PickFile(LocSource.Instance["apps.exe.filter"], recovery.Label);
        if (path == null) return false;
        var edited = app.Clone();
        edited.ExecutablePath = path;
        if (string.IsNullOrWhiteSpace(edited.ProcessName)) edited.ProcessName = System.IO.Path.GetFileNameWithoutExtension(path);
        _apps.AddOrUpdate(edited);
        return true;
    }
}
