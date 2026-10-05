using NAudio.Wave;
using VoiceCommander.Core.Speech;

namespace VoiceCommander.Tests.Speech;

public class AudioPipelineTests
{
    private static byte[] Pcm16(IEnumerable<short> samples)
    {
        var list = samples.ToList();
        var b = new byte[list.Count * 2];
        for (var i = 0; i < list.Count; i++) { b[2 * i] = (byte)(list[i] & 0xFF); b[2 * i + 1] = (byte)((list[i] >> 8) & 0xFF); }
        return b;
    }

    private static short[] ToShorts(byte[] b)
    {
        var s = new short[b.Length / 2];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(b[2 * i] | (b[2 * i + 1] << 8));
        return s;
    }

    /// <summary>Rising zero crossings per second of a mono signal: a cheap frequency estimate.</summary>
    private static double Frequency(short[] s, int rate)
    {
        var crossings = 0;
        for (var i = 1; i < s.Length; i++) if (s[i - 1] < 0 && s[i] >= 0) crossings++;
        return crossings / (s.Length / (double)rate);
    }

    private static byte[] Sine(int rate, int channels, double hz, double seconds, double amp = 0.5, int bits = 16, bool isFloat = false, double rightScale = 1.0)
    {
        var frames = (int)(rate * seconds);
        var bytesPer = bits / 8;
        var buf = new byte[frames * channels * bytesPer];
        for (var f = 0; f < frames; f++)
        {
            var v = Math.Sin(2 * Math.PI * hz * f / rate) * amp;
            for (var c = 0; c < channels; c++)
            {
                var x = c == 1 ? v * rightScale : v;
                var o = (f * channels + c) * bytesPer;
                if (isFloat) BitConverter.GetBytes((float)x).CopyTo(buf, o);
                else if (bits == 16) BitConverter.GetBytes((short)Math.Round(x * 32767)).CopyTo(buf, o);
                else if (bits == 24)
                {
                    var i24 = (int)Math.Round(x * 8388607);
                    buf[o] = (byte)i24; buf[o + 1] = (byte)(i24 >> 8); buf[o + 2] = (byte)(i24 >> 16);
                }
                else BitConverter.GetBytes((int)Math.Round(x * 2147483647)).CopyTo(buf, o);
            }
        }
        return buf;
    }

