using NAudio.Wave;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Core.Services;

public enum PttCue { Start, Stop, Error }

public interface IPttSoundService : IDisposable
{
    /// <summary>Begins following the listening state. Cues play when the state really changes, never on a bare key press.</summary>
    void Start();
    /// <summary>Plays a cue immediately (settings "Test" buttons), regardless of the on/off setting.</summary>
    void Preview(PttCue cue);
}

/// <summary>
/// Short, original synthesized cues for "listening started / stopped / failed". Nothing is read from disk and no third-party
/// sound is used. Playback runs on one dedicated thread and a newer cue always replaces an older one, so rapid presses never
/// stack. The sounds go straight to the default output device, so they work while the window is minimized, hidden in the tray
/// or while a game has focus.
/// </summary>
public sealed class PttSoundService : IPttSoundService
{
    private const int SampleRate = 44100;

    private readonly IListeningService _listening;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private readonly Thread _worker;
    private PttCue? _pending;
    private ListeningState _previous;
    private volatile bool _disposed, _subscribed;
    private int _workerStarted;

    // Rendered once; scaled by the volume setting at play time.
    private static readonly float[] StartWave = Render((660, 0.00, 0.075), (990, 0.065, 0.085));
    private static readonly float[] StopWave = Render((820, 0.00, 0.070), (540, 0.060, 0.090));
    private static readonly float[] ErrorWave = Render((240, 0.00, 0.110), (180, 0.120, 0.150));

    public PttSoundService(IListeningService listening, ISettingsService settings, ILogService log)
    {
        _listening = listening; _settings = settings; _log = log;
        _previous = listening.State;
        _worker = new Thread(Pump) { IsBackground = true, Name = "PttSound", Priority = ThreadPriority.AboveNormal };
    }

    public void Start()
    {
        if (_subscribed) return;
        _subscribed = true;
        _previous = _listening.State;
        EnsureWorker();
        _listening.StateChanged += OnStateChanged;
    }

    public void Preview(PttCue cue)
    {
        EnsureWorker();
        Enqueue(cue);
    }

    private void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) == 0) _worker.Start();
    }

    private void OnStateChanged()
    {
        var now = _listening.State;
        var before = _previous;
        _previous = now;
        if (now == before || !_settings.Current.PttSoundsEnabled) return;

        if (now == ListeningState.Listening) Enqueue(PttCue.Start);
        else if (before == ListeningState.Listening) Enqueue(now == ListeningState.Error ? PttCue.Error : PttCue.Stop);
        else if (now == ListeningState.Error && before == ListeningState.Idle) Enqueue(PttCue.Error);   // the microphone could not be opened
    }

    private void Enqueue(PttCue cue)
    {
        if (_disposed) return;
        lock (_gate) _pending = cue;
        _signal.Set();
    }

    private void Pump()
    {
        while (!_disposed)
        {
            _signal.WaitOne();
            if (_disposed) break;
            PttCue? cue;
            lock (_gate) { cue = _pending; _pending = null; }
            if (cue == null) continue;
            try { PlayBlocking(cue.Value); }
            catch (Exception ex) { _log.Warn(LogChannel.Audio, $"Push-to-talk sound failed: {ex.Message}"); }
        }
    }

    private void PlayBlocking(PttCue cue)
    {
        var wave = cue switch { PttCue.Start => StartWave, PttCue.Stop => StopWave, _ => ErrorWave };
        var volume = Math.Clamp(_settings.Current.PttSoundVolume, 0, 1);
        if (volume <= 0.001) return;
        var gain = (float)(volume * volume * 0.85);   // perceptual curve; full scale still leaves headroom

        var bytes = new byte[wave.Length * 2];
        for (int i = 0; i < wave.Length; i++)
        {
            var v = (short)Math.Clamp(wave[i] * gain * short.MaxValue, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        using var ms = new MemoryStream(bytes);
        using var src = new RawSourceWaveStream(ms, new WaveFormat(SampleRate, 16, 1));
        using var output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 2 };
        output.Init(src);
        output.Play();

        var durationMs = wave.Length * 1000 / SampleRate;
        var started = Environment.TickCount64;
        while (output.PlaybackState == PlaybackState.Playing && !_disposed)
        {
            lock (_gate) { if (_pending != null) break; }               // a newer cue replaces this one
            if (Environment.TickCount64 - started > durationMs + 400) break;
            Thread.Sleep(4);
        }
        output.Stop();
    }

    /// <summary>Soft, plucked sine blips: a fundamental plus a quiet octave, with a short attack and an exponential decay.</summary>
    private static float[] Render(params (double Hz, double StartSec, double LenSec)[] notes)
    {
        var total = notes.Max(n => n.StartSec + n.LenSec) + 0.03;
        var buf = new float[(int)(total * SampleRate)];
        foreach (var (hz, startSec, lenSec) in notes)
        {
            int s0 = (int)(startSec * SampleRate), len = (int)(lenSec * SampleRate);
            for (int i = 0; i < len && s0 + i < buf.Length; i++)
            {
                double t = (double)i / SampleRate;
                double attack = Math.Min(1.0, i / (0.006 * SampleRate));
                double decay = Math.Exp(-t * 26);
                double release = Math.Min(1.0, (len - i) / (0.012 * SampleRate));
                double tone = Math.Sin(2 * Math.PI * hz * t) + 0.22 * Math.Sin(2 * Math.PI * hz * 2 * t);
                buf[s0 + i] += (float)(tone * attack * decay * release * 0.8);
            }
        }
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listening.StateChanged -= OnStateChanged; } catch { }
        _signal.Set();
    }
}
