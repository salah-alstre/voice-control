using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Services;

namespace VoiceCommander.Core.Speech;

/// <summary>One downloadable whisper.cpp (ggml) model. Size and SHA-256 are the published values of the exact file.</summary>
public sealed record WhisperModelInfo(
    string Id, string Name, string FileName, string Url, long SizeBytes, string Sha256, bool Recommended);

/// <summary>A model operation failed in a way the user can act on. <see cref="Key"/> is a localization key.</summary>
public sealed class WhisperModelException : Exception
{
    public string Key { get; }
    public WhisperModelException(string key, string message, Exception? inner = null) : base(message, inner) => Key = key;
}

public interface IWhisperModelManager
{
    IReadOnlyList<WhisperModelInfo> Catalog { get; }
    event Action? Changed;

    /// <summary>The catalog model the user picked (default: Small).</summary>
    WhisperModelInfo Selected { get; }
    /// <summary>A user-chosen ggml file that overrides the catalog, or null.</summary>
    string? CustomPath { get; }

    /// <summary>The model file the engine should load, or null when no valid model is installed.</summary>
    string? ResolveModelPath();
    bool IsInstalled(WhisperModelInfo model);
    string InstallPath(WhisperModelInfo model);
    /// <summary>Where downloaded models live (outside the application folder).</summary>
    string ModelsDirectory { get; }

