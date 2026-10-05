using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Pipeline;

public class CommandExecutorTests
{
    private readonly MemorySettings _settings = new();
    private readonly MemoryHistory _history = new();
    private readonly FakeConfirm _confirm = new();
    private readonly Localizer _loc = new();

    private CommandExecutor Build(params FakeHandler[] handlers) =>
        new(new ActionHandlerRegistry(handlers), _settings, _history, _loc, _confirm, new NullLog());

    [Fact]
    public async Task Workflow_steps_run_in_the_order_they_are_defined()
    {
        var order = new List<string>();
        FakeHandler H(string type) => new(type, run: (_, _, _) => { order.Add(type); return Task.FromResult(ActionResult.Ok()); });
        var exec = Build(H("app.open"), H("wait"), H("volume.set"), H("notification"));

        var cmd = Make.Command("gaming", new[] { "start gaming" }, "app.open", "wait", "volume.set", "notification");
        var report = await exec.ExecuteAsync(cmd, null, null, TriggerSource.Test);

        Assert.True(report.Success);
        Assert.Equal(new[] { "app.open", "wait", "volume.set", "notification" }, order);
        Assert.Equal(4, report.StepsCompleted);
        Assert.Equal(4, report.StepsTotal);
    }

    [Fact]
    public async Task Step_parameters_number_and_app_reach_the_handler()
    {
        var h = new FakeHandler("volume.app.set");
        var exec = Build(h);
        var cmd = Make.Command("v", new[] { "x" });
        cmd.Actions = new List<ActionStep> { new("volume.app.set", ("level", "{number}")) };
        var app = Make.App("app.discord", "Discord");

        await exec.ExecuteAsync(cmd, 35, app, TriggerSource.Voice);

        Assert.Equal("{number}", h.Calls.Single().Get("level", ""));
        Assert.Equal(35, h.Contexts.Single().Number);
        Assert.Same(app, h.Contexts.Single().App);
        Assert.Equal(TriggerSource.Voice, h.Contexts.Single().Source);
    }

