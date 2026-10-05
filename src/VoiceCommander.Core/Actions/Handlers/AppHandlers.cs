using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.Core.Actions.Handlers;

/// <summary>Open/close/restart/focus/minimize/maximize for registry applications.</summary>
public sealed class AppActionHandler : HandlerBase
{
    private readonly IAppRegistry _apps;
    private readonly IAppController _ctl;

    public AppActionHandler(string type, string nameKey, string icon, IAppRegistry apps, IAppController ctl)
    {
        _apps = apps; _ctl = ctl;
        Descriptor = new ActionDescriptor(type, nameKey, icon, Groups.Apps, new[] { AppParam() });
    }

    public override ActionDescriptor Descriptor { get; }

    public override async Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var app = ResolveApp(_apps, step, c);
        if (app == null) return NoApp(c, step);
        var locate = new RecoveryAction(RecoveryKind.LocateApp, app.Id, c.Loc.Get("recovery.locate", app.Name));

        AppControlResult r;
        string okKey;
        switch (Descriptor.Type)
        {
            case ActionTypes.OpenApp: r = await _ctl.OpenAsync(app); okKey = "msg.opened"; break;
            case ActionTypes.CloseApp: r = await _ctl.CloseAsync(app); okKey = "msg.closed"; break;
            case ActionTypes.RestartApp: r = await _ctl.RestartAsync(app); okKey = "msg.restarted"; break;
            case ActionTypes.FocusApp: r = _ctl.Focus(app); okKey = "msg.focused"; break;
            case ActionTypes.MinimizeApp: r = _ctl.Minimize(app); okKey = "msg.minimized"; break;
            case ActionTypes.MaximizeApp: r = _ctl.Maximize(app); okKey = "msg.maximized"; break;
            default: return ActionResult.Fail("Unsupported app action " + Descriptor.Type);
        }

        return r.Status switch
        {
            AppControlStatus.Ok => ActionResult.Ok(c.Loc.Get(okKey, app.Name)),
            AppControlStatus.NotRunning => ActionResult.Fail(c.Loc.Get("msg.notrunning", app.Name)),
            AppControlStatus.NotFound => ActionResult.Fail(c.Loc.Get("msg.couldnotopen", app.Name), locate),
            _ => ActionResult.Fail(c.Loc.Get("msg.couldnotopen", app.Name) + (string.IsNullOrEmpty(r.Detail) ? "" : " (" + r.Detail + ")"), locate),
        };
    }
}

public sealed class FolderHandler : HandlerBase
{
    private readonly IShellService _shell;
    public FolderHandler(IShellService shell) => _shell = shell;

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.OpenFolder, "action.folder.open", "", Groups.System,
        new[] { new ParameterDefinition("path", "param.path", ParameterKind.Folder) });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var path = Expand(Param(step, "path"), c);
        var err = _shell.OpenFolder(path);
        return Task.FromResult(err == null ? ActionResult.Ok(c.Loc.Get("msg.folderopened")) : ActionResult.Fail(err));
    }
}

public sealed class UrlHandler : HandlerBase
{
    private readonly IShellService _shell;
    public UrlHandler(IShellService shell) => _shell = shell;

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.OpenUrl, "action.url.open", "", Groups.System,
        new[] { new ParameterDefinition("url", "param.url", ParameterKind.Url, true, "https://") });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var err = _shell.OpenUrl(Expand(Param(step, "url"), c));
        return Task.FromResult(err == null ? ActionResult.Ok(c.Loc.Get("msg.urlopened")) : ActionResult.Fail(err));
    }
}

public sealed class SettingsHandler : HandlerBase
{
    private readonly IShellService _shell;
    public SettingsHandler(IShellService shell) => _shell = shell;

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.OpenSettings, "action.windows.settings", "", Groups.System,
        new[] { new ParameterDefinition("uri", "param.settingsuri", ParameterKind.Text, true, "ms-settings:") });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var err = _shell.OpenSettings(Param(step, "uri", "ms-settings:"));
        return Task.FromResult(err == null ? ActionResult.Ok(c.Loc.Get("msg.settingsopened")) : ActionResult.Fail(err));
    }
}
