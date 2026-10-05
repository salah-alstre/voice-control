using Microsoft.Win32;

namespace VoiceCommander.Core.Windows;

public interface IStartupService
{
    bool IsEnabled { get; }
    /// <summary>Adds or removes the per-user "start with Windows" entry. Returns false when the registry write failed.</summary>
    bool SetEnabled(bool enabled, bool startMinimized = true);
}

/// <summary>Start with Windows via HKCU\...\Run (per user, no admin rights needed).</summary>
public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VoiceCommander";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public bool SetEnabled(bool enabled, bool startMinimized = true)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                k.SetValue(ValueName, $"\"{exe}\"" + (startMinimized ? " --minimized" : ""));
            }
            else k.DeleteValue(ValueName, false);
            return true;
        }
        catch { return false; }
    }
}