    [Fact]
    public async Task First_failing_step_stops_the_workflow()
    {
        var third = new FakeHandler("notification");
        var exec = Build(
            new FakeHandler("app.open"),
            new FakeHandler("wait", run: (_, _, _) => Task.FromResult(ActionResult.Fail("nope", new RecoveryAction(RecoveryKind.LocateApp, "app.x", "Locate")))),
            third);

        var report = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "app.open", "wait", "notification"), null, null, TriggerSource.Test);

        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Equal("nope", report.Message);
        Assert.Equal(1, report.StepsCompleted);
        Assert.Equal(3, report.StepsTotal);
        Assert.Equal(RecoveryKind.LocateApp, report.Recovery!.Kind);
        Assert.Empty(third.Calls);
    }

    [Fact]
    public async Task Handler_exception_is_reported_not_thrown()
    {
        var exec = Build(new FakeHandler("wait", run: (_, _, _) => throw new InvalidOperationException("boom")));
        var report = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait"), null, null, TriggerSource.Test);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Contains("boom", report.Message);
    }

    [Fact]
    public async Task Unknown_action_type_fails_before_anything_runs()
    {
        var first = new FakeHandler("wait");
        var exec = Build(first);
        var report = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait", "does.not.exist"), null, null, TriggerSource.Test);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Equal(0, report.StepsCompleted);
        Assert.Empty(first.Calls);
    }

    [Fact]
    public async Task Command_without_actions_fails()
    {
        var exec = Build();
        var cmd = Make.Command("c", new[] { "go" });
        cmd.Actions.Clear();
        var report = await exec.ExecuteAsync(cmd, null, null, TriggerSource.Test);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
    }

    [Theory]
    [InlineData("power.shutdown")]
    [InlineData("power.restart")]
    [InlineData("power.signout")]
    [InlineData("power.sleep")]
    public async Task Power_actions_are_blocked_by_default_and_never_reach_the_handler(string type)
    {
        var h = new FakeHandler(type, dangerous: true);
        var exec = Build(h);
        var report = await exec.ExecuteAsync(Make.Command("p", new[] { "x" }, type), null, null, TriggerSource.Voice);
        Assert.Equal(ExecutionOutcome.Blocked, report.Outcome);
        Assert.Empty(h.Calls);
        Assert.Equal(0, _confirm.Asked);
    }

    [Fact]
    public async Task Blocked_step_prevents_earlier_steps_from_running()
    {
        var safe = new FakeHandler("wait");
        var power = new FakeHandler("power.shutdown", dangerous: true);
        var exec = Build(safe, power);
        var report = await exec.ExecuteAsync(Make.Command("p", new[] { "x" }, "wait", "power.shutdown"), null, null, TriggerSource.Voice);
        Assert.Equal(ExecutionOutcome.Blocked, report.Outcome);
        Assert.Empty(safe.Calls);
    }

    [Fact]
    public async Task Enabled_dangerous_action_needs_confirmation_and_declining_cancels()
    {
        _settings.Current.DisableSleepCommands = false;
        var h = new FakeHandler("power.sleep", dangerous: true);
        var exec = Build(h);
        _confirm.Answer = false;

        var report = await exec.ExecuteAsync(Make.Command("s", new[] { "x" }, "power.sleep"), null, null, TriggerSource.Voice);

        Assert.Equal(ExecutionOutcome.Cancelled, report.Outcome);
        Assert.Equal(1, _confirm.Asked);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Confirming_a_dangerous_action_runs_it()
    {
        _settings.Current.DisableSleepCommands = false;
        var h = new FakeHandler("power.sleep", dangerous: true);
        var exec = Build(h);
        _confirm.Answer = true;

        var report = await exec.ExecuteAsync(Make.Command("s", new[] { "x" }, "power.sleep"), null, null, TriggerSource.Voice);

        Assert.True(report.Success);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task Confirmation_can_be_switched_off()
    {
        _settings.Current.DisableSleepCommands = false;
        _settings.Current.ConfirmDangerousActions = false;
        var h = new FakeHandler("power.sleep", dangerous: true);
        var report = await Build(h).ExecuteAsync(Make.Command("s", new[] { "x" }, "power.sleep"), null, null, TriggerSource.Voice);
        Assert.True(report.Success);
        Assert.Equal(0, _confirm.Asked);
    }

    [Fact]
    public async Task Every_run_is_recorded_in_history_with_recognized_text()
    {
        var exec = Build(new FakeHandler("wait"));
        await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait"), null, null, TriggerSource.Voice, "go now", 0.8);

        var e = Assert.Single(_history.Entries);
        Assert.Equal("c", e.CommandId);
        Assert.Equal("go now", e.RecognizedText);
        Assert.Equal(ExecutionOutcome.Success, e.Outcome);
        Assert.Equal(TriggerSource.Voice, e.Source);
        Assert.Equal(0.8, e.Confidence);
    }

    [Fact]
    public async Task Failed_and_blocked_runs_are_recorded_too()
    {
        var exec = Build(new FakeHandler("wait", run: (_, _, _) => Task.FromResult(ActionResult.Fail("bad"))));
        await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait"), null, null, TriggerSource.Test);
        await exec.ExecuteAsync(Make.Command("p", new[] { "x" }, "power.shutdown"), null, null, TriggerSource.Test);
        Assert.Equal(new[] { ExecutionOutcome.Failed, ExecutionOutcome.Failed }, _history.Entries.Select(e => e.Outcome));
    }

    [Fact]
    public async Task Single_action_returns_the_handlers_message_and_workflow_returns_summary()
    {
        var exec = Build(new FakeHandler("wait", run: (_, _, _) => Task.FromResult(ActionResult.Ok("hello"))));
        var one = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait"), null, null, TriggerSource.Test);
        Assert.Equal("hello", one.Message);
        var many = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait", "wait"), null, null, TriggerSource.Test);
        Assert.NotEqual("hello", many.Message);
        Assert.False(string.IsNullOrWhiteSpace(many.Message));
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var exec = Build(new FakeHandler("wait", run: async (_, _, ct) =>
        {
            cts.Cancel();
            await Task.Delay(5000, ct);
            return ActionResult.Ok();
        }));
        var report = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait"), null, null, TriggerSource.Test, ct: cts.Token);
        Assert.Equal(ExecutionOutcome.Cancelled, report.Outcome);
    }

    [Fact]
    public async Task Timeout_is_reported_as_failure()
    {
        _settings.Current.CommandTimeoutSeconds = 1;
        var exec = Build(new FakeHandler("wait", run: async (_, _, ct) =>
        {
            await Task.Delay(10_000, ct);
            return ActionResult.Ok();
        }));
        var report = await exec.ExecuteAsync(Make.Command("c", new[] { "go" }, "wait"), null, null, TriggerSource.Test);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
    }

    [Fact]
    public async Task Executions_are_serialized()
    {
        int running = 0, maxRunning = 0;
        var exec = Build(new FakeHandler("wait", run: async (_, _, _) =>
        {
            var n = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, n);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
            return ActionResult.Ok();
        }));
        var cmd = Make.Command("c", new[] { "go" }, "wait");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => exec.ExecuteAsync(cmd, null, null, TriggerSource.Test)));
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public void DisplayName_prefers_localized_key_then_name_then_id()
    {
        var c = new VoiceCommand { Id = "id1", Name = "My Name" };
        Assert.Equal("My Name", CommandExecutor.DisplayName(c, _loc));
        c.Name = "";
        Assert.Equal("id1", CommandExecutor.DisplayName(c, _loc));
        c.NameKey = "definitely.not.a.key";
        Assert.Equal("id1", CommandExecutor.DisplayName(c, _loc));
    }
}
