using System.Drawing;
using System.Drawing.Imaging;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Windows;

public interface IScreenshotService
{
    /// <summary>Captures the whole virtual screen or the active window and saves it. Returns the file path.</summary>
    string Capture(bool activeWindow, string directory, ScreenshotFormat format);
}

public sealed class ScreenshotService : IScreenshotService
{
    private readonly ILogService _log;
    public ScreenshotService(ILogService log) => _log = log;

    public string Capture(bool activeWindow, string directory, ScreenshotFormat format)
    {
        Directory.CreateDirectory(directory);
        var rect = activeWindow ? ActiveWindowBounds() : VirtualScreen();
        if (rect.Width <= 0 || rect.Height <= 0) rect = VirtualScreen();

        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);

        var (ext, fmt) = format switch
        {
            ScreenshotFormat.Jpeg => ("jpg", ImageFormat.Jpeg),
            ScreenshotFormat.Bmp => ("bmp", ImageFormat.Bmp),
            _ => ("png", ImageFormat.Png),
        };
        var path = Path.Combine(directory, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.{ext}");
        bmp.Save(path, fmt);
        _log.Info(LogChannel.Command, "Screenshot saved: " + path);
        return path;
    }

    private static Rectangle VirtualScreen() => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    private static Rectangle ActiveWindowBounds()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return Rectangle.Empty;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
            Native.GetWindowRect(h, out r);
        return new Rectangle(r.Left, r.Top, r.Width, r.Height);
    }
}
