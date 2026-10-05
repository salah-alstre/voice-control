using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Actions.Handlers;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Windows;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Services;

/// <summary>The "Open CS2" acceptance scenario: registered app + custom command + Open Application action.</summary>
public class ApplicationCommandTests
{
    private sealed class FakeRegistry : IAppRegistry
    {
        private readonly List<AppDefinition> _apps = new();
        public IReadOnlyList<AppDefinition> Apps => _apps;
        public event Action? Changed;
        public AppDefinition? FindById(string? id) => _apps.FirstOrDefault(a => a.Id == id);
        public AppDefinition? FindByName(string name) => _apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        public void AddOrUpdate(AppDefinition app) { _apps.RemoveAll(a => a.Id == app.Id); _apps.Add(app); Changed?.Invoke(); }
        public bool Remove(string id) { var n = _apps.RemoveAll(a => a.Id == id); Changed?.Invoke(); return n > 0; }
        public void ReplaceAll(IEnumerable<AppDefinition> apps) { _apps.Clear(); _apps.AddRange(apps); Changed?.Invoke(); }
    }

    private sealed class FakeController : IAppController
    {
        public AppControlStatus OpenStatus { get; set; } = AppControlStatus.Ok;
        public List<AppDefinition> Opened { get; } = new();
        public Task<AppControlResult> OpenAsync(AppDefinition app) { Opened.Add(app); return Task.FromResult(new AppControlResult(OpenStatus)); }
        public Task<AppControlResult> CloseAsync(AppDefinition app) => Task.FromResult(new AppControlResult(AppControlStatus.Ok));
        public Task<AppControlResult> RestartAsync(AppDefinition app) => Task.FromResult(new AppControlResult(AppControlStatus.Ok));
        public AppControlResult Focus(AppDefinition app) => new(AppControlStatus.Ok);
        public AppControlResult Minimize(AppDefinition app) => new(AppControlStatus.Ok);
        public AppControlResult Maximize(AppDefinition app) => new(AppControlStatus.Ok);
        public bool IsRunning(AppDefinition app) => false;
    }

    private static AppDefinition Cs2() => new()
    {
        Id = "app.cs2", Name = "Counter-Strike 2", ExecutablePath = "steam://rungameid/730", ProcessName = "cs2.exe",
    };

    private static VoiceCommand OpenCs2(string appId) => new()
    {
        Id = "custom.open-cs2", Name = "Open CS2",
        Phrases = new() { "Open CS2", "Start CS2", "Launch CS2", "افتح سي اس", "شغل سي اس" },
        Actions = new() { new ActionStep(ActionTypes.OpenApp, ("app", appId)) },
    };

    private static ActionContext Ctx(VoiceCommand cmd) => new()
    {
        Command = cmd, Source = TriggerSource.Voice, Loc = new Localizer(),
    };

    private static AppActionHandler Handler(FakeRegistry reg, FakeController ctl) =>
        new(ActionTypes.OpenApp, "action.openapp", "", reg, ctl);

    // ---------- launch targets ----------

