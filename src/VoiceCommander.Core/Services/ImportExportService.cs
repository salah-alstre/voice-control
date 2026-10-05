using System.Text.Json;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Services;

/// <summary>The on-disk shape of a <c>.voicecommander</c> file.</summary>
public sealed class ExchangeFile
{
    public const string FormatName = "voicecommander";
    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public List<VoiceCommand> Commands { get; set; } = new();
    public List<AppDefinition> Applications { get; set; } = new();
}

/// <summary>What a file contains after validation, before anything is changed.</summary>
public sealed class ImportPreview
{
    public List<VoiceCommand> Commands { get; } = new();
    public List<AppDefinition> Applications { get; } = new();
    /// <summary>Number of commands/apps in the file that were dropped because they are invalid.</summary>
    public int Skipped { get; set; }
}

public enum ImportMode { Merge, Replace }

public sealed record ImportResult(int Commands, int Applications, int Skipped);

public interface IImportExportService
{
    void Export(string path, bool commands = true, bool applications = true);
    /// <summary>Reads and validates a file. Throws <see cref="InvalidDataException"/> when it is not a Voice Commander file.</summary>
    ImportPreview Preview(string path);
    ImportResult Apply(ImportPreview preview, ImportMode mode);
}

public sealed class ImportExportService : IImportExportService
{
    private readonly ICommandRepository _commands;
    private readonly IAppRegistry _apps;
    private readonly ILogService _log;

    public ImportExportService(ICommandRepository commands, IAppRegistry apps, ILogService log)
    {
        _commands = commands;
        _apps = apps;
        _log = log;
    }

    public void Export(string path, bool commands = true, bool applications = true)
    {
        var file = new ExchangeFile();
        if (commands) file.Commands = _commands.Commands.Select(c => c.Clone()).ToList();
        if (applications) file.Applications = _apps.Apps.Select(a => a.Clone()).ToList();
        JsonStore.Save(path, file);
        _log.Info(LogChannel.App, $"Exported {file.Commands.Count} commands and {file.Applications.Count} apps to {path}");
    }

    public ImportPreview Preview(string path)
    {
        ExchangeFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExchangeFile>(File.ReadAllText(path), JsonStore.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Not a readable Voice Commander file.", ex);
        }
        if (file == null || !string.Equals(file.Format, ExchangeFile.FormatName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not a Voice Commander file.");

        var preview = new ImportPreview();
        foreach (var c in file.Commands ?? new())
        {
            if (c == null) { preview.Skipped++; continue; }
            c.Phrases ??= new();
            c.Actions ??= new();
            c.Actions.RemoveAll(a => a == null);
            foreach (var a in c.Actions) a.Parameters ??= new();
            if (string.IsNullOrWhiteSpace(c.Id)) c.Id = Guid.NewGuid().ToString("N");
            if (_commands.Validate(c).Count > 0) { preview.Skipped++; continue; }
            preview.Commands.Add(c);
        }
        foreach (var a in file.Applications ?? new())
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.ExecutablePath)) { preview.Skipped++; continue; }
            if (string.IsNullOrWhiteSpace(a.Id)) a.Id = Guid.NewGuid().ToString("N");
            a.Aliases ??= new();
            preview.Applications.Add(a);
        }
        return preview;
    }

    public ImportResult Apply(ImportPreview preview, ImportMode mode)
    {
        if (mode == ImportMode.Replace)
        {
            if (preview.Commands.Count > 0) _commands.ReplaceAll(preview.Commands);
            if (preview.Applications.Count > 0) _apps.ReplaceAll(preview.Applications);
        }
        else
        {
            foreach (var c in preview.Commands) _commands.AddOrUpdate(c);
            foreach (var a in preview.Applications) _apps.AddOrUpdate(a);
        }
        _log.Info(LogChannel.App, $"Imported {preview.Commands.Count} commands, {preview.Applications.Count} apps ({mode}), skipped {preview.Skipped}");
        return new ImportResult(preview.Commands.Count, preview.Applications.Count, preview.Skipped);
    }
}
