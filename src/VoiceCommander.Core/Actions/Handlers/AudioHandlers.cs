using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.Core.Actions.Handlers;

public sealed class VolumeSetHandler : HandlerBase
{
    private readonly IAudioService _audio;
    public VolumeSetHandler(IAudioService audio) => _audio = audio;

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.VolumeSet, "action.volume.set", "", Groups.Audio,
        new[] { new ParameterDefinition("value", "param.volume", ParameterKind.Text, true, "50", null, "param.volume.hint") });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var n = ResolveNumber(Param(step, "value"), c);
        if (n is null) return Task.FromResult(ActionResult.Fail(c.Loc.Get("msg.needsnumber")));
        n = Math.Clamp(n.Value, 0, 100);
        _audio.SetMasterVolume(n.Value);
        return Task.FromResult(ActionResult.Ok(c.Loc.Get("msg.volumeset", n.Value)));
    }
}

public sealed class VolumeChangeHandler : HandlerBase
{
    private readonly IAudioService _audio;
    public VolumeChangeHandler(IAudioService audio) => _audio = audio;

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.VolumeChange, "action.volume.change", "", Groups.Audio,
        new[] { new ParameterDefinition("amount", "param.amount", ParameterKind.Text, true, "10", null, "param.amount.hint") });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var amount = ResolveNumber(Param(step, "amount", "10"), c);
        if (amount is null) return Task.FromResult(ActionResult.Fail(c.Loc.Get("msg.needsnumber")));
        var now = _audio.ChangeMasterVolume(amount.Value);
        return Task.FromResult(ActionResult.Ok(c.Loc.Get("msg.volumeset", now)));
    }
}

public sealed class MuteHandler : HandlerBase
{
    private readonly IAudioService _audio;
    public MuteHandler(IAudioService audio) => _audio = audio;

    public static readonly IReadOnlyList<ChoiceOption> Modes = new[]
    {
        new ChoiceOption("mute", "choice.mute"), new ChoiceOption("unmute", "choice.unmute"), new ChoiceOption("toggle", "choice.toggle"),
    };

    public override ActionDescriptor Descriptor { get; } = new(ActionTypes.Mute, "action.volume.mute", "", Groups.Audio,
        new[] { new ParameterDefinition("mode", "param.mode", ParameterKind.Choice, true, "mute", Modes) });

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var mode = Param(step, "mode", "mute").ToLowerInvariant();
        var muted = mode switch { "unmute" => false, "toggle" => !_audio.IsMasterMuted(), _ => true };
        _audio.SetMasterMuted(muted);
        return Task.FromResult(ActionResult.Ok(c.Loc.Get(muted ? "msg.muted" : "msg.unmuted")));
    }
}

/// <summary>Per-application volume/mute. Reports clearly when the app is not currently producing audio.</summary>
public sealed class AppVolumeHandler : HandlerBase
{
    private readonly IAppRegistry _apps;
    private readonly IAudioService _audio;

    public AppVolumeHandler(string type, string nameKey, IAppRegistry apps, IAudioService audio)
    {
        _apps = apps; _audio = audio;
        var extra = type switch
        {
            ActionTypes.AppVolumeSet => new ParameterDefinition("value", "param.volume", ParameterKind.Text, true, "50", null, "param.volume.hint"),
            ActionTypes.AppVolumeChange => new ParameterDefinition("amount", "param.amount", ParameterKind.Text, true, "10", null, "param.amount.hint"),
            _ => new ParameterDefinition("mode", "param.mode", ParameterKind.Choice, true, "mute", MuteHandler.Modes),
        };
        Descriptor = new ActionDescriptor(type, nameKey, "", Groups.Audio, new[] { AppParam(), extra });
    }

    public override ActionDescriptor Descriptor { get; }

    public override Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext c, CancellationToken ct)
    {
        var app = ResolveApp(_apps, step, c);
        if (app == null) return Task.FromResult(NoApp(c, step));
        var proc = ProcessNameOf(app);
        int affected;
        string msg;
        switch (Descriptor.Type)
        {
            case ActionTypes.AppVolumeSet:
            {
                var n = ResolveNumber(Param(step, "value"), c);
                if (n is null) return Task.FromResult(ActionResult.Fail(c.Loc.Get("msg.needsnumber")));
                n = Math.Clamp(n.Value, 0, 100);
                affected = _audio.SetAppVolume(proc, n.Value);
                msg = c.Loc.Get("msg.appvolumeset", app.Name, n.Value);
                break;
            }
            case ActionTypes.AppVolumeChange:
            {
                var n = ResolveNumber(Param(step, "amount", "10"), c);
                if (n is null) return Task.FromResult(ActionResult.Fail(c.Loc.Get("msg.needsnumber")));
                affected = _audio.ChangeAppVolume(proc, n.Value);
                msg = c.Loc.Get("msg.appvolumechanged", app.Name);
                break;
            }
            default:
            {
                var mode = Param(step, "mode", "mute").ToLowerInvariant();
                bool? muted = mode switch { "unmute" => false, "toggle" => null, _ => true };
                affected = _audio.SetAppMuted(proc, muted);
                msg = c.Loc.Get(mode == "unmute" ? "msg.appunmuted" : mode == "toggle" ? "msg.apptoggled" : "msg.appmuted", app.Name);
                break;
            }
        }
        return Task.FromResult(affected > 0 ? ActionResult.Ok(msg) : ActionResult.Fail(c.Loc.Get("msg.noaudio", app.Name)));
    }
}
