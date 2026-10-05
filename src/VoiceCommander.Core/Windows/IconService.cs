using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Windows;

public interface IIconService
{
    /// <summary>Returns a cached PNG for the application's icon, extracting it on first use. Null when there is none.</summary>
    string? GetIconPath(AppDefinition app);
}

public sealed class IconService : IIconService
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" };

    private readonly IAppLocator _locator;
    private readonly ILogService _log;
    private readonly object _gate = new();
    // Resolved paths are remembered for the session so a list of apps never hits disk/registry twice per app.
    private readonly Dictionary<string, string?> _memo = new();

    public IconService(IAppLocator locator, ILogService? log = null)
    {
        _locator = locator; _log = log ?? NullLogService.Instance;
    }

    public string? GetIconPath(AppDefinition app)
    {
        var memoKey = $"{app.Id}|{app.ExecutablePath}|{app.IconPath}";
        lock (_gate)
        {
            if (_memo.TryGetValue(memoKey, out var cached) && (cached == null || File.Exists(cached))) return cached;
        }
        var result = Resolve(app);
        lock (_gate) _memo[memoKey] = result;
        return result;
    }

    private string? Resolve(AppDefinition app)
    {
        try
        {
            // 1. An explicitly chosen icon (image file or an exe/dll/ico to extract from).
            var custom = Environment.ExpandEnvironmentVariables(app.IconPath ?? "").Trim('"');
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            {
                var ext = Path.GetExtension(custom).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif") return custom;
                var fromCustom = ExtractToCache(custom);
                if (fromCustom != null) return fromCustom;
            }

            // 2. The executable's own icon.
            var exe = FindExecutable(app);
            if (exe != null) return ExtractToCache(exe);

            // 3. URI targets: use the icon of the program that handles the scheme (steam:// -> steam.exe).
            if (LaunchTarget.IsUri(app.ExecutablePath))
            {
                var handler = FindSchemeHandler(LaunchTarget.SchemeOf(app.ExecutablePath));
                if (handler != null) return ExtractToCache(handler);
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn(LogChannel.App, $"Icon extraction failed for {app.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Extracts the associated icon once and stores it as a PNG; later calls only check that the file exists.</summary>
    private string? ExtractToCache(string file)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant())))[..16];
        var png = Path.Combine(AppPaths.IconsDirectory, key + ".png");
        lock (_gate)
        {
            if (File.Exists(png)) return png;
            Directory.CreateDirectory(AppPaths.IconsDirectory);
            using var icon = Icon.ExtractAssociatedIcon(file);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            bmp.Save(png, ImageFormat.Png);
            return png;
        }
    }

    private string? FindExecutable(AppDefinition app)
    {
        if (LaunchTarget.IsUri(app.ExecutablePath)) return null;
        if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && Path.IsPathRooted(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            return app.ExecutablePath;
        var r = _locator.Resolve(app);
        return r != null && Path.IsPathRooted(r.FileName) && File.Exists(r.FileName) ? r.FileName : null;
    }

    /// <summary>Reads HKCR\&lt;scheme&gt;\shell\open\command and returns the executable it names.</summary>
    private static string? FindSchemeHandler(string? scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme)) return null;
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
            var cmd = k?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = cmd.Trim();
            string exe;
            if (cmd.StartsWith('"'))
            {
                var end = cmd.IndexOf('"', 1);
                if (end < 0) return null;
                exe = cmd[1..end];
            }
            else
            {
                var idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                exe = cmd[..(idx + 4)];
            }
            exe = Environment.ExpandEnvironmentVariables(exe);
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }
}
