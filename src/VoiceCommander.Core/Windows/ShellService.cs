using System.Diagnostics;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Windows;

public interface IShellService
{
    /// <summary>Opens a folder path or a shell: location in Explorer. Returns an error message or null on success.</summary>
    string? OpenFolder(string path);
    /// <summary>Only http/https URLs are opened.</summary>
    string? OpenUrl(string url);
    /// <summary>Only ms-settings: URIs are opened.</summary>
    string? OpenSettings(string uri);
    void Lock();
    void ShowDesktop();
    void Shutdown();
    void Restart();
    void SignOut();
    void Sleep();
}

/// <summary>Fixed, hard-coded system operations. No recognised text is ever passed to a shell.</summary>
public sealed class ShellService : IShellService
{
    private readonly ILogService _log;
    public ShellService(ILogService log) => _log = log;

    public string? OpenFolder(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path ?? "").Trim().Trim('"');
        if (path.Length == 0) return "No folder specified.";
        if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(path))
            return $"Folder not found: {path}";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"\"{path}\"" })?.Dispose();
            _log.Info(LogChannel.Command, "Opened folder " + path);
            return null;
        }
        catch (Exception ex) { _log.Error(LogChannel.Command, "OpenFolder failed", ex); return ex.Message; }
    }

    public string? OpenUrl(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Only http:// and https:// addresses can be opened.";
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            _log.Info(LogChannel.Command, "Opened URL " + uri.Host);
            return null;
        }
        catch (Exception ex) { _log.Error(LogChannel.Command, "OpenUrl failed", ex); return ex.Message; }
    }

    public string? OpenSettings(string uri)
    {
        uri = (uri ?? "").Trim();
        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return "Not a Windows settings address.";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) { _log.Error(LogChannel.Command, "OpenSettings failed", ex); return ex.Message; }
    }

    public void Lock()
    {
        if (!Native.LockWorkStation()) throw new InvalidOperationException("Windows refused to lock the workstation.");
    }

    public void ShowDesktop() => KeyboardInjector.SendChord("Win+D");

    private void RunShutdown(string arg)
    {
        _log.Warn(LogChannel.Command, "Power action: shutdown.exe " + arg);
        Process.Start(new ProcessStartInfo("shutdown.exe", arg) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    public void Shutdown() => RunShutdown("/s /t 0");
    public void Restart() => RunShutdown("/r /t 0");
    public void SignOut() => RunShutdown("/l");

    public void Sleep()
    {
        _log.Warn(LogChannel.Command, "Power action: sleep");
        Native.SetSuspendState(false, false, false);
    }
}