    /// <summary>Feeds the signal in 60 ms chunks like the microphone does and concatenates the output.</summary>
    private static short[] Normalize(WaveFormat fmt, byte[] input)
    {
        var n = new PcmNormalizer(fmt);
        var chunk = fmt.AverageBytesPerSecond * 60 / 1000 / fmt.BlockAlign * fmt.BlockAlign;
        var all = new List<short>();
        for (var i = 0; i < input.Length; i += chunk)
        {
            var len = Math.Min(chunk, input.Length - i);
            var part = new byte[len];
            Array.Copy(input, i, part, 0, len);
            all.AddRange(ToShorts(n.Convert(part, len)));
        }
        return all.ToArray();
    }

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(48000, 1)]
    [InlineData(44100, 2)]
    [InlineData(44100, 1)]
    [InlineData(32000, 1)]
    public void Normalizer_outputs_16k_mono_with_the_right_length_and_pitch(int rate, int channels)
    {
        var input = Sine(rate, channels, 440, 2.0);
        var output = Normalize(new WaveFormat(rate, 16, channels), input);

        // 2 s in -> about 2 s out at 16 kHz (the resampler holds back a few ms of filter delay).
        Assert.InRange(output.Length, 16000 * 2 - 800, 16000 * 2 + 50);
        Assert.InRange(Frequency(output, 16000), 430, 450);
        Assert.InRange(output.Max(x => (int)x), 14000, 17000);   // amplitude preserved (0.5 FS)
    }

    [Fact]
    public void Normalizer_16k_mono_is_pass_through_and_bit_exact()
    {
        var input = Pcm16(new short[] { 1, -2, 300, -32768, 32767 });
        var n = new PcmNormalizer(new WaveFormat(16000, 16, 1));
        Assert.True(n.IsPassThrough);
        Assert.Equal(input, n.Convert(input, input.Length));
    }

    [Fact]
    public void Normalizer_downmix_averages_channels_so_a_one_sided_stereo_mic_is_not_lost()
    {
        // Speech on the left channel only (right is silent): averaging halves it, but it must not vanish.
        var input = Sine(16000, 2, 300, 1.0, amp: 0.8, rightScale: 0.0);
        var output = Normalize(new WaveFormat(16000, 16, 2), input);
        var peak = output.Max(x => Math.Abs((int)x));
        Assert.InRange(peak, 12000, 14000);   // ~0.4 FS
    }

    [Fact]
    public void Normalizer_downmix_does_not_clip_when_both_channels_are_loud()
    {
        var input = Sine(48000, 2, 500, 1.0, amp: 0.99);
        var output = Normalize(new WaveFormat(48000, 16, 2), input);
        Assert.True(output.Max(x => (int)x) <= short.MaxValue);
        Assert.InRange(output.Max(x => (int)x), 30000, 32767);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void Normalizer_reads_24_and_32_bit_pcm(int bits)
    {
        var input = Sine(48000, 2, 440, 1.0, bits: bits);
        var output = Normalize(new WaveFormat(48000, bits, 2), input);
        Assert.InRange(Frequency(output, 16000), 430, 450);
        Assert.InRange(output.Max(x => (int)x), 14000, 17000);
    }

    [Fact]
    public void Normalizer_reads_float32()
    {
        var input = Sine(48000, 2, 440, 1.0, bits: 32, isFloat: true);
        var output = Normalize(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), input);
        Assert.InRange(Frequency(output, 16000), 430, 450);
        Assert.InRange(output.Max(x => (int)x), 14000, 17000);
    }

    [Fact]
    public void Normalizer_rejects_unsupported_encodings()
    {
        Assert.Throws<NotSupportedException>(() => new PcmNormalizer(WaveFormat.CreateALawFormat(8000, 1)));
    }

    // ---------- AudioMeter ----------

    [Fact]
    public void Meter_reports_peak_average_and_duration()
    {
        var m = new AudioMeter();
        var pcm = Pcm16(Enumerable.Repeat((short)16384, 16000));   // 1 s of a constant 0.5 FS signal
        var (peak, rms) = m.Add(pcm, pcm.Length);
        Assert.Equal(0.5f, peak, 3);
        Assert.Equal(0.5f, rms, 3);

        var s = m.Snapshot("Mic", "16000 Hz", "16000 Hz", false);
        Assert.Equal(1.0, s.Duration.TotalSeconds, 3);
        Assert.Equal(0.5, s.Peak, 3);
        Assert.Equal(0.5, s.Rms, 3);
        Assert.Equal(0, s.ClippedSamples);
        Assert.Equal(0.0, s.SilentPercent);
    }

    [Fact]
    public void Meter_counts_clipping()
    {
        var m = new AudioMeter();
        var samples = new List<short>();
        for (var i = 0; i < 1000; i++) samples.Add(i % 10 == 0 ? short.MaxValue : (short)100);   // 10 % clipped
        var pcm = Pcm16(samples);
        m.Add(pcm, pcm.Length);
        var s = m.Snapshot("Mic", "", "", false);
        Assert.Equal(100, s.ClippedSamples);
        Assert.Equal(10.0, s.ClipPercent, 1);
        Assert.Equal(1.0, s.Peak, 2);
    }

    [Fact]
    public void Meter_negative_full_scale_counts_as_clipped_and_does_not_overflow()
    {
        var m = new AudioMeter();
        var pcm = Pcm16(new[] { short.MinValue, short.MinValue });
        var (peak, _) = m.Add(pcm, pcm.Length);
        Assert.Equal(1.0f, peak, 3);
        Assert.Equal(2, m.Snapshot("", "", "", false).ClippedSamples);
    }

    [Fact]
    public void Meter_detects_silence_and_speech_chunks()
    {
        var m = new AudioMeter();
        var silence = Pcm16(Enumerable.Repeat((short)3, 960));          // far below -47 dBFS
        var speech = Pcm16(Enumerable.Repeat((short)8000, 960));
        for (var i = 0; i < 3; i++) m.Add(silence, silence.Length);
        m.Add(speech, speech.Length);
        Assert.Equal(75.0, m.Snapshot("", "", "", false).SilentPercent, 1);
    }

    [Fact]
    public void Meter_with_no_audio_is_all_silent_and_zero()
    {
        var s = new AudioMeter().Snapshot("Mic", "", "", false);
        Assert.Equal(TimeSpan.Zero, s.Duration);
        Assert.Equal(0, s.Peak);
        Assert.Equal(100, s.SilentPercent);
    }

    [Fact]
    public void Meter_reset_clears_everything_and_summary_names_the_device()
    {
        var m = new AudioMeter();
        var pcm = Pcm16(Enumerable.Repeat(short.MaxValue, 100));
        m.Add(pcm, pcm.Length);
        m.Reset();
        var s = m.Snapshot("USB Mic", "48000 Hz / 16-bit / 2 ch", "16000 Hz / 16-bit / mono", true);
        Assert.Equal(0, s.ClippedSamples);
        Assert.Contains("USB Mic", s.Summary);
        Assert.Contains("(converted)", s.Summary);
        Assert.Contains("48000 Hz", s.Summary);
    }

    [Fact]
    public void Meter_ignores_an_odd_trailing_byte()
    {
        var m = new AudioMeter();
        var pcm = new byte[] { 0x00, 0x40, 0xFF };
        m.Add(pcm, pcm.Length);
        Assert.Equal(1, m.Samples);
    }
}
