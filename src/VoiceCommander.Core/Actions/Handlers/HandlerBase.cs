using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;

namespace VoiceCommander.Core.Actions.Handlers;

public static class Groups
{
    public const string Apps = "group.apps", System = "group.system", Audio = "group.audio", Flow = "group.flow", Power = "group.power";
}

/// <summary>Shared parameter/placeholder resolution for handlers.</summary>
public abstract class HandlerBase : ICommandActionHandler
{
    public abstract ActionDescriptor Descriptor { get; }
    public abstract Task<ActionResult> ExecuteAsync(ActionStep step, ActionContext context, CancellationToken ct);

    protected static string Param(ActionStep step, string key, string def = "") =>
        step.Parameters.TryGetValue(key, out var v) && v != null ? v : def;

    /// <summary>Expands {number} and {app} placeholders in free text.</summary>
    protected static string Expand(string text, ActionContext ctx)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (ctx.Number is int n) text = text.Replace("{number}", n.ToString(), StringComparison.OrdinalIgnoreCase);
        if (ctx.App != null) text = text.Replace("{app}", ctx.App.Name, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    protected static int? ResolveNumber(string raw, ActionContext ctx)
    {
        raw = (raw ?? "").Trim();
        if (raw.Equals("{number}", StringComparison.OrdinalIgnoreCase)) return ctx.Number;
        return int.TryParse(raw, out var n) ? n : null;
    }

    protected static AppDefinition? ResolveApp(IAppRegistry apps, ActionStep step, ActionContext ctx, string key = "app")
    {
        var raw = Param(step, key).Trim();
        if (raw.Equals("{app}", StringComparison.OrdinalIgnoreCase)) return ctx.App;
        return apps.FindById(raw) ?? apps.FindByName(raw);
    }

    protected static string ProcessNameOf(AppDefinition a) =>
        !string.IsNullOrWhiteSpace(a.ProcessName) ? a.ProcessName : Path.GetFileNameWithoutExtension(a.ExecutablePath ?? "");

    protected static ActionResult NoApp(ActionContext ctx, ActionStep step) =>
        ActionResult.Fail(ctx.Loc.Get("msg.noapp", Param(step, "app")));

    protected static ParameterDefinition AppParam(bool required = true) =>
        new("app", "param.app", ParameterKind.App, required, "", null, "param.app.hint");
}
