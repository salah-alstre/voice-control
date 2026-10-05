using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VoiceCommander.Core.Models;

namespace VoiceCommander.App.Infrastructure;

/// <summary>Swaps the colour dictionary (always the first merged dictionary) and tells the title bars about it.</summary>
public static class ThemeService
{
    private static bool _dark = true;
    public static bool IsDark => _dark;

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch { return true; }
    }

    public static void Apply(AppTheme theme)
    {
        _dark = theme switch { AppTheme.Dark => true, AppTheme.Light => false, _ => SystemUsesDark() };
        var app = System.Windows.Application.Current;
        var dict = new ResourceDictionary
        {
            Source = new Uri(_dark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml")
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);
        foreach (Window w in app.Windows) ApplyTitleBar(w);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark/light native title bar (Windows 10 20H1+ / 11). Harmless where unsupported.</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = _dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            // Windows 11 22000+: paint the caption and the window border true black (COLORREF 0x00BBGGRR) so no grey chrome remains.
            // Light theme restores the system defaults (0xFFFFFFFF = DWMWA_COLOR_DEFAULT).
            int caption = _dark ? 0x000000 : unchecked((int)0xFFFFFFFF);
            int text = _dark ? 0xFFFFFF : unchecked((int)0xFFFFFFFF);
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(hwnd, 34, ref caption, sizeof(int));   // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));      // DWMWA_TEXT_COLOR
        }
        catch { }
    }
}
