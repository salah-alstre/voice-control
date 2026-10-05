using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Speech;

public sealed record MicrophoneDevice(int Index, string Name, string? MmeName = null);

public interface IMicrophoneService : IDisposable
{
    bool IsCapturing { get; }
    /// <summary>16 kHz mono 16-bit PCM chunks (~60 ms). Raised on the capture thread.</summary>
    event Action<byte[], int>? AudioAvailable;
    /// <summary>Peak level 0..1 of each chunk.</summary>
    event Action<float>? LevelChanged;
    /// <summary>Capture stopped because the device failed or was unplugged.</summary>
    event Action<string>? Failed;
    /// <summary>Developer diagnostics of the capture session that just ended (device, formats, levels, clipping, duration).</summary>
    event Action<AudioSessionStats>? SessionEnded;
    /// <summary>The most recent finished capture session, or null.</summary>
    AudioSessionStats? LastSession { get; }

    IReadOnlyList<MicrophoneDevice> GetDevices();
    /// <summary>Starts capturing from the named device (null/empty = Windows default). Returns an error message or null.</summary>
    string? Start(string? deviceName);
    void Stop();
}

/// <summary>
/// Microphone capture via NAudio. Audio is never written to disk; it only flows to the recognizer and the level meter.
/// The recognizer always receives 16 kHz / 16-bit / mono. Windows is first asked for exactly that; if the device refuses,
/// the device's own format (44.1/48 kHz, mono/stereo) is captured and downmixed and resampled here (<see cref="PcmNormalizer"/>).
/// </summary>
public sealed class MicrophoneService : IMicrophoneService
{
    private const string RecognizerFormat = "16000 Hz / 16-bit / mono";
    private static readonly int[] FallbackRates = { 48000, 44100 };

    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly object _dataGate = new();     // Stop() waits for an in-flight chunk so no audio is queued after it returns
    private readonly AudioMeter _meter = new();
    private WaveInEvent? _waveIn;
    private PcmNormalizer? _normalizer;
    private string _deviceLabel = "";
    private string _inputFormat = "";
    private bool _converted;

    public MicrophoneService(ILogService? log = null) => _log = log ?? NullLogService.Instance;

    public bool IsCapturing { get { lock (_gate) return _waveIn != null; } }
    public AudioSessionStats? LastSession { get; private set; }
    public event Action<byte[], int>? AudioAvailable;
    public event Action<float>? LevelChanged;
    public event Action<string>? Failed;
    public event Action<AudioSessionStats>? SessionEnded;

