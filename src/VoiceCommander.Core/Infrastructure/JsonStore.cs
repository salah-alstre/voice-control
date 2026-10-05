using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceCommander.Core.Infrastructure;

/// <summary>Reliable JSON persistence: atomic writes, corrupt-file quarantine instead of crashing.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Single-line variant for JSONL files.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    private static readonly object WriteLock = new();

    /// <summary>Loads a file; returns null when missing. A corrupt file is moved aside (.corrupt-timestamp) and null returned.</summary>
    public static T? Load<T>(string path, ILogService? log = null) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            log?.Error(LogChannel.App, $"Could not read {Path.GetFileName(path)}; moving it aside.", ex);
            try { File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { /* best effort */ }
            return null;
        }
    }

    public static void Save<T>(string path, T value)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, true);
        }
    }
}
