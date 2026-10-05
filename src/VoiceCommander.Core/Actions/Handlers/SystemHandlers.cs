using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.Core.Actions.Handlers;

public sealed class ScreenshotHandler : HandlerBase
{
    private readonly IScreenshotService _shots;
    private readonly ISettingsService _settings;
    private readonly INotifier _notifier;

    public ScreenshotHandler(IScreenshotService shots, ISettingsService settings, INotifier notifier)
    { _shots = shots; _settings = settings; _notifier = notifier; }

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.Screenshot, "action.screenshot", "", Groups.System,
        new[]
        {
            new ParameterDefinition("target", "param.target", ParameterKind.Choice, true, "screen", new[]
            {
                new ChoiceOption("screen", "choice.screen"), new ChoiceOption("window", "choice.window"),
            }),
        });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var s = _settings.Current;
        try
        {
            var path = _shots.Capture(Param(step, "target", "screen").Equals("window", StringComparison.OrdinalIgnoreCase),
                s.EffectiveScreenshotDirectory, s.ScreenshotFormat);
            if (s.ShowNotifications) _notifier.Notify(c.Loc["msg.screenshot.title"], Path.GetFileName(path));
            return Task.FromResult(ActionResult.Ok(c.Loc.Get("msg.screenshotsaved", path)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail(c.Loc.Get("msg.screenshotfailed", ex.Message)));
        }
    }
}

public sealed class WaitHandler : HandlerBase
{
    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.Wait, "action.wait", "", Groups.Flow,
        new[] { new ParameterDefinition("seconds", "param.seconds", ParameterKind.Number, true, "1") });

    public override async Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        if (!double.TryParse(Param(step, "seconds", "1"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs) || secs < 0)
            return ActionResult.Fail(c.Loc.Get("msg.badseconds"));
        secs = Math.Min(secs, 300);
        await Task.Delay(TimeSpan.FromSeconds(secs), ct);
        return ActionResult.Ok(c.Loc.Get("msg.waited", secs));
    }
}

public sealed class HotkeyHandler : HandlerBase
{
    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.Hotkey, "action.keyboard.shortcut", "", Groups.System,
        new[] { new ParameterDefinition("keys", "param.keys", ParameterKind.Hotkey, true, "", null, "param.keys.hint") });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var keys = Param(step, "keys");
        try
        {
            KeyboardInjector.SendChord(keys);
            return Task.FromResult(ActionResult.Ok(c.Loc.Get("msg.keyssent", keys)));
        }
        catch (FormatException ex) { return Task.FromResult(ActionResult.Fail(c.Loc.Get("msg.badkeys", ex.Message))); }
        catch (Exception ex) { return Task.FromResult(ActionResult.Fail(ex.Message)); }
    }
}

public sealed class NotificationHandler : HandlerBase
{
    private readonly INotifier _notifier;
    public NotificationHandler(INotifier notifier) => _notifier = notifier;

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.Notification, "action.notification", "", Groups.Flow,
        new[]
        {
            new ParameterDefinition("title", "param.title", ParameterKind.Text, true, "Voice Commander"),
            new ParameterDefinition("message", "param.message", ParameterKind.Text, false),
        });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        _notifier.Notify(Expand(Param(step, "title", "Voice Commander"), c), Expand(Param(step, "message"), c));
        return Task.FromResult(ActionResult.Ok(c.Loc.Get("msg.notified")));
    }
}

/// <summary>Lock, show desktop, and the (confirmation-gated) power actions. All use fixed system calls.</summary>
public sealed class SystemActionHandler : HandlerBase
{
    private readonly IShellService _shell;
    private readonly string _type;

    public SystemActionHandler(string type, string nameKey, string icon, bool dangerous, IShellService shell)
    {
        _type = type; _shell = shell;
        Descriptor = new ActionDescriptor(type, nameKey, icon, dangerous ? Groups.Power : Groups.System, Array.Empty<ParameterDefinition>(), dangerous);
    }

    public override ActionDescriptor Descriptor { get; }

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        try
        {
            switch (_type)
            {
                case ActionTypes.Lock: _shell.Lock(); break;
                case ActionTypes.ShowDesktop: _shell.ShowDesktop(); break;
                case ActionTypes.Shutdown: _shell.Shutdown(); break;
                case ActionTypes.Restart: _shell.Restart(); break;
                case ActionTypes.SignOut: _shell.SignOut(); break;
                case ActionTypes.Sleep: _shell.Sleep(); break;
                default: return Task.FromResult(ActionResult.Fail("Unsupported action " + _type));
            }
            return Task.FromResult(ActionResult.Ok(c.Loc.Get("msg.done")));
        }
        catch (Exception ex) { return Task.FromResult(ActionResult.Fail(ex.Message)); }
    }
}

/// <summary>Builds the full handler set. One registration point keeps DI trivial and the registry additive.</summary>
public static class ActionHandlerFactory
{
    public static IEnumerable<ICommandActionHandler> CreateAll(IAppRegistry apps, IAppController ctl, IAudioService audio,
        IScreenshotService shots, IShellService shell, ISettingsService settings, INotifier notifier)
    {
        yield return new AppActionHandler(ActionTypes.OpenApp, "action.app.open", "", apps, ctl);
        yield return new AppActionHandler(ActionTypes.CloseApp, "action.app.close", "", apps, ctl);
        yield return new AppActionHandler(ActionTypes.RestartApp, "action.app.restart", "", apps, ctl);
        yield return new AppActionHandler(ActionTypes.FocusApp, "action.app.focus", "", apps, ctl);
        yield return new AppActionHandler(ActionTypes.MinimizeApp, "action.app.minimize", "", apps, ctl);
        yield return new AppActionHandler(ActionTypes.MaximizeApp, "action.app.maximize", "", apps, ctl);
        yield return new FolderHandler(shell);
        yield return new UrlHandler(shell);
        yield return new SettingsHandler(shell);
        yield return new VolumeSetHandler(audio);
        yield return new VolumeChangeHandler(audio);
        yield return new MuteHandler(audio);
        yield return new AppVolumeHandler(ActionTypes.AppVolumeSet, "action.volume.app.set", apps, audio);
        yield return new AppVolumeHandler(ActionTypes.AppVolumeChange, "action.volume.app.change", apps, audio);
        yield return new AppVolumeHandler(ActionTypes.AppMute, "action.volume.app.mute", apps, audio);
        yield return new ScreenshotHandler(shots, settings, notifier);
        yield return new WaitHandler();
        yield return new HotkeyHandler();
        yield return new NotificationHandler(notifier);
        yield return new SystemActionHandler(ActionTypes.Lock, "action.windows.lock", "", false, shell);
        yield return new SystemActionHandler(ActionTypes.ShowDesktop, "action.windows.showdesktop", "", false, shell);
        yield return new SystemActionHandler(ActionTypes.Shutdown, "action.power.shutdown", "", true, shell);
        yield return new SystemActionHandler(ActionTypes.Restart, "action.power.restart", "", true, shell);
        yield return new SystemActionHandler(ActionTypes.SignOut, "action.power.signout", "", true, shell);
        yield return new SystemActionHandler(ActionTypes.Sleep, "action.power.sleep", "", true, shell);
    }
}
