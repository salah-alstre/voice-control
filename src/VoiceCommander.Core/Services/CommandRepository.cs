using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Services;

public sealed class CommandStoreFile
{
    public int SeedVersion { get; set; }
    /// <summary>Built-in ids the user deleted; they are not re-seeded on upgrade.</summary>
    public List<string> DeletedBuiltIns { get; set; } = new();
    public List<VoiceCommand> Commands { get; set; } = new();
}

public interface ICommandRepository
{
    IReadOnlyList<VoiceCommand> Commands { get; }
    /// <summary>Enabled commands whose pack is not disabled: exactly what the matcher may consider.</summary>
    IReadOnlyList<VoiceCommand> ActiveCommands { get; }
    IReadOnlyList<CommandPack> Packs { get; }
    event Action? Changed;
    VoiceCommand? FindById(string? id);
    void AddOrUpdate(VoiceCommand command);
    bool Remove(string id);
    void ReplaceAll(IEnumerable<VoiceCommand> commands);
    bool IsPackEnabled(string packId);
    void SetPackEnabled(string packId, bool enabled);
    /// <summary>Restore all built-in commands to their shipped state.</summary>
    void RestoreBuiltIns();
    /// <summary>Validates a command; returns a list of localization keys describing problems (empty = valid).</summary>
    IReadOnlyList<string> Validate(VoiceCommand command);
}

public sealed class CommandRepository : ICommandRepository
{
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private CommandStoreFile _store;

    public CommandRepository(ILogService log, ISettingsService settings)
    {
        _log = log;
        _settings = settings;
        _store = JsonStore.Load<CommandStoreFile>(AppPaths.CommandsFile, log) ?? new CommandStoreFile();
        _store.Commands ??= new();
        _store.DeletedBuiltIns ??= new();
        Sanitize();
        if (_store.SeedVersion < BuiltInCommands.SeedVersion)
        {
            var existing = _store.Commands.Select(c => c.Id).ToHashSet();
            foreach (var c in BuiltInCommands.Create())
            {
                if (_store.DeletedBuiltIns.Contains(c.Id)) continue;
                if (!existing.Contains(c.Id)) { _store.Commands.Add(c); continue; }
                // Existing built-in: append shipped phrases it does not have yet (e.g. new Arabic aliases); user phrases are kept.
                var current = _store.Commands.First(x => x.Id == c.Id);
                if (!current.IsBuiltIn) continue;
                var have = current.Phrases.Select(TextNormalizer.Normalize).ToHashSet();
                foreach (var p in c.Phrases)
                    if (have.Add(TextNormalizer.Normalize(p))) current.Phrases.Add(p);
            }
            _store.SeedVersion = BuiltInCommands.SeedVersion;
            Persist();
        }
    }

    public IReadOnlyList<CommandPack> Packs => BuiltInCommands.Packs;
    public event Action? Changed;

    public IReadOnlyList<VoiceCommand> Commands { get { lock (_gate) return _store.Commands.ToList(); } }

    public IReadOnlyList<VoiceCommand> ActiveCommands
    {
        get
        {
            var disabled = _settings.Current.DisabledPacks;
            lock (_gate)
                return _store.Commands.Where(c => c.Enabled && (c.PackId == null || !disabled.Contains(c.PackId))).ToList();
        }
    }

    public VoiceCommand? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _store.Commands.FirstOrDefault(c => c.Id == id);
    }

    public void AddOrUpdate(VoiceCommand command)
    {
        lock (_gate)
        {
            var i = _store.Commands.FindIndex(c => c.Id == command.Id);
            if (i >= 0) _store.Commands[i] = command; else _store.Commands.Add(command);
            _store.DeletedBuiltIns.Remove(command.Id);
            Persist();
        }
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            var cmd = _store.Commands.FirstOrDefault(c => c.Id == id);
            removed = cmd != null && _store.Commands.Remove(cmd);
            if (removed)
            {
                if (cmd!.IsBuiltIn && !_store.DeletedBuiltIns.Contains(id)) _store.DeletedBuiltIns.Add(id);
                Persist();
            }
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void ReplaceAll(IEnumerable<VoiceCommand> commands)
    {
        lock (_gate)
        {
            _store.Commands = commands.ToList();
            Sanitize();
            Persist();
        }
        Changed?.Invoke();
    }

    public bool IsPackEnabled(string packId) => !_settings.Current.DisabledPacks.Contains(packId);

    public void SetPackEnabled(string packId, bool enabled)
    {
        var list = _settings.Current.DisabledPacks;
        if (enabled) list.RemoveAll(p => p == packId);
        else if (!list.Contains(packId)) list.Add(packId);
        _settings.Save();
        Changed?.Invoke();
    }

    public void RestoreBuiltIns()
    {
        lock (_gate)
        {
            var fresh = BuiltInCommands.Create();
            foreach (var f in fresh)
            {
                var i = _store.Commands.FindIndex(c => c.Id == f.Id);
                if (i >= 0) _store.Commands[i] = f; else _store.Commands.Add(f);
            }
            _store.DeletedBuiltIns.Clear();
            Persist();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Validate(VoiceCommand command) => CommandValidator.Validate(command);

    /// <summary>Drop structurally invalid entries (null lists, blank ids) so a hand-edited file cannot crash the pipeline.</summary>
    private void Sanitize()
    {
        _store.Commands.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Id));
        foreach (var c in _store.Commands)
        {
            c.Phrases ??= new();
            c.Actions ??= new();
            c.Phrases = c.Phrases.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            foreach (var a in c.Actions) a.Parameters ??= new();
        }
        var seen = new HashSet<string>();
        _store.Commands.RemoveAll(c => !seen.Add(c.Id));
    }

    private void Persist()
    {
        try { JsonStore.Save(AppPaths.CommandsFile, _store); }
        catch (Exception ex) { _log.Error(LogChannel.App, "Failed to save commands", ex); }
    }
}

public static class CommandValidator
{
    public static IReadOnlyList<string> Validate(VoiceCommand c)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.NameKey)) errors.Add("validation.name");
        if (c.Phrases.Count == 0 || c.Phrases.All(p => string.IsNullOrWhiteSpace(p) || TextNormalizer.Normalize(p).Length == 0)) errors.Add("validation.phrases");
        if (c.Actions.Count == 0) errors.Add("validation.actions");
        if (c.Actions.Any(a => string.IsNullOrWhiteSpace(a.Type))) errors.Add("validation.actiontype");
        foreach (var p in c.Phrases)
        {
            var opens = p.Count(ch => ch == '{'); var closes = p.Count(ch => ch == '}');
            if (opens != closes) { errors.Add("validation.braces"); break; }
        }
        return errors;
    }
}
