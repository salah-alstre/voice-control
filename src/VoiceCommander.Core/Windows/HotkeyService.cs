using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Windows;

public enum HotkeyKind { PushToTalk, ToggleListening, OpenApp }

/// <summary>
/// Empty = the hotkey is disabled (not an error). InUse for push-to-talk is only a warning: the low-level hook
/// still receives the key first, but another program may react to it as well.
/// </summary>
public enum HotkeyProblem { None, Empty, Invalid, Duplicate, InUse }

public sealed record HotkeyStatus(HotkeyKind Kind, string Text, bool Registered, HotkeyProblem Problem);

public interface IHotkeyService : IDisposable
{
    event Action? PushToTalkPressed;
    event Action? PushToTalkReleased;
    event Action? ToggleListeningPressed;
    event Action? OpenAppPressed;

    IReadOnlyList<HotkeyStatus> Status { get; }

    /// <summary>Replaces all hotkeys. Empty text disables that hotkey. Returns the per-hotkey outcome, including conflicts.</summary>
    Task<IReadOnlyList<HotkeyStatus>> ApplyAsync(string pushToTalk, string toggleListening, string openApp);

    /// <summary>Checks whether a hotkey text is valid and not taken by another program, without keeping it registered.</summary>
    Task<HotkeyProblem> CheckAsync(string text);
}

