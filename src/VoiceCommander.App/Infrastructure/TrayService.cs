using System.Drawing;
using System.Windows;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.Infrastructure;

/// <summary>System tray icon: open / start-stop listening / exit, plus balloon notifications (the app's <see cref="INotifier"/>).</summary>
public sealed class TrayService : INotifier, IDisposable
{
    // Resolved lazily in Start(): the action handlers need INotifier (this class), and the listening service needs the handlers.
    private readonly IServiceProvider _services;
    private IListeningService _listening = null!;
    private System.Windows.Forms.NotifyIcon? _icon;
    private System.Windows.Forms.ToolStripMenuItem? _open, _toggle, _exit;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService(IServiceProvider services)
    {
        _services = services;
    }

    public void Start()
    {
        _listening = (IListeningService)_services.GetService(typeof(IListeningService))!;
        _icon = new System.Windows.Forms.NotifyIcon { Icon = LoadIcon(), Visible = true };
        _open = new System.Windows.Forms.ToolStripMenuItem();
        _toggle = new System.Windows.Forms.ToolStripMenuItem();
        _exit = new System.Windows.Forms.ToolStripMenuItem();
        _open.Click += (_, _) => OpenRequested?.Invoke();
        _toggle.Click += (_, _) => _listening.Toggle();
        _exit.Click += (_, _) => ExitRequested?.Invoke();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { _open, _toggle, new System.Windows.Forms.ToolStripSeparator(), _exit });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _listening.StateChanged += () => UI.Post(Refresh);
        LocSource.Instance.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
            if (info != null) return new Icon(info.Stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Refresh()
    {
        if (_icon == null || _listening == null) return;
        var l = LocSource.Instance;
        _open!.Text = l["tray.open"];
        _exit!.Text = l["tray.exit"];
        _toggle!.Text = _listening.IsEnabled ? l["tray.stop"] : l["tray.start"];
        var tip = _listening.State switch
        {
            ListeningState.Listening => l["tray.tooltip.listening"],
            ListeningState.Idle => l["tray.tooltip.idle"],
            _ => l["tray.tooltip.stopped"],
        };
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    public void Notify(string title, string message)
    {
        UI.Post(() =>
        {
            try { _icon?.ShowBalloonTip(3000, title, string.IsNullOrWhiteSpace(message) ? " " : message, System.Windows.Forms.ToolTipIcon.Info); }
            catch { }
        });
    }

    public void Dispose()
    {
        if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
    }
}