    [Theory]
    [InlineData("steam://rungameid/730", true, "steam")]
    [InlineData("ms-settings:", true, "ms-settings")]
    [InlineData("\"steam://rungameid/730\"", true, "steam")]
    [InlineData(@"C:\Games\cs2.exe", false, null)]
    [InlineData("notepad.exe", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void LaunchTarget_detects_uri_targets(string? target, bool isUri, string? scheme)
    {
        Assert.Equal(isUri, LaunchTarget.IsUri(target));
        Assert.Equal(scheme, LaunchTarget.SchemeOf(target));
    }

    [Fact]
    public void Locator_hands_uri_targets_to_the_shell_unchanged()
    {
        var r = new AppLocator().Resolve(new AppDefinition { Id = "x", Name = "CS2", ExecutablePath = "steam://rungameid/730", Arguments = "-novid" });
        Assert.NotNull(r);
        Assert.Equal("steam://rungameid/730", r!.FileName);
        Assert.Equal("-novid", r.Arguments);
    }

    [Fact]
    public void Locator_returns_null_for_empty_or_missing_executables()
    {
        using var sb = new Sandbox();
        var loc = new AppLocator();
        Assert.Null(loc.Resolve(new AppDefinition { Id = "e", Name = "E", ExecutablePath = "" }));
        Assert.Null(loc.Resolve(new AppDefinition { Id = "m", Name = "M", ExecutablePath = Path.Combine(sb.Root, "gone.exe") }));
    }

    [Fact]
    public void Locator_resolves_an_existing_rooted_executable()
    {
        using var sb = new Sandbox();
        var exe = Path.Combine(sb.Root, "tool.exe");
        File.WriteAllText(exe, "x");
        var r = new AppLocator().Resolve(new AppDefinition { Id = "t", Name = "Tool", ExecutablePath = exe, Arguments = "--a" });
        Assert.Equal(exe, r?.FileName);
        Assert.Equal("--a", r?.Arguments);
    }

    // ---------- matching ----------

    [Theory]
    [InlineData("Open CS2")]
    [InlineData("Start CS2")]
    [InlineData("Launch CS2")]
    [InlineData("افتح سي اس")]
    [InlineData("شغل سي اس")]
    public void Every_cs2_phrase_matches_the_custom_command(string said)
    {
        var cmd = OpenCs2(Cs2().Id);
        var eval = new CommandMatcher().Evaluate(said, new[] { cmd }, new[] { Cs2() });
        Assert.NotNull(eval.Result);
        Assert.Equal(cmd.Id, eval.Result!.Command.Id);
    }

    [Theory]
    [InlineData("Open CS2")]
    [InlineData("افتح سي اس")]
    public void Custom_command_still_wins_when_merged_with_built_ins(string said)
    {
        var cmd = OpenCs2(Cs2().Id);
        var commands = BuiltInCommands.Create().Append(cmd).ToList();
        var apps = BuiltInApps.Create().Append(Cs2()).ToList();
        var eval = new CommandMatcher().Evaluate(said, commands, apps);
        Assert.NotNull(eval.Result);
        Assert.Equal(cmd.Id, eval.Result!.Command.Id);
    }

    [Fact]
    public void Unregistered_application_name_is_not_matched()
    {
        var eval = new CommandMatcher().Evaluate("Open Disk", BuiltInCommands.Create(), BuiltInApps.Create());
        Assert.Null(eval.Result);
    }

    // ---------- execution ----------

    [Fact]
    public async Task Open_application_action_launches_the_registered_app()
    {
        var reg = new FakeRegistry(); reg.AddOrUpdate(Cs2());
        var ctl = new FakeController();
        var cmd = OpenCs2("app.cs2");
        var res = await Handler(reg, ctl).ExecuteAsync(cmd.Actions[0], Ctx(cmd), CancellationToken.None);
        Assert.True(res.Success);
        Assert.Equal("app.cs2", Assert.Single(ctl.Opened).Id);
        Assert.Equal("cs2.exe", ctl.Opened[0].ProcessName);
    }

    [Fact]
    public async Task Open_application_action_survives_a_deleted_application()
    {
        var reg = new FakeRegistry();           // app was removed from the registry
        var ctl = new FakeController();
        var cmd = OpenCs2("app.cs2");
        var res = await Handler(reg, ctl).ExecuteAsync(cmd.Actions[0], Ctx(cmd), CancellationToken.None);
        Assert.False(res.Success);
        Assert.Empty(ctl.Opened);
    }

    [Fact]
    public async Task Missing_executable_fails_with_a_locate_recovery()
    {
        var reg = new FakeRegistry(); reg.AddOrUpdate(Cs2());
        var ctl = new FakeController { OpenStatus = AppControlStatus.NotFound };
        var cmd = OpenCs2("app.cs2");
        var res = await Handler(reg, ctl).ExecuteAsync(cmd.Actions[0], Ctx(cmd), CancellationToken.None);
        Assert.False(res.Success);
        Assert.Equal(RecoveryKind.LocateApp, res.Recovery?.Kind);
        Assert.Equal("app.cs2", res.Recovery?.TargetId);
    }

    [Fact]
    public async Task Application_can_also_be_referenced_by_name()
    {
        var reg = new FakeRegistry(); reg.AddOrUpdate(Cs2());
        var ctl = new FakeController();
        var cmd = OpenCs2("Counter-Strike 2");
        var res = await Handler(reg, ctl).ExecuteAsync(cmd.Actions[0], Ctx(cmd), CancellationToken.None);
        Assert.True(res.Success);
        Assert.Single(ctl.Opened);
    }

    // ---------- push-to-talk / assistant settings ----------

    [Fact]
    public void Ptt_sound_and_assistant_settings_persist_and_are_clamped()
    {
        using var sb = new Sandbox();
        var a = new SettingsService(new NullLog());
        Assert.True(a.Current.PttSoundsEnabled);
        a.Current.PttSoundsEnabled = false;
        a.Current.PttSoundVolume = 7;          // out of range
        a.Current.AssistantOpacity = 0;        // out of range
        a.Save();

        var b = new SettingsService(new NullLog());
        Assert.False(b.Current.PttSoundsEnabled);
        Assert.Equal(1, b.Current.PttSoundVolume);
        Assert.Equal(0.3, b.Current.AssistantOpacity);
    }

    [Fact]
    public void Custom_command_and_application_survive_a_json_round_trip()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Root, "cmd.json");
        var cmd = OpenCs2("app.cs2");
        VoiceCommander.Core.Infrastructure.JsonStore.Save(path, cmd);
        var back = VoiceCommander.Core.Infrastructure.JsonStore.Load<VoiceCommand>(path)!;
        Assert.Equal(5, back.Phrases.Count);
        Assert.Contains("افتح سي اس", back.Phrases);
        Assert.Equal("app.cs2", back.Actions[0].Parameters["app"]);
    }
}