/// <summary>
/// Global hotkeys without needing a window: one dedicated thread owns a message loop, the RegisterHotKey
/// registrations (toggle/open) and the low-level keyboard hook (push-to-talk needs key-up, which RegisterHotKey lacks).
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private const int ToggleId = 1, OpenId = 2, ProbeId = 99;

    private readonly ILogService _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Native.HookProc _hookProc;
    private readonly Channel<bool> _pttEvents = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    private uint _threadId;
    private IntPtr _hook;
    private volatile bool _disposed;
    private volatile IReadOnlyList<HotkeyStatus> _status = Array.Empty<HotkeyStatus>();

    // Touched only from the message-loop thread.
    private uint _pttMods;
    private int _pttVk;
    private bool _pttActive;
    private readonly HashSet<int> _registeredIds = new();
    private (uint Mods, int Vk)? _toggle, _open;

    public event Action? PushToTalkPressed;
    public event Action? PushToTalkReleased;
    public event Action? ToggleListeningPressed;
    public event Action? OpenAppPressed;

    public HotkeyService(ILogService? log = null)
    {
        _log = log ?? NullLogService.Instance;
        _hookProc = HookCallback;
        _pump = Task.Run(PumpPttEventsAsync);
        _thread = new Thread(Loop) { IsBackground = true, Name = "VoiceCommander.Hotkeys" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<HotkeyStatus> Status => _status;

    public Task<IReadOnlyList<HotkeyStatus>> ApplyAsync(string pushToTalk, string toggleListening, string openApp) =>
        RunOnLoop(() => ApplyCore(pushToTalk, toggleListening, openApp));

    public Task<HotkeyProblem> CheckAsync(string text) => RunOnLoop(() => CheckCore(text));

    // ---------------------------------------------------------------- message loop

    private void Loop()
    {
        try
        {
            Native.PeekMessage(out _, IntPtr.Zero, Native.WM_APP, Native.WM_APP, Native.PM_NOREMOVE); // force queue creation
            _threadId = Native.GetCurrentThreadId();
        }
        finally { _ready.Set(); }

        try
        {
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == Native.WM_APP) DrainWork();
                else if (msg.message == Native.WM_HOTKEY) OnHotKey((int)msg.wParam);
            }
        }
        catch (Exception ex) { _log.Error(LogChannel.App, "Hotkey loop crashed", ex); }
        finally
        {
            DrainWork();
            Cleanup();
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { _log.Error(LogChannel.App, "Hotkey work failed", ex); }
        }
    }

    private Task<T> RunOnLoop<T>(Func<T> func)
    {
        if (_disposed || _threadId == 0) return Task.FromException<T>(new ObjectDisposedException(nameof(HotkeyService)));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!Native.PostThreadMessage(_threadId, Native.WM_APP, IntPtr.Zero, IntPtr.Zero))
            tcs.TrySetException(new InvalidOperationException("Hotkey thread is not running."));
        return tcs.Task;
    }

    private void OnHotKey(int id)
    {
        try
        {
            if (id == ToggleId) ToggleListeningPressed?.Invoke();
            else if (id == OpenId) OpenAppPressed?.Invoke();
        }
        catch (Exception ex) { _log.Error(LogChannel.App, "Hotkey handler failed", ex); }
    }

    // ---------------------------------------------------------------- apply / check

    private IReadOnlyList<HotkeyStatus> ApplyCore(string ptt, string toggle, string open)
    {
        ReleaseAll();

        var taken = new HashSet<(uint, int)>();
        var list = new List<HotkeyStatus>
        {
            Evaluate(HotkeyKind.PushToTalk, ptt, taken),
            Evaluate(HotkeyKind.ToggleListening, toggle, taken),
            Evaluate(HotkeyKind.OpenApp, open, taken),
        };
        _status = list;
        foreach (var s in list)
            _log.Info(LogChannel.App, $"Hotkey {s.Kind}: '{s.Text}' registered={s.Registered} problem={s.Problem}");
        return list;
    }

    private HotkeyStatus Evaluate(HotkeyKind kind, string text, HashSet<(uint, int)> taken)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return new HotkeyStatus(kind, text, false, HotkeyProblem.Empty);
        if (!KeyNames.TryParse(text, out var mods, out var vk)) return new HotkeyStatus(kind, text, false, HotkeyProblem.Invalid);
        mods &= 0xF;
        if (!taken.Add((mods, vk))) return new HotkeyStatus(kind, text, false, HotkeyProblem.Duplicate);

        if (kind == HotkeyKind.PushToTalk)
        {
            var conflict = !Probe(mods, vk);
            InstallPtt(mods, vk);
            if (_hook == IntPtr.Zero) return new HotkeyStatus(kind, text, false, HotkeyProblem.Invalid);
            return new HotkeyStatus(kind, text, true, conflict ? HotkeyProblem.InUse : HotkeyProblem.None);
        }

        var id = kind == HotkeyKind.ToggleListening ? ToggleId : OpenId;
        if (!Native.RegisterHotKey(IntPtr.Zero, id, mods | KeyNames.MOD_NOREPEAT, (uint)vk))
            return new HotkeyStatus(kind, text, false, HotkeyProblem.InUse);
        _registeredIds.Add(id);
        if (kind == HotkeyKind.ToggleListening) _toggle = (mods, vk); else _open = (mods, vk);
        return new HotkeyStatus(kind, text, true, HotkeyProblem.None);
    }

    private HotkeyProblem CheckCore(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return HotkeyProblem.Empty;
        if (!KeyNames.TryParse(text, out var mods, out var vk)) return HotkeyProblem.Invalid;
        mods &= 0xF;
        // Already ours? Then registering again would fail, which says nothing about other programs.
        if (_toggle == (mods, vk) || _open == (mods, vk) || (_pttVk == vk && _pttMods == mods)) return HotkeyProblem.None;
        return Probe(mods, vk) ? HotkeyProblem.None : HotkeyProblem.InUse;
    }

    /// <summary>True when nobody else owns the combination (registers it briefly and releases it again).</summary>
    private static bool Probe(uint mods, int vk)
    {
        if (!Native.RegisterHotKey(IntPtr.Zero, ProbeId, mods | KeyNames.MOD_NOREPEAT, (uint)vk)) return false;
        Native.UnregisterHotKey(IntPtr.Zero, ProbeId);
        return true;
    }

    private void ReleaseAll()
    {
        foreach (var id in _registeredIds) Native.UnregisterHotKey(IntPtr.Zero, id);
        _registeredIds.Clear();
        _toggle = _open = null;
        RemovePtt();
    }

    private void Cleanup()
    {
        ReleaseAll();
        _pttEvents.Writer.TryComplete();
    }

    // ---------------------------------------------------------------- push-to-talk hook

    private void InstallPtt(uint mods, int vk)
    {
        _pttMods = mods; _pttVk = vk; _pttActive = false;
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _pttVk = 0;
            _log.Error(LogChannel.App, "SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }
    }

    private void RemovePtt()
    {
        if (_pttActive) { _pttActive = false; _pttEvents.Writer.TryWrite(false); }
        _pttVk = 0; _pttMods = 0;
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private const uint LLKHF_INJECTED = 0x10;

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _pttVk != 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0) // never react to synthetic keys (our own SendInput)
            {
                var msg = (int)wParam;
                var down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                var up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                var vk = (int)k.vkCode;

                if (vk == _pttVk)
                {
                    if (down)
                    {
                        if (_pttActive) return (IntPtr)1; // swallow auto-repeat
                        if (ModifiersMatch())
                        {
                            _pttActive = true;
                            _pttEvents.Writer.TryWrite(true);
                            return (IntPtr)1;
                        }
                    }
                    else if (up && _pttActive)
                    {
                        _pttActive = false;
                        _pttEvents.Writer.TryWrite(false);
                        return (IntPtr)1;
                    }
                }
                else if (_pttActive && up && IsRequiredModifier(vk))
                {
                    _pttActive = false;
                    _pttEvents.Writer.TryWrite(false);
                }
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool ModifiersMatch()
    {
        var ctrl = IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3);
        var shift = IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1);
        var alt = IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5);
        var win = IsDown(0x5B) || IsDown(0x5C);
        return ctrl == ((_pttMods & KeyNames.MOD_CONTROL) != 0)
            && shift == ((_pttMods & KeyNames.MOD_SHIFT) != 0)
            && alt == ((_pttMods & KeyNames.MOD_ALT) != 0)
            && win == ((_pttMods & KeyNames.MOD_WIN) != 0);
    }

    private bool IsRequiredModifier(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => (_pttMods & KeyNames.MOD_SHIFT) != 0,
        0x11 or 0xA2 or 0xA3 => (_pttMods & KeyNames.MOD_CONTROL) != 0,
        0x12 or 0xA4 or 0xA5 => (_pttMods & KeyNames.MOD_ALT) != 0,
        0x5B or 0x5C => (_pttMods & KeyNames.MOD_WIN) != 0,
        _ => false,
    };

    /// <summary>Raises press/release in order, off the hook thread so a slow handler can never stall the keyboard.</summary>
    private async Task PumpPttEventsAsync()
    {
        try
        {
            await foreach (var pressed in _pttEvents.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { (pressed ? PushToTalkPressed : PushToTalkReleased)?.Invoke(); }
                catch (Exception ex) { _log.Error(LogChannel.App, "Push-to-talk handler failed", ex); }
            }
        }
        catch (Exception ex) { _log.Error(LogChannel.App, "Push-to-talk pump failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
