using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceCommander.Core.Speech;

/// <summary>Developer diagnostics for one capture session (in push-to-talk, one session is one utterance).</summary>
public sealed record AudioSessionStats(
    string Device,
    string InputFormat,
    string RecognizerFormat,
    bool Converted,
    TimeSpan Duration,
    double Peak,
    double Rms,
    long ClippedSamples,
    double ClipPercent,
    double SilentPercent)
{
    public string Summary =>
        $"device=\"{Device}\" input={InputFormat} recognizer={RecognizerFormat}{(Converted ? " (converted)" : "")} duration={Duration.TotalSeconds:0.00}s " +
        $"peak={Peak:0.000} avg={Rms:0.000} clipped={ClippedSamples} ({ClipPercent:0.00}%) silent={SilentPercent:0}%";
}

/// <summary>Accumulates level statistics over 16-bit mono PCM: peak, average (RMS), clipping and how much of the audio was silence.</summary>
public sealed class AudioMeter
{
    /// <summary>A sample this close to full scale counts as clipped.</summary>
    public const int ClipThreshold = 32700;
    /// <summary>A chunk whose RMS is below this (about -47 dBFS) counts as silence.</summary>
    public const double SilenceRms = 0.0045;

    private long _samples, _clipped;
    private double _sumSquares;
    private int _peak;
    private int _chunks, _silentChunks;

    public long Samples => _samples;

    public void Reset()
    {
        _samples = _clipped = 0; _sumSquares = 0; _peak = 0; _chunks = _silentChunks = 0;
    }

    /// <summary>Adds a chunk and returns that chunk's peak (0..1) and RMS (0..1).</summary>
    public (float Peak, float Rms) Add(byte[] pcm, int count)
    {
        var n = count / 2;
        if (n == 0) return (0, 0);
        var peak = 0; double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
            var a = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (a > peak) peak = a;
            if (a >= ClipThreshold) _clipped++;
            sum += (double)s * s;
        }
        _samples += n;
        _sumSquares += sum;
        if (peak > _peak) _peak = peak;
        var rms = Math.Sqrt(sum / n) / 32768.0;
        _chunks++;
        if (rms < SilenceRms) _silentChunks++;
        return (peak / 32768f, (float)rms);
    }

    public AudioSessionStats Snapshot(string device, string inputFormat, string recognizerFormat, bool converted, int sampleRate = 16000) =>
        new(device, inputFormat, recognizerFormat, converted,
            TimeSpan.FromSeconds(_samples / (double)sampleRate),
            _peak / 32768.0,
            _samples == 0 ? 0 : Math.Sqrt(_sumSquares / _samples) / 32768.0,
            _clipped,
            _samples == 0 ? 0 : 100.0 * _clipped / _samples,
            _chunks == 0 ? 100 : 100.0 * _silentChunks / _chunks);
}

/// <summary>
/// Turns captured audio of any common format (PCM16 or float32, any channel count, any sample rate) into what Vosk wants:
/// 16 kHz, 16-bit, mono. Channels are averaged (so a one-sided stereo mic is not halved by a "take the left channel" shortcut
/// and phase differences do not cancel speech), then resampled with NAudio's band-limited WDL resampler.
/// </summary>
public sealed class PcmNormalizer
{
    public const int TargetRate = 16000;

    private readonly WaveFormat _in;
    private readonly FloatQueue _queue;
    private readonly ISampleProvider? _resampler;
    private readonly float[] _scratch = new float[8192];

    public PcmNormalizer(WaveFormat input)
    {
        if (input.Encoding != WaveFormatEncoding.Pcm && input.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new NotSupportedException("Unsupported capture format: " + input);
        if (input.Encoding == WaveFormatEncoding.Pcm && input.BitsPerSample != 16 && input.BitsPerSample != 24 && input.BitsPerSample != 32)
            throw new NotSupportedException("Unsupported capture bit depth: " + input.BitsPerSample);
        _in = input;
        _queue = new FloatQueue(input.SampleRate);
        _resampler = input.SampleRate == TargetRate ? null : new WdlResamplingSampleProvider(_queue, TargetRate);
    }

    public bool IsPassThrough => _in.SampleRate == TargetRate && _in.Channels == 1 && _in.Encoding == WaveFormatEncoding.Pcm && _in.BitsPerSample == 16;

    public byte[] Convert(byte[] buffer, int count)
    {
        if (IsPassThrough)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            return copy;
        }

        var bytesPerSample = _in.BitsPerSample / 8;
        var frame = bytesPerSample * _in.Channels;
        var frames = count / frame;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < _in.Channels; c++)
                sum += ReadSample(buffer, f * frame + c * bytesPerSample);
            _queue.Enqueue((float)(sum / _in.Channels));
        }

        var output = new List<byte>(frames * 2 * TargetRate / Math.Max(1, _in.SampleRate) + 16);
        if (_resampler == null)
        {
            while (_queue.Count > 0)
            {
                var n = _queue.Read(_scratch, 0, _scratch.Length);
                if (n == 0) break;
                AppendPcm16(output, n);
            }
        }
        else
        {
            int n;
            while ((n = _resampler.Read(_scratch, 0, _scratch.Length)) > 0) AppendPcm16(output, n);
        }
        return output.ToArray();
    }

    private float ReadSample(byte[] b, int o)
    {
        if (_in.Encoding == WaveFormatEncoding.IeeeFloat) return BitConverter.ToSingle(b, o);
        return _in.BitsPerSample switch
        {
            16 => (short)(b[o] | (b[o + 1] << 8)) / 32768f,
            24 => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f,
            _ => BitConverter.ToInt32(b, o) / 2147483648f,
        };
    }

    private void AppendPcm16(List<byte> output, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var v = Math.Clamp(_scratch[i], -1f, 1f);
            var s = (short)Math.Round(v * 32767f);
            output.Add((byte)(s & 0xFF));
            output.Add((byte)((s >> 8) & 0xFF));
        }
    }

    /// <summary>Mono float samples waiting to be resampled; reading never blocks and returns what is available.</summary>
    private sealed class FloatQueue : ISampleProvider
    {
        private readonly Queue<float> _q = new();
        public FloatQueue(int rate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public WaveFormat WaveFormat { get; }
        public int Count => _q.Count;
        public void Enqueue(float v) => _q.Enqueue(v);
        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _q.Count);
            for (var i = 0; i < n; i++) buffer[offset + i] = _q.Dequeue();
            return n;
        }
    }
}
