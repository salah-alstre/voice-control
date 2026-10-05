using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    event Action? Changed;
    void Save();
    void Replace(AppSettings settings);
}

public sealed class SettingsService : ISettingsService
{
    private readonly ILogService _log;

    public SettingsService(ILogService log)
    {
        _log = log;
        Current = JsonStore.Load<AppSettings>(AppPaths.SettingsFile, log) ?? new AppSettings();
        Sanitize();
    }

    public AppSettings Current { get; private set; }
    public event Action? Changed;

    public void Save()
    {
        Sanitize();
        try { JsonStore.Save(AppPaths.SettingsFile, Current); }
        catch (Exception ex) { _log.Error(LogChannel.App, "Failed to save settings", ex); }
        Changed?.Invoke();
    }

    public void Replace(AppSettings settings)
    {
        Current = settings;
        Save();
    }

    /// <summary>Clamp hand-edited values so a bad settings.json can never put the app in a broken state.</summary>
    private void Sanitize()
    {
        var s = Current;
        s.ConfidenceThreshold = Math.Clamp(s.ConfidenceThreshold, 0, 1);
        s.SimilarityTolerance = Math.Clamp(s.SimilarityTolerance, 0, 0.5);
        s.CommandCooldownMs = Math.Clamp(s.CommandCooldownMs, 0, 30000);
        s.CommandTimeoutSeconds = Math.Clamp(s.CommandTimeoutSeconds, 1, 3600);
        s.PttSoundVolume = Math.Clamp(double.IsNaN(s.PttSoundVolume) ? 0.5 : s.PttSoundVolume, 0, 1);
        s.AssistantOpacity = Math.Clamp(double.IsNaN(s.AssistantOpacity) ? 1 : s.AssistantOpacity, 0.3, 1);
        s.AssistantHideAfterSeconds = Math.Clamp(s.AssistantHideAfterSeconds, 2, 600);
        s.ModelPaths ??= new();
        s.DisabledPacks ??= new();
        s.EngineByLanguage ??= new();
        foreach (var key in s.EngineByLanguage.Where(kv => !AppSettings.KnownEngines.Contains(kv.Value ?? "", StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            s.EngineByLanguage.Remove(key);
        if (string.IsNullOrWhiteSpace(s.WhisperModelId)) s.WhisperModelId = "whisper-small";
        if (string.IsNullOrWhiteSpace(s.WhisperModelPath)) s.WhisperModelPath = null;
        if (string.IsNullOrWhiteSpace(s.Language)) s.Language = "en";
    }
}
