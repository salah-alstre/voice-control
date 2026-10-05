namespace VoiceCommander.Core.Infrastructure;

/// <summary>All on-disk locations. User data lives in %AppData%\VoiceCommander so it survives app updates.</summary>
public static class AppPaths
{
    private static string? _root;

    /// <summary>Overridable for tests and portable mode.</summary>
    public static string Root
    {
        get => _root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceCommander");
        set => _root = value;
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ApplicationsFile => Path.Combine(Root, "applications.json");
    public static string CommandsFile => Path.Combine(Root, "commands.json");
    public static string HistoryFile => Path.Combine(Root, "history.jsonl");
    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string ModelsDirectory => Path.Combine(Root, "models");
    /// <summary>Whisper ggml models (single .bin files). Always outside the install/project folder.</summary>
    public static string WhisperModelsDirectory => Path.Combine(ModelsDirectory, "whisper");
    public static string IconsDirectory => Path.Combine(Root, "icons");
    public static string BackupsDirectory => Path.Combine(Root, "backups");

    public static string DefaultScreenshotDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Voice Commander", "Screenshots");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(WhisperModelsDirectory);
        Directory.CreateDirectory(IconsDirectory);
    }
}