    public IReadOnlyList<MicrophoneDevice> GetDevices()
    {
        var list = new List<MicrophoneDevice>();
        try
        {
            var full = GetEndpointNames();
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var mme = WaveInEvent.GetCapabilities(i).ProductName;
                list.Add(new MicrophoneDevice(i, ResolveFullName(mme, full), mme));
            }
        }
        catch (Exception ex) { _log.Error(LogChannel.Audio, "Could not list microphones", ex); }
        return list;
    }

    /// <summary>Full capture-endpoint names; empty if the Core Audio API is unavailable.</summary>
    private List<string> GetEndpointNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => { using (d) return d.FriendlyName; })
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Warn(LogChannel.Audio, $"Could not read full microphone names: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>The format Windows itself mixes this endpoint in (what the driver really delivers), for diagnostics only.</summary>
    private string DescribeEndpoint(string? fullName)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? dev = null;
            if (string.IsNullOrWhiteSpace(fullName)) dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            else
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (dev == null && d.FriendlyName == fullName) dev = d; else d.Dispose();
                }
            }
            if (dev == null) return "unknown";
            using (dev)
            {
                var f = dev.AudioClient.MixFormat;
                return $"{dev.FriendlyName}: {f.SampleRate} Hz / {f.BitsPerSample}-bit {f.Encoding} / {f.Channels} ch";
            }
        }
        catch (Exception ex) { return "unreadable (" + ex.Message + ")"; }
    }

    /// <summary>
    /// The legacy (MME) API cuts device names at 31 characters. Returns the full endpoint name when exactly one
    /// endpoint starts with the MME name; otherwise the MME name unchanged.
    /// </summary>
    public static string ResolveFullName(string mmeName, IReadOnlyList<string> endpointNames)
    {
        if (mmeName.Length < 31) return mmeName;
        var matches = endpointNames.Where(n => n.StartsWith(mmeName, StringComparison.Ordinal)).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : mmeName;
    }

    public string? Start(string? deviceName)
    {
        lock (_gate)
        {
            if (_waveIn != null) return null;
            try
            {
                var devices = GetDevices();
                if (devices.Count == 0) return "no-microphone";

                var device = -1; // Windows default
                MicrophoneDevice? chosen = null;
                if (!string.IsNullOrWhiteSpace(deviceName))
                {
                    // Accepts the full name or the truncated name older versions saved.
                    chosen = devices.FirstOrDefault(d => d.Name == deviceName)
                             ?? devices.FirstOrDefault(d => d.MmeName == deviceName);
                    if (chosen == null) return "microphone-missing";
                    device = chosen.Index;
                }

                _deviceLabel = chosen?.Name ?? "Windows default device";
                var started = OpenDevice(device);
                if (started == null) return "microphone-failed";

                _log.Info(LogChannel.Audio,
                    $"Microphone started: {_deviceLabel} | capture {_inputFormat} -> recognizer {RecognizerFormat}{(_converted ? " (downmix + resample)" : "")} | endpoint {DescribeEndpoint(chosen?.Name)}");
                return null;
            }
            catch (Exception ex)
            {
                _log.Error(LogChannel.Audio, "Could not start the microphone", ex);
                _waveIn = null;
                return "microphone-failed";
            }
        }
    }

    /// <summary>
    /// Opens the device. First choice is 16 kHz/16-bit/mono (Windows resamples inside the driver stack). If the device or driver
    /// refuses, its own 48/44.1 kHz stereo or mono format is captured and converted here. Must be called under <c>_gate</c>.
    /// </summary>
    private WaveInEvent? OpenDevice(int device)
    {
        var attempts = new List<WaveFormat> { new(16000, 16, 1) };
        foreach (var rate in FallbackRates)
        {
            attempts.Add(new WaveFormat(rate, 16, 2));
            attempts.Add(new WaveFormat(rate, 16, 1));
        }

        Exception? last = null;
        foreach (var format in attempts)
        {
            WaveInEvent? wave = null;
            try
            {
                var normalizer = new PcmNormalizer(format);
                wave = Create(device, format);
                _normalizer = normalizer;
                _converted = !normalizer.IsPassThrough;
                _inputFormat = $"{format.SampleRate} Hz / {format.BitsPerSample}-bit / {format.Channels} ch";
                _meter.Reset();
                wave.DataAvailable += OnData;
                wave.RecordingStopped += OnStopped;
                _waveIn = wave;          // set before recording so the very first chunk is not discarded by OnData's sender check
                wave.StartRecording();
                return wave;
            }
            catch (Exception ex)
            {
                last = ex;
                _waveIn = null;
                if (wave != null)
                {
                    wave.DataAvailable -= OnData;
                    wave.RecordingStopped -= OnStopped;
                    try { wave.Dispose(); } catch { /* already unusable */ }
                }
                _log.Warn(LogChannel.Audio, $"Microphone refused {format.SampleRate} Hz / {format.Channels} ch: {ex.Message}");
            }
        }
        _log.Error(LogChannel.Audio, "No capture format worked for this microphone", last);
        return null;
    }

    private static WaveInEvent Create(int device, WaveFormat format) => new()
    {
        DeviceNumber = device,
        WaveFormat = format,
        BufferMilliseconds = 60,
        NumberOfBuffers = 4,
    };

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        lock (_dataGate)
        {
            if (!ReferenceEquals(sender, _waveIn)) return;
            byte[] pcm;
            var normalizer = _normalizer;
            if (normalizer == null || normalizer.IsPassThrough)
            {
                pcm = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
            }
            else
            {
                pcm = normalizer.Convert(e.Buffer, e.BytesRecorded);
                if (pcm.Length == 0) return;
            }

            var (peak, _) = _meter.Add(pcm, pcm.Length);
            LevelChanged?.Invoke(peak);
            AudioAvailable?.Invoke(pcm, pcm.Length);
        }
    }

    public void Stop()
    {
        WaveInEvent? wave;
        lock (_gate) { wave = _waveIn; _waveIn = null; }
        if (wave == null) return;
        try
        {
            wave.DataAvailable -= OnData;
            wave.RecordingStopped -= OnStopped;
            wave.StopRecording();
        }
        catch (Exception ex) { _log.Warn(LogChannel.Audio, "Stopping the microphone: " + ex.Message); }
        finally { wave.Dispose(); }

        AudioSessionStats stats;
        lock (_dataGate)   // an in-flight chunk finishes (and reaches the recognizer) before the caller finalizes the utterance
        {
            stats = _meter.Snapshot(_deviceLabel, _inputFormat, RecognizerFormat, _converted);
            LastSession = stats;
        }
        LevelChanged?.Invoke(0);
        _log.Info(LogChannel.Audio, "Microphone stopped | " + stats.Summary);
        if (stats.ClipPercent > 0.5) _log.Warn(LogChannel.Audio, "Input is clipping: lower the microphone volume in Windows");
        try { SessionEnded?.Invoke(stats); } catch (Exception ex) { _log.Warn(LogChannel.Audio, "Audio diagnostics handler failed: " + ex.Message); }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        _log.Error(LogChannel.Audio, "Microphone stopped unexpectedly", e.Exception);
        lock (_gate) { if (!ReferenceEquals(_waveIn, sender)) return; _waveIn = null; }
        (sender as IDisposable)?.Dispose();
        LevelChanged?.Invoke(0);
        Failed?.Invoke("microphone-failed");
    }

    public void Dispose() => Stop();
}
