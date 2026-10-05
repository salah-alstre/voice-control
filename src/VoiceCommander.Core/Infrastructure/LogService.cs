using System.Collections.Concurrent;
using System.Text;

namespace VoiceCommander.Core.Infrastructure;

public enum LogChannel { App, Speech, Command, Audio }

public interface ILogService
{
    void Info(LogChannel channel, string message);
    void Warn(LogChannel channel, string message);
    void Error(LogChannel channel, string message, Exception? ex = null);
    /// <summary>Raised (from the calling thread) for every line logged; used by the Developer page's live log.</summary>
    event Action<LogChannel, string>? Written;
}

/// <summary>
/// Separate rolling log files per channel (app / speech / command / audio). Each file rolls at 2 MB and only the
/// most recent files are kept. No audio is ever logged.
/// </summary>
public sealed class LogService : ILogService, IDisposable
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFilesPerChannel = 4;
    private readonly string _dir;
    private readonly BlockingCollection<(LogChannel Channel, string Line)> _queue = new(2000);
    private readonly Task _writer;

    public event Action<LogChannel, string>? Written;

    public LogService(string? directory = null)
    {
        _dir = directory ?? AppPaths.LogsDirectory;
        try { Directory.CreateDirectory(_dir); } catch { /* logging must never break the app */ }
        _writer = Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
    }

    public void Info(LogChannel channel, string message) => Enqueue(channel, "INFO ", message, null);
    public void Warn(LogChannel channel, string message) => Enqueue(channel, "WARN ", message, null);
    public void Error(LogChannel channel, string message, Exception? ex = null) => Enqueue(channel, "ERROR", message, ex);

    private void Enqueue(LogChannel channel, string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').Append(message);
        if (ex != null) sb.AppendLine().Append(ex);
        var line = sb.ToString();
        _queue.TryAdd((channel, line));
        try { Written?.Invoke(channel, line); } catch { /* a faulty listener must not break logging */ }
    }

    private void Pump()
    {
        foreach (var (channel, line) in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_dir, $"{channel.ToString().ToLowerInvariant()}.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) Roll(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* ignore IO errors in logging */ }
        }
    }

    private static void Roll(string path)
    {
        for (var i = MaxFilesPerChannel - 1; i >= 1; i--)
        {
            var from = $"{path}.{i}";
            var to = $"{path}.{i + 1}";
            if (!File.Exists(from)) continue;
            if (i + 1 >= MaxFilesPerChannel) File.Delete(from);
            else File.Move(from, to, true);
        }
        File.Move(path, $"{path}.1", true);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _writer.Wait(1500); } catch { }
    }
}

public sealed class NullLogService : ILogService
{
    public static readonly NullLogService Instance = new();
#pragma warning disable CS0067
    public event Action<LogChannel, string>? Written;
#pragma warning restore CS0067
    public void Info(LogChannel channel, string message) { }
    public void Warn(LogChannel channel, string message) { }
    public void Error(LogChannel channel, string message, Exception? ex = null) { }
}
