using System.Text.Json;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Services;

public interface IHistoryService
{
    IReadOnlyList<HistoryEntry> Entries { get; }
    event Action<HistoryEntry>? Added;
    event Action? Cleared;
    void Add(HistoryEntry entry);
    void Clear();
}

/// <summary>Local command history (text only, never audio). Append-only JSON lines, capped at 500 entries in memory; the file is compacted when it grows past twice that.</summary>
public sealed class HistoryService : IHistoryService
{
    private const int MaxEntries = 500;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = new();
    private int _linesOnDisk;

    public HistoryService(ILogService log)
    {
        _log = log;
        Load();
    }

    public IReadOnlyList<HistoryEntry> Entries { get { lock (_gate) return _entries.ToList(); } }
    public event Action<HistoryEntry>? Added;
    public event Action? Cleared;

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            try
            {
                AppPaths.EnsureCreated();
                File.AppendAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(entry, JsonStore.Compact) + Environment.NewLine);
            }
            catch (Exception ex) { _log.Warn(LogChannel.App, "History write failed: " + ex.Message); }
            _linesOnDisk++;
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            if (_linesOnDisk > MaxEntries * 2) { Rewrite(_entries); _linesOnDisk = _entries.Count; }
        }
        Added?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            try { if (File.Exists(AppPaths.HistoryFile)) File.Delete(AppPaths.HistoryFile); } catch { }
        }
        Cleared?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile)) return;
            foreach (var line in File.ReadLines(AppPaths.HistoryFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _linesOnDisk++;
                try
                {
                    var e = JsonSerializer.Deserialize<HistoryEntry>(line, JsonStore.Options);
                    if (e != null) _entries.Add(e);
                }
                catch (JsonException) { /* skip a damaged line */ }
            }
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
                Rewrite(_entries);
                _linesOnDisk = _entries.Count;
            }
        }
        catch (Exception ex) { _log.Warn(LogChannel.App, "History load failed: " + ex.Message); }
    }

    private void Rewrite(List<HistoryEntry> keep)
    {
        try
        {
            File.WriteAllLines(AppPaths.HistoryFile, keep.Select(e => JsonSerializer.Serialize(e, JsonStore.Compact)));
            if (!ReferenceEquals(keep, _entries)) { _entries.Clear(); _entries.AddRange(keep); }
        }
        catch (Exception ex) { _log.Warn(LogChannel.App, "History rewrite failed: " + ex.Message); }
    }
}
