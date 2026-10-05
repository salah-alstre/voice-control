using VoiceCommander.Core.Pipeline;

namespace VoiceCommander.Tests.Pipeline;

public class CooldownGateTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private CooldownGate Gate() => new(() => _now);

    [Fact]
    public void First_call_is_allowed_and_repeat_within_window_is_blocked()
    {
        var g = Gate();
        Assert.True(g.TryEnter("a", 1500));
        _now = _now.AddMilliseconds(500);
        Assert.False(g.TryEnter("a", 1500));
    }

    [Fact]
    public void Allowed_again_once_window_has_elapsed()
    {
        var g = Gate();
        Assert.True(g.TryEnter("a", 1500));
        _now = _now.AddMilliseconds(1500);
        Assert.True(g.TryEnter("a", 1500));
    }

    [Fact]
    public void Blocked_attempt_does_not_extend_the_window()
    {
        var g = Gate();
        Assert.True(g.TryEnter("a", 1000));
        _now = _now.AddMilliseconds(600);
        Assert.False(g.TryEnter("a", 1000));
        _now = _now.AddMilliseconds(500); // 1100ms after the first
        Assert.True(g.TryEnter("a", 1000));
    }

    [Fact]
    public void Cooldown_is_tracked_per_command()
    {
        var g = Gate();
        Assert.True(g.TryEnter("a", 1500));
        Assert.True(g.TryEnter("b", 1500));
        Assert.False(g.TryEnter("a", 1500));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Zero_cooldown_disables_the_gate(int ms)
    {
        var g = Gate();
        Assert.True(g.TryEnter("a", ms));
        Assert.True(g.TryEnter("a", ms));
    }

    [Fact]
    public void Reset_clears_state()
    {
        var g = Gate();
        Assert.True(g.TryEnter("a", 1500));
        g.Reset();
        Assert.True(g.TryEnter("a", 1500));
    }
}
