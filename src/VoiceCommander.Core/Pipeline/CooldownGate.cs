namespace VoiceCommander.Core.Pipeline;

/// <summary>
/// Debounce for voice-triggered commands: the same command id cannot fire again within the cooldown window.
/// The clock is injectable so tests are deterministic.
/// </summary>
public sealed class CooldownGate
{
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, DateTime> _last = new();
    private readonly object _lock = new();

    public CooldownGate(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>True (and records the firing) when allowed; false while still cooling down.</summary>
    public bool TryEnter(string commandId, int cooldownMs)
    {
        if (cooldownMs <= 0) return true;
        lock (_lock)
        {
            var now = _now();
            if (_last.TryGetValue(commandId, out var t) && (now - t).TotalMilliseconds < cooldownMs)
                return false;
            _last[commandId] = now;
            return true;
        }
    }

    public void Reset() { lock (_lock) _last.Clear(); }
}
