using Microsoft.Win32;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Windows;

public sealed record ResolvedLaunch(string FileName, string Arguments);

public interface IAppLocator
{
    /// <summary>Resolves a registry entry to something launchable, or null when the app cannot be found.</summary>
    ResolvedLaunch? Resolve(AppDefinition app);
}

public static class LaunchTarget
{
    private static readonly System.Text.RegularExpressions.Regex Scheme =
        new(@"^[A-Za-z][A-Za-z0-9+.\-]+:", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True for launcher/URI targets such as steam://rungameid/730, ms-settings: or shell:AppsFolder\...</summary>
    public static bool IsUri(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var t = target.Trim().Trim('"');
        return Scheme.IsMatch(t);     // a drive letter ("C:") is a single character and never matches
    }

    /// <summary>The scheme part ("steam" for steam://rungameid/730), or null.</summary>
    public static string? SchemeOf(string? target)
    {
        if (!IsUri(target)) return null;
        var t = target!.Trim().Trim('"');
        return t[..t.IndexOf(':')];
    }
}

/// <summary>
/// Turns an <see cref="AppDefinition"/> into a real launch target. Order: explicit full path, per-app special cases,
/// App Paths registry, well-known install folders, PATH, shell-resolvable name.
/// </summary>
public sealed class AppLocator : IAppLocator
{
    public ResolvedLaunch? Resolve(AppDefinition app)
    {
        var path = Environment.ExpandEnvironmentVariables(app.ExecutablePath ?? "").Trim('"');
        var args = app.Arguments ?? "";
        if (string.IsNullOrWhiteSpace(path)) return null;

        // Launcher / URI targets (steam://rungameid/730, ms-settings:) are handed to the shell as they are.
        if (LaunchTarget.IsUri(path)) return new ResolvedLaunch(path, args);

        if (Path.IsPathRooted(path))
            return File.Exists(path) ? new ResolvedLaunch(path, args) : null;

        // Discord (and other Squirrel apps) launch through Update.exe
        if (app.Id == "app.discord")
        {
            var upd = Path.Combine(Local, "Discord", "Update.exe");
            if (File.Exists(upd)) return new ResolvedLaunch(upd, "--processStart Discord.exe");
        }

        var exe = path;
        var reg = FromAppPaths(exe);
        if (reg != null) return new ResolvedLaunch(reg, args);

        foreach (var c in Candidates(app.Id, exe))
            if (File.Exists(c)) return new ResolvedLaunch(c, args);

        var onPath = FromPath(exe);
        if (onPath != null) return new ResolvedLaunch(onPath, args);

        // System shell names (notepad.exe, calc.exe, explorer.exe) resolve in System32/Windows.
        var sys = Path.Combine(Environment.SystemDirectory, exe);
        if (File.Exists(sys)) return new ResolvedLaunch(sys, args);
        var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
        if (File.Exists(win)) return new ResolvedLaunch(win, args);
        return null;
    }

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Pf => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string Pf86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static IEnumerable<string> Candidates(string id, string exe)
    {
        switch (id)
        {
            case "app.chrome":
                yield return Path.Combine(Pf, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(Pf86, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(Local, "Google", "Chrome", "Application", "chrome.exe");
                break;
            case "app.edge":
                yield return Path.Combine(Pf86, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(Pf, "Microsoft", "Edge", "Application", "msedge.exe");
                break;
            case "app.firefox":
                yield return Path.Combine(Pf, "Mozilla Firefox", "firefox.exe");
                yield return Path.Combine(Pf86, "Mozilla Firefox", "firefox.exe");
                break;
            case "app.spotify":
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
                break;
            case "app.steam":
                yield return Path.Combine(Pf86, "Steam", "steam.exe");
                yield return Path.Combine(Pf, "Steam", "steam.exe");
                break;
            case "app.vscode":
                yield return Path.Combine(Local, "Programs", "Microsoft VS Code", "Code.exe");
                yield return Path.Combine(Pf, "Microsoft VS Code", "Code.exe");
                break;
            case "app.vlc":
                yield return Path.Combine(Pf, "VideoLAN", "VLC", "vlc.exe");
                yield return Path.Combine(Pf86, "VideoLAN", "VLC", "vlc.exe");
                break;
            case "app.obs":
                yield return Path.Combine(Pf, "obs-studio", "bin", "64bit", "obs64.exe");
                break;
            case "app.discord":
                break;
        }
        // Generic: LocalAppData\<name>\<exe>
        var stem = Path.GetFileNameWithoutExtension(exe);
        yield return Path.Combine(Local, stem, exe);
        yield return Path.Combine(Local, "Programs", stem, exe);
    }

    private static string? FromAppPaths(string exe)
    {
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                var v = k?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    v = Environment.ExpandEnvironmentVariables(v).Trim('"');
                    if (File.Exists(v)) return v;
                }
            }
            catch { /* registry unavailable */ }
        }
        return null;
    }

    private static string? FromPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* invalid PATH entry */ }
        }
        return null;
    }
}