    /// <summary>Downloads (resuming a partial file when possible), validates and installs a model. Only ever started by the user. Progress is 0..1.</summary>
    /// <exception cref="WhisperModelException"/>
    Task DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Cheap structural check (exists, ggml header, catalog size). Returns a reason key or null.</summary>
    string? ValidateQuick(string path);
    /// <summary>Full check including the SHA-256 for catalog models. Returns a reason key or null.</summary>
    Task<string?> ValidateAsync(string path, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Uses an existing local ggml file instead of the catalog. Returns an error key or null.</summary>
    string? UseFile(string path);
    void ClearCustom();
    void Select(string modelId);
    void Remove(WhisperModelInfo model);
}

/// <summary>
/// Downloads and tracks whisper.cpp models under <c>%LOCALAPPDATA%\VoiceCommander\models\whisper</c>. A model is only
/// "installed" after it was downloaded in full, matched its published size and SHA-256 and was moved into place atomically;
/// interrupted downloads stay as <c>.part</c> files and are resumed.
/// </summary>
public sealed class WhisperModelManager : IWhisperModelManager
{
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private const long DiskMargin = 64L * 1024 * 1024;
    private static readonly byte[] GgmlMagic = { 0x6c, 0x6d, 0x67, 0x67 };   // "lmgg": little-endian 0x67676d6c
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static readonly IReadOnlyList<WhisperModelInfo> DefaultCatalog = new[]
    {
        new WhisperModelInfo("whisper-tiny", "Tiny (multilingual, 74 MB)", "ggml-tiny.bin", BaseUrl + "ggml-tiny.bin",
            77_691_713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21", false),
        new WhisperModelInfo("whisper-base", "Base (multilingual, 141 MB)", "ggml-base.bin", BaseUrl + "ggml-base.bin",
            147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", false),
        new WhisperModelInfo("whisper-small", "Small (multilingual, 465 MB)", "ggml-small.bin", BaseUrl + "ggml-small.bin",
            487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", true),
        new WhisperModelInfo("whisper-medium", "Medium (multilingual, 1.4 GB)", "ggml-medium.bin", BaseUrl + "ggml-medium.bin",
            1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", false),
    };

    /// <summary>Order tried when the selected model is not installed but another one is.</summary>
    private static readonly string[] FallbackOrder = { "whisper-small", "whisper-base", "whisper-medium", "whisper-tiny" };

    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<WhisperModelInfo> _catalog;
    private readonly string? _directoryOverride;
    private readonly HashSet<string> _active = new();

    public WhisperModelManager(ISettingsService settings, ILogService? log = null,
        HttpMessageHandler? handler = null, IReadOnlyList<WhisperModelInfo>? catalog = null, string? directory = null)
    {
        _settings = settings;
        _log = log ?? NullLogService.Instance;
        _http = handler == null ? SharedHttp : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _catalog = catalog ?? DefaultCatalog;
        _directoryOverride = directory;
    }

    public IReadOnlyList<WhisperModelInfo> Catalog => _catalog;
    public event Action? Changed;
    public string ModelsDirectory => _directoryOverride ?? AppPaths.WhisperModelsDirectory;

    public WhisperModelInfo Selected =>
        _catalog.FirstOrDefault(m => m.Id == _settings.Current.WhisperModelId)
        ?? _catalog.FirstOrDefault(m => m.Recommended) ?? _catalog[0];

    public string? CustomPath => string.IsNullOrWhiteSpace(_settings.Current.WhisperModelPath) ? null : _settings.Current.WhisperModelPath;

    public string InstallPath(WhisperModelInfo model) => Path.Combine(ModelsDirectory, model.FileName);

    public bool IsInstalled(WhisperModelInfo model)
    {
        var path = InstallPath(model);
        return File.Exists(path) && ValidateQuick(path) == null;
    }

    public string? ResolveModelPath()
    {
        var custom = CustomPath;
        if (custom != null && ValidateQuick(custom) == null) return custom;

        var selected = Selected;
        if (IsInstalled(selected)) return InstallPath(selected);
        foreach (var id in FallbackOrder)
        {
            var m = _catalog.FirstOrDefault(x => x.Id == id);
            if (m != null && IsInstalled(m)) return InstallPath(m);
        }
        return null;
    }

    // ---- validation --------------------------------------------------------------------------------------

    public string? ValidateQuick(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "model.file-missing";
            var info = new FileInfo(path);
            if (info.Length < 1024 * 1024) return "model.file-invalid";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[4];
                if (fs.Read(head, 0, 4) != 4 || !head.AsSpan().SequenceEqual(GgmlMagic)) return "model.file-invalid";
            }
            var known = ByFileName(path);
            if (known != null && info.Length != known.SizeBytes) return "model.size-mismatch";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(LogChannel.Speech, $"Could not read the Whisper model file: {ex.Message}");
            return "model.file-missing";
        }
    }

    public async Task<string?> ValidateAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var quick = ValidateQuick(path);
        if (quick != null) return quick;
        var known = ByFileName(path);
        if (known == null) return null;   // a custom file with an unknown name can only be checked structurally
        var hash = await HashFileAsync(path, progress, ct).ConfigureAwait(false);
        return string.Equals(hash, known.Sha256, StringComparison.OrdinalIgnoreCase) ? null : "model.corrupt";
    }

    private WhisperModelInfo? ByFileName(string path) =>
        _catalog.FirstOrDefault(m => string.Equals(m.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static async Task<string> HashFileAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var buffer = new byte[1 << 20];
        long done = 0, total = Math.Max(1, fs.Length);
        int read;
        while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            progress?.Report(done / (double)total);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // ---- selection ---------------------------------------------------------------------------------------

    public string? UseFile(string path)
    {
        var error = ValidateQuick(path);
        if (error != null) return error;
        _settings.Current.WhisperModelPath = path;
        _settings.Save();
        _log.Info(LogChannel.Speech, $"Using a local Whisper model file: {path}");
        Changed?.Invoke();
        return null;
    }

    public void ClearCustom()
    {
        if (_settings.Current.WhisperModelPath == null) return;
        _settings.Current.WhisperModelPath = null;
        _settings.Save();
        Changed?.Invoke();
    }

    public void Select(string modelId)
    {
        if (!_catalog.Any(m => m.Id == modelId) || _settings.Current.WhisperModelId == modelId) return;
        _settings.Current.WhisperModelId = modelId;
        _settings.Save();
        _log.Info(LogChannel.Speech, $"Whisper model selected: {modelId}");
        Changed?.Invoke();
    }

    public void Remove(WhisperModelInfo model)
    {
        try
        {
            TryDelete(InstallPath(model));
            TryDelete(InstallPath(model) + ".part");
        }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "Could not remove the Whisper model", ex); }
        Changed?.Invoke();
    }

    // ---- download ----------------------------------------------------------------------------------------

    public async Task DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken ct)
    {
        lock (_active)
            if (!_active.Add(model.Id)) throw new WhisperModelException("model.busy", "This model is already being downloaded.");
        try
        {
            await DownloadCoreAsync(model, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_active) _active.Remove(model.Id);
        }
    }

