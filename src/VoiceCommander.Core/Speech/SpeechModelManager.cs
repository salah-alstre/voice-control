using System.IO.Compression;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Services;

namespace VoiceCommander.Core.Speech;

public sealed record SpeechModelInfo(
    string Id, string Language, string Name, string Url, long SizeBytes, string FolderName, bool Recommended);

public interface ISpeechModelManager
{
    IReadOnlyList<SpeechModelInfo> Catalog { get; }
    event Action? Changed;

    /// <summary>The model folder to use for a language ("en"/"ar"), or null when none is available.</summary>
    string? ResolveModelPath(string language);
    bool IsInstalled(SpeechModelInfo model);
    string InstallPath(SpeechModelInfo model);

    /// <summary>Downloads and extracts a model (only ever started by the user). Progress is 0..1.</summary>
    Task DownloadAsync(SpeechModelInfo model, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Returns null when the folder is a usable Vosk model, otherwise a reason key.</summary>
    string? ValidateModelFolder(string path);
    /// <summary>Selects an existing local model folder for a language. Returns an error key or null.</summary>
    string? UseFolder(string language, string path);
    void Remove(SpeechModelInfo model);
}

public sealed class SpeechModelManager : ISpeechModelManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(60) };

    public static readonly IReadOnlyList<SpeechModelInfo> DefaultCatalog = new[]
    {
        new SpeechModelInfo("vosk-en-small", "en", "English (small, 39 MB)",
            "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip", 40_000_000, "vosk-model-small-en-us-0.15", true),
        new SpeechModelInfo("vosk-ar-mgb2", "ar", "Arabic (MGB2, 318 MB)",
            "https://alphacephei.com/vosk/models/vosk-model-ar-mgb2-0.4.zip", 333_000_000, "vosk-model-ar-mgb2-0.4", true),
        new SpeechModelInfo("vosk-ar-tn-small", "ar", "Arabic Tunisian (small, 158 MB)",
            "https://alphacephei.com/vosk/models/vosk-model-small-ar-tn-0.1-linto.zip", 166_000_000, "vosk-model-small-ar-tn-0.1-linto", false),
    };

    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public SpeechModelManager(ISettingsService settings, ILogService? log = null)
    {
        _settings = settings;
        _log = log ?? NullLogService.Instance;
    }

    public IReadOnlyList<SpeechModelInfo> Catalog => DefaultCatalog;
    public event Action? Changed;

    public string InstallPath(SpeechModelInfo model) => Path.Combine(AppPaths.ModelsDirectory, model.FolderName);

    public bool IsInstalled(SpeechModelInfo model) => VoskEngine.IsModelFolder(InstallPath(model));

    public string? ResolveModelPath(string language)
    {
        if (_settings.Current.ModelPaths.TryGetValue(language, out var custom) && VoskEngine.IsModelFolder(custom))
            return custom;
        // A model that can decode against the registered command phrases beats a bigger free-form-only one: the command
        // vocabulary is tiny and fixed, and free-form decoding is what turns "افتح" into "افتتاح". Size/quality only breaks ties.
        var installed = Catalog.Where(m => m.Language == language && IsInstalled(m))
            .OrderByDescending(m => VoskEngine.ModelSupportsGrammar(InstallPath(m)))
            .ThenByDescending(m => m.Recommended)
            .ToList();
        if (installed.Count > 0)
            _log.Info(LogChannel.Speech, $"Model for '{language}': {installed[0].FolderName} (grammar-capable: {VoskEngine.ModelSupportsGrammar(InstallPath(installed[0]))})");
        return installed.Count > 0 ? InstallPath(installed[0]) : null;
    }

    public string? ValidateModelFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return "model.folder-missing";
        if (!VoskEngine.IsModelFolder(path)) return "model.folder-invalid";
        return null;
    }

    public string? UseFolder(string language, string path)
    {
        var error = ValidateModelFolder(path);
        if (error != null) return error;
        _settings.Current.ModelPaths[language] = path;
        _settings.Save();
        _log.Info(LogChannel.Speech, $"Using local speech model for '{language}': {path}");
        Changed?.Invoke();
        return null;
    }

    public void Remove(SpeechModelInfo model)
    {
        var path = InstallPath(model);
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            if (_settings.Current.ModelPaths.TryGetValue(model.Language, out var p) &&
                string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                _settings.Current.ModelPaths.Remove(model.Language);
                _settings.Save();
            }
        }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "Could not remove the speech model", ex); }
        Changed?.Invoke();
    }

    public async Task DownloadAsync(SpeechModelInfo model, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.ModelsDirectory);
        var target = InstallPath(model);
        var part = Path.Combine(AppPaths.ModelsDirectory, model.FolderName + ".zip.part");
        var staging = Path.Combine(AppPaths.ModelsDirectory, model.FolderName + ".extracting");
        _log.Info(LogChannel.Speech, $"Downloading speech model {model.Id} from {model.Url}");
        try
        {
            using (var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? model.SizeBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(Math.Min(0.95, total > 0 ? done / (double)total * 0.95 : 0));
                }
            }

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await Task.Run(() => ExtractSafely(part, staging, ct), ct).ConfigureAwait(false);

            var root = FindModelRoot(staging) ?? throw new InvalidDataException("The downloaded archive does not contain a Vosk model.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(root, target);
            progress?.Report(1);
            _log.Info(LogChannel.Speech, $"Speech model installed: {target}");
            Changed?.Invoke();
        }
        finally
        {
            TryDelete(part);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>Extracts a zip, refusing any entry that would land outside the destination (zip-slip).</summary>
    internal static void ExtractSafely(string zipPath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var destRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!full.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The archive contains an unsafe path: " + entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(full); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, true);
        }
    }

    private static string? FindModelRoot(string dir)
    {
        if (VoskEngine.IsModelFolder(dir)) return dir;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var found = FindModelRoot(sub);
            if (found != null) return found;
        }
        return null;
    }

    private static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
}
