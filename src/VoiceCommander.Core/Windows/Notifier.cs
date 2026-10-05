namespace VoiceCommander.Core.Windows;

/// <summary>Shows a desktop notification. The WPF app supplies the tray-balloon implementation.</summary>
public interface INotifier
{
    void Notify(string title, string message);
}

public sealed class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();
    public void Notify(string title, string message) { }
}