    private async Task DownloadCoreAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken ct)
    {
        try { Directory.CreateDirectory(ModelsDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WhisperModelException("model.disk-error", "The model folder could not be created: " + ex.Message, ex);
        }

        var target = InstallPath(model);
        var part = target + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing >= model.SizeBytes) { TryDelete(part); existing = 0; }   // a "partial" file that is not smaller is useless

        EnsureDiskSpace(model.SizeBytes - existing);
        _log.Info(LogChannel.Speech, $"Downloading Whisper model {model.Id} ({model.SizeBytes:N0} bytes) from {model.Url}" +
            (existing > 0 ? $", resuming at {existing:N0}" : ""));

        try
        {
            await FetchAsync(model, part, existing, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Info(LogChannel.Speech, "Whisper model download cancelled; the partial file is kept so it can be resumed");
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new WhisperModelException("model.network", "The download was interrupted: " + ex.Message, ex);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new WhisperModelException("model.disk-space", "There is not enough free disk space for the model.", ex);
        }
        catch (IOException ex)
        {
            throw new WhisperModelException("model.network", "The download was interrupted: " + ex.Message, ex);
        }

        // Verify before it can ever be treated as installed.
        var length = new FileInfo(part).Length;
        if (length != model.SizeBytes)
        {
            TryDelete(part);
            throw new WhisperModelException("model.corrupt", $"The downloaded file has the wrong size ({length:N0} instead of {model.SizeBytes:N0} bytes).");
        }
        // Forward synchronously: Progress<T> would post to the thread pool and could deliver a late 0.98 after the final 1.0.
        var hashProgress = progress == null ? null : new SyncProgress(p => progress.Report(0.9 + p * 0.1));
        var hash = await HashFileAsync(part, hashProgress, ct).ConfigureAwait(false);
        if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new WhisperModelException("model.corrupt", "The downloaded model failed its integrity check and was discarded.");
        }

        File.Move(part, target, overwrite: true);
        if (ValidateQuick(target) != null)
        {
            TryDelete(target);
            throw new WhisperModelException("model.corrupt", "The downloaded model is not a valid Whisper model.");
        }

        progress?.Report(1);
        _log.Info(LogChannel.Speech, $"Whisper model installed and verified: {target}");
        // First model wins the selection so "Install" immediately makes it the active one.
        if (_settings.Current.WhisperModelId != model.Id && CustomPath == null && ResolveModelPathExcluding(model) == null)
            _settings.Current.WhisperModelId = model.Id;
        _settings.Save();
        Changed?.Invoke();
    }

    private string? ResolveModelPathExcluding(WhisperModelInfo model) =>
        _catalog.Where(m => m.Id != model.Id && IsInstalled(m)).Select(InstallPath).FirstOrDefault();

    private async Task FetchAsync(WhisperModelInfo model, string part, long existing, IProgress<double>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && attempt == 0)
            {
                TryDelete(part); existing = 0;   // the server disagrees about the partial file: start over
                continue;
            }
            response.EnsureSuccessStatusCode();

            var resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!resumed) existing = 0;   // 200: the server ignored the range and sent everything

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            var done = existing;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                progress?.Report(Math.Min(0.9, done / (double)model.SizeBytes * 0.9));
            }
            return;
        }
    }

    private void EnsureDiskSpace(long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(ModelsDirectory));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed + DiskMargin)
                throw new WhisperModelException("model.disk-space",
                    $"Not enough free disk space: {needed / 1_048_576:N0} MB are needed, {free / 1_048_576:N0} MB are free.");
        }
        catch (WhisperModelException) { throw; }
        catch (Exception ex) { _log.Warn(LogChannel.Speech, "Could not check free disk space: " + ex.Message); }
    }

    private static bool IsDiskFull(IOException ex)
    {
        const int ErrorDiskFull = 0x70, ErrorHandleDiskFull = 0x27;
        var code = ex.HResult & 0xFFFF;
        return code is ErrorDiskFull or ErrorHandleDiskFull;
    }

    private static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
}
