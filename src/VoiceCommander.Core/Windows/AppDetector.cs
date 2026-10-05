using System.Runtime.InteropServices;
using Microsoft.Win32;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Windows;

public sealed record DetectedApp(string Name, string ExecutablePath, string ProcessName);

public interface IAppDetector
{
    /// <summary>Finds installed desktop apps from the Start Menu shortcuts and the App Paths registry. Read-only.</summary>
    Task<IReadOnlyList<DetectedApp>> DetectAsync(CancellationToken ct = default);
}

public sealed class AppDetector : IAppDetector
{
    private static readonly string[] Skip = { "uninstall", "readme", "help", "documentation", "license", "release notes", "website", "manual", "setup", "installer", "update" };
    private readonly ILogService _log;

    public AppDetector(ILogService? log = null) => _log = log ?? NullLogService.Instance;

    public Task<IReadOnlyList<DetectedApp>> DetectAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DetectedApp>>(() => Detect(ct), ct);

    private List<DetectedApp> Detect(CancellationToken ct)
    {
        var byPath = new Dictionary<string, DetectedApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, target) in ReadStartMenu(ct))
            AddIfNew(byPath, name, target);
        foreach (var (name, target) in ReadAppPaths(ct))
            AddIfNew(byPath, name, target);

        return byPath.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddIfNew(Dictionary<string, DetectedApp> map, string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
        var lower = name.ToLowerInvariant();
        if (Skip.Any(lower.Contains)) return;
        if (map.ContainsKey(path)) return;
        map[path] = new DetectedApp(name.Trim(), path, Path.GetFileNameWithoutExtension(path));
    }

    private IEnumerable<(string Name, string Target)> ReadStartMenu(CancellationToken ct)
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };
        object? shell = null;
        var results = new List<(string, string)>();
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return results;
            shell = Activator.CreateInstance(t);
            foreach (var dir in dirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
                catch (Exception ex) { _log.Warn(LogChannel.App, $"Start menu scan failed for {dir}: {ex.Message}"); continue; }
                foreach (var lnk in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = ResolveShortcut(shell!, lnk);
                    if (!string.IsNullOrEmpty(target)) results.Add((Path.GetFileNameWithoutExtension(lnk), target));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn(LogChannel.App, "Shortcut scan failed: " + ex.Message); }
        finally { if (shell != null) Marshal.ReleaseComObject(shell); }
        return results;
    }

    private static string? ResolveShortcut(object shell, string lnkPath)
    {
        object? sc = null;
        try
        {
            dynamic d = shell;
            sc = d.CreateShortcut(lnkPath);
            string target = ((dynamic)sc).TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        catch { return null; }
        finally { if (sc != null) Marshal.ReleaseComObject(sc); }
    }

    private IEnumerable<(string Name, string Target)> ReadAppPaths(CancellationToken ct)
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var results = new List<(string, string)>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var root = hive.OpenSubKey(key);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var k = root.OpenSubKey(sub);
                    var path = (k?.GetValue(null) as string)?.Trim('"');
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    string? desc = null;
                    try { desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
                    results.Add((string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(sub) : desc!, path));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Warn(LogChannel.App, "App Paths scan failed: " + ex.Message); }
        }
        return results;
    }
}
