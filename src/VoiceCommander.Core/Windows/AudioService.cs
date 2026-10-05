using NAudio.CoreAudioApi;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Windows;

public sealed record AudioSessionInfo(string ProcessName, int ProcessId, float Volume, bool Muted);

public interface IAudioService
{
    /// <summary>Master volume 0..100 of the default playback device.</summary>
    int GetMasterVolume();
    void SetMasterVolume(int percent);
    /// <summary>Adds delta (can be negative) and returns the new value.</summary>
    int ChangeMasterVolume(int delta);
    bool IsMasterMuted();
    void SetMasterMuted(bool muted);
    /// <summary>Returns the number of audio sessions affected (0 = app is not currently producing/holding audio).</summary>
    int SetAppVolume(string processName, int percent);
    int ChangeAppVolume(string processName, int delta);
    int SetAppMuted(string processName, bool? muted);
    IReadOnlyList<AudioSessionInfo> GetSessions();
}

/// <summary>Real Windows Core Audio (WASAPI) master and per-application volume via NAudio.</summary>
public sealed class AudioService : IAudioService
{
    private readonly ILogService _log;
    public AudioService(ILogService log) => _log = log;

    private static MMDevice DefaultDevice()
    {
        using var en = new MMDeviceEnumerator();
        return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public int GetMasterVolume()
    {
        using var d = DefaultDevice();
        return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
    }

    public void SetMasterVolume(int percent)
    {
        using var d = DefaultDevice();
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        if (percent > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
        _log.Info(LogChannel.Audio, $"Master volume set to {percent}%");
    }

    public int ChangeMasterVolume(int delta)
    {
        using var d = DefaultDevice();
        var cur = (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        var next = Math.Clamp(cur + delta, 0, 100);
        d.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
        if (delta > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
        _log.Info(LogChannel.Audio, $"Master volume {cur}% -> {next}%");
        return next;
    }

    public bool IsMasterMuted()
    {
        using var d = DefaultDevice();
        return d.AudioEndpointVolume.Mute;
    }

    public void SetMasterMuted(bool muted)
    {
        using var d = DefaultDevice();
        d.AudioEndpointVolume.Mute = muted;
        _log.Info(LogChannel.Audio, muted ? "Master muted" : "Master unmuted");
    }

    private static string Norm(string name) => name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();

    private static IEnumerable<(AudioSessionControl Session, string Name)> Sessions(MMDevice d)
    {
        var mgr = d.AudioSessionManager;
        mgr.RefreshSessions();
        var col = mgr.Sessions;
        for (int i = 0; i < col.Count; i++)
        {
            var s = col[i];
            string name = "";
            try
            {
                var pid = (int)s.GetProcessID;
                if (pid > 0) using (var p = System.Diagnostics.Process.GetProcessById(pid)) name = p.ProcessName;
            }
            catch { /* process exited */ }
            yield return (s, name);
        }
    }

    private int ForEachSession(string processName, Action<AudioSessionControl> act)
    {
        var target = Norm(processName);
        using var d = DefaultDevice();
        int n = 0;
        foreach (var (s, name) in Sessions(d))
        {
            if (name.Length == 0 || Norm(name) != target) continue;
            act(s); n++;
        }
        return n;
    }

    public int SetAppVolume(string processName, int percent) =>
        ForEachSession(processName, s => { s.SimpleAudioVolume.Volume = Math.Clamp(percent, 0, 100) / 100f; if (percent > 0) s.SimpleAudioVolume.Mute = false; });

    public int ChangeAppVolume(string processName, int delta) =>
        ForEachSession(processName, s => s.SimpleAudioVolume.Volume = Math.Clamp(s.SimpleAudioVolume.Volume + delta / 100f, 0f, 1f));

    public int SetAppMuted(string processName, bool? muted) =>
        ForEachSession(processName, s => s.SimpleAudioVolume.Mute = muted ?? !s.SimpleAudioVolume.Mute);

    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        using var d = DefaultDevice();
        return Sessions(d).Where(x => x.Name.Length > 0)
            .Select(x => new AudioSessionInfo(x.Name, (int)x.Session.GetProcessID, x.Session.SimpleAudioVolume.Volume, x.Session.SimpleAudioVolume.Mute)).ToList();
    }
}
