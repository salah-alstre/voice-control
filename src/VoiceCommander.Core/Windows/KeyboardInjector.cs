using System.Runtime.InteropServices;

namespace VoiceCommander.Core.Windows;

/// <summary>Key-name parsing shared by hotkey registration and the "keyboard shortcut" action.</summary>
public static class KeyNames
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private static readonly Dictionary<string, int> Map = BuildMap();

    private static Dictionary<string, int> BuildMap()
    {
        var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
            ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
            ["medianext"] = 0xB0, ["mediaprevious"] = 0xB1, ["mediaprev"] = 0xB1, ["mediastop"] = 0xB2, ["mediaplaypause"] = 0xB3,
            ["volumemute"] = 0xAD, ["volumedown"] = 0xAE, ["volumeup"] = 0xAF,
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B,
        };
        for (int i = 1; i <= 24; i++) m["f" + i] = 0x6F + i;
        for (char c = 'a'; c <= 'z'; c++) m[c.ToString()] = c - 'a' + 0x41;
        for (char c = '0'; c <= '9'; c++) m[c.ToString()] = c;
        return m;
    }

    public static bool IsModifier(string token) =>
        token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("control", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("shift", StringComparison.OrdinalIgnoreCase) || token.Equals("alt", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("win", StringComparison.OrdinalIgnoreCase) || token.Equals("windows", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetVk(string token, out int vk) => Map.TryGetValue(token.Trim(), out vk);

    /// <summary>Parses "Ctrl+Shift+V". Returns false for unknown keys or a missing main key.</summary>
    public static bool TryParse(string? text, out uint modifiers, out int vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "win": case "windows": modifiers |= MOD_WIN; break;
                default:
                    if (vk != 0 || !TryGetVk(raw, out vk)) return false;
                    break;
            }
        }
        return vk != 0;
    }

    public static string Format(uint modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        var name = Map.Where(kv => kv.Value == vk && kv.Key.Length > 1 && !IsModifier(kv.Key)).Select(kv => kv.Key).FirstOrDefault()
                   ?? Map.Where(kv => kv.Value == vk && !IsModifier(kv.Key)).Select(kv => kv.Key).FirstOrDefault() ?? "0x" + vk.ToString("X");
        parts.Add(name.Length == 1 ? name.ToUpperInvariant() : char.ToUpperInvariant(name[0]) + name[1..]);
        return string.Join("+", parts);
    }
}

public static class KeyboardInjector
{
    private static Native.INPUT Key(int vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)vk, dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0) } },
    };

    private static bool IsExtended(int vk) => vk is 0x5B or 0x5C or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E;

    /// <summary>Presses the chord (modifiers down, key down, key up, modifiers up). Throws FormatException for unknown keys.</summary>
    public static void SendChord(string chord)
    {
        var tokens = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new FormatException("Empty shortcut");
        var vks = new List<int>();
        foreach (var t in tokens)
        {
            if (!KeyNames.TryGetVk(t, out var vk)) throw new FormatException($"Unknown key '{t}'");
            vks.Add(vk);
        }
        var inputs = new List<Native.INPUT>();
        foreach (var vk in vks) inputs.Add(Key(vk, false));
        for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(Key(vks[i], true));
        var arr = inputs.ToArray();
        if (Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Native.INPUT>()) != arr.Length)
            throw new InvalidOperationException("Windows rejected the keyboard input (is the target window elevated?)");
    }

    public static void TapAlt()
    {
        var arr = new[] { Key(0x12, false), Key(0x12, true) };
        Native.SendInput(2, arr, Marshal.SizeOf<Native.INPUT>());
    }
}
