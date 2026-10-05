using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using VoiceCommander.Core.Infrastructure;
using Whisper.net;

namespace VoiceCommander.Core.Speech;

/// <summary>
/// Offline utterance recognition with Whisper (whisper.cpp through Whisper.net, CPU). The ggml model is loaded once and
/// reused for every utterance; the language is fixed (never auto-detected, never translated).
///
/// Whisper cannot stream, so audio fed in is only buffered (in memory, never written to disk). When an utterance ends
/// - push-to-talk release via <see cref="EndUtterance"/>, or a stretch of silence when <see cref="AutoEndpoint"/> is on -
/// the snapshot is trimmed, silence/noise-only audio is rejected, and the rest is transcribed on the engine's own
/// worker so the caller is never blocked. <see cref="IsProcessing"/> stays true from the end of speech until the result
/// has been published, which is what lets the UI show "Processing speech..." instead of "Ready".
/// </summary>
public sealed class WhisperEngine : ISpeechRecognitionEngine, IAsyncSpeechEngine
{
    public const string Id = "whisper";

    private const int SampleRate = 16000;
    private const int BytesPerMs = SampleRate * 2 / 1000;
    private const int FrameSamples = SampleRate / 50;            // 20 ms
    private const int MaxUtteranceMs = 30_000;                   // Whisper's native window
    private const int AutoMaxUtteranceMs = 20_000;
    private const int AutoSilenceMs = 700;
    private const int PreRollMs = 400;
    private const int MinSpeechMs = 160;
    private const int PadMs = 250;
    private const int MinInputMs = 1000;                         // whisper.cpp wants about a second of input
    private const float SilentPeakRms = 0.008f;                  // below this the whole utterance is treated as silence
    private const float AutoSpeechRms = 0.012f;

    private static readonly Regex Bracketed = new(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫]+", RegexOptions.Compiled);
    // Phrases Whisper is known to invent for silence/noise (subtitle credits, channel outros).
    private static readonly string[] KnownHallucinations =
    {
        "اشتركوا في القناة", "اشترك في القناة", "ترجمة نانسي قنقر", "ترجمة", "شكرا للمشاهدة", "شكرا على المشاهدة",
        "thanks for watching", "thank you for watching", "subtitles by",
    };

    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private Task? _worker;
    private string _modelName = "?";

    private byte[] _buffer = new byte[SampleRate * 2 * 2];
    private int _length;
    private bool _inSpeech;
    private int _speechMs;
    private int _silenceMs;
    private float _noiseFloor = 0.004f;

    private int _pending;                // utterances ended and not yet published
    private int _announced;              // NotifyUtteranceEnding calls whose EndUtterance has not arrived yet
    private volatile bool _disposed;

    public WhisperEngine(string language, ILogService? log = null)
    {
        Language = language;
        _log = log ?? NullLogService.Instance;
    }

    public string EngineId => Id;
    public string Language { get; }
    public bool UsesGrammar => false;
    public bool SupportsGrammar => false;
    public ModelVocabulary? Vocabulary => null;
    public event Action<SpeechResult>? Recognized;

    public bool IsProcessing => Volatile.Read(ref _pending) > 0;
    public event Action? ProcessingChanged;
    public event Action<UtteranceOutcome>? UtteranceRejected;
    public bool AutoEndpoint { get; set; }

    public void Load(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new EngineLoadException(EngineFailure.MissingModel, $"No Whisper model found for '{Language}'.");

        var memoryBefore = ProcessMemoryMb();
        var sw = Stopwatch.StartNew();
        try
        {
            _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
            _processor = _factory.CreateBuilder()
                .WithLanguage(Language)
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 2, 8))
                .WithNoContext()
                .WithSingleSegment()
                .WithProbabilities()
                .Build();
        }
        catch (Exception ex)
        {
            CloseNative();
            throw Classify(ex);
        }
        sw.Stop();
        _modelName = Path.GetFileName(modelPath);
        _log.Info(LogChannel.Speech,
            $"[{Language}] Whisper model {_modelName} loaded in {sw.ElapsedMilliseconds} ms (process memory {memoryBefore} -> {ProcessMemoryMb()} MB)");
        _worker = Task.Factory.StartNew(() => RunAsync(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private static EngineLoadException Classify(Exception ex)
    {
        if (ex is DllNotFoundException or BadImageFormatException || ex.GetType().Name.Contains("Runtime", StringComparison.OrdinalIgnoreCase))
            return new EngineLoadException(EngineFailure.Unavailable, "The Whisper runtime could not be loaded: " + ex.Message, ex);
        if (ex is OutOfMemoryException)
            return new EngineLoadException(EngineFailure.Failed, "Not enough memory to load the Whisper model. Try a smaller model.", ex);
        return new EngineLoadException(EngineFailure.Failed, "The Whisper model could not be loaded (it may be corrupt): " + ex.Message, ex);
    }

    public void SetGrammar(GrammarPlan plan) { }

    public void Feed(byte[] buffer, int count)
    {
        if (_disposed || count <= 0) return;
        count &= ~1;
        bool finalize = false;
        lock (_gate)
        {
            Append(buffer, count);
            if (!AutoEndpoint)
            {
                CapLength(MaxUtteranceMs);
                return;
            }

            var ms = count / BytesPerMs;
            var rms = Rms(buffer, 0, count / 2);
            var threshold = Math.Max(AutoSpeechRms, _noiseFloor * 3f);
            if (rms > threshold)
            {
                _inSpeech = true;
                _silenceMs = 0;
                _speechMs += ms;
            }
            else
            {
                _noiseFloor = _noiseFloor * 0.95f + rms * 0.05f;
                if (_inSpeech) _silenceMs += ms;
                else CapLength(PreRollMs);
            }
            finalize = _inSpeech && (_silenceMs >= AutoSilenceMs || _length >= AutoMaxUtteranceMs * BytesPerMs);
        }
        if (finalize) Complete(auto: true);
    }

    public void NotifyUtteranceEnding()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _announced);
        BeginPending();
    }

    public void EndUtterance()
    {
        if (_disposed) return;
        // The caller announced this end already (pending is counted); otherwise count it now.
        if (Interlocked.Decrement(ref _announced) < 0)
        {
            Interlocked.Exchange(ref _announced, 0);
            BeginPending();
        }
        Complete(auto: false);
    }

    /// <summary>Takes the buffered audio and hands it to the worker (or drops the utterance when there is nothing to transcribe).</summary>
    private void Complete(bool auto)
    {
        byte[] snapshot;
        int speechMs;
        lock (_gate)
        {
            speechMs = _speechMs;
            snapshot = _length > 0 ? _buffer.AsSpan(0, _length).ToArray() : Array.Empty<byte>();
            _length = 0;
            _inSpeech = false;
            _speechMs = 0;
            _silenceMs = 0;
        }

        if (auto)
        {
            // An auto-detected utterance announced nothing beforehand.
            BeginPending();
            if (speechMs < MinSpeechMs) { EndPending(); return; }
        }
        else if (AutoEndpoint && speechMs < MinSpeechMs)
        {
            // Push-to-talk released in always-listening mode: nothing was being said, nothing to report.
            EndPending();
            return;
        }

        if (snapshot.Length == 0 || !_jobs.Writer.TryWrite(new Job(snapshot)))
        {
            Reject(UtteranceOutcome.Empty);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_cts.IsCancellationRequested) { EndPending(); continue; }
                try
                {
                    await TranscribeAsync(job).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    EndPending();
                }
                catch (OutOfMemoryException ex)
                {
                    _log.Error(LogChannel.Speech, $"[{Language}] Whisper ran out of memory transcribing an utterance", ex);
                    Reject(UtteranceOutcome.Failed);
                }
                catch (Exception ex)
                {
                    _log.Error(LogChannel.Speech, $"[{Language}] Whisper transcription failed", ex);
                    Reject(UtteranceOutcome.Failed);
                }
            }
        }
        finally
        {
            // The native objects are only ever touched from this worker, so releasing them here cannot race a transcription.
            CloseNative();
        }
    }

    private async Task TranscribeAsync(Job job)
    {
        var processor = _processor ?? throw new InvalidOperationException("The Whisper model is not loaded.");
        var totalMs = job.Pcm.Length / BytesPerMs;
        var samples = Prepare(job.Pcm, out var trimmedMs, out var peak);
        if (samples == null)
        {
            _log.Info(LogChannel.Speech, $"[{Language}] Whisper skipped an utterance: no speech in {totalMs} ms of audio (peak level {peak:0.000})");
            Reject(UtteranceOutcome.Empty);
            return;
        }

        var sw = Stopwatch.StartNew();
        var text = new StringBuilder();
        double probSum = 0;
        int segments = 0;
        await foreach (var segment in processor.ProcessAsync(samples, _cts.Token).ConfigureAwait(false))
        {
            var part = segment.Text?.Trim();
            if (string.IsNullOrEmpty(part)) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(part);
            probSum += segment.Probability;
            segments++;
        }
        sw.Stop();

        var cleaned = Clean(text.ToString());
        var rawProbability = segments > 0 ? probSum / segments : 0;
        _log.Info(LogChannel.Speech,
            $"[{Language}] Whisper {_modelName}: utterance {trimmedMs} ms (recorded {totalMs} ms), processed in {sw.ElapsedMilliseconds} ms, " +
            $"heard \"{cleaned}\" (probability {rawProbability:0.00}), process memory {ProcessMemoryMb()} MB");

        if (cleaned.Length == 0)
        {
            Reject(UtteranceOutcome.Empty);
            return;
        }

        // Whisper's token probability is not a calibrated command confidence; keep it above the floor and let the
        // deterministic matcher decide whether the text is a command.
        var confidence = Math.Clamp(rawProbability, 0.35, 1.0);
        // Raise the result before the pending count drops, so a listener never sees "idle" with the result still in flight.
        Recognized?.Invoke(new SpeechResult(cleaned, confidence, Language));
        EndPending();
    }

    /// <summary>Trims silence, rejects utterances with no speech, and converts to normalized 16 kHz float samples.</summary>
    internal static float[]? Prepare(byte[] pcm, out int trimmedMs, out float peakRms)
    {
        int total = pcm.Length / 2;
        trimmedMs = 0;
        peakRms = 0;
        if (total < FrameSamples) return null;

        var samples = new float[total];
        for (int i = 0; i < total; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

        int frames = total / FrameSamples;
        var energy = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            int start = f * FrameSamples;
            for (int i = 0; i < FrameSamples; i++) sum += samples[start + i] * samples[start + i];
            energy[f] = (float)Math.Sqrt(sum / FrameSamples);
            if (energy[f] > peakRms) peakRms = energy[f];
        }
        if (peakRms < SilentPeakRms) return null;

        var threshold = Math.Max(SilentPeakRms * 0.6f, peakRms * 0.12f);
        int first = -1, last = -1, voiced = 0;
        for (int f = 0; f < frames; f++)
        {
            if (energy[f] <= threshold) continue;
            if (first < 0) first = f;
            last = f;
            voiced++;
        }
        if (first < 0 || voiced * 20 < MinSpeechMs) return null;

        int padFrames = PadMs / 20;
        int from = Math.Max(0, first - padFrames) * FrameSamples;
        int to = Math.Min(total, (Math.Min(frames - 1, last + padFrames) + 1) * FrameSamples);
        int length = to - from;
        trimmedMs = length / (SampleRate / 1000);

        var result = new float[Math.Max(length, MinInputMs * SampleRate / 1000)];   // padded with silence when shorter than a second
        Array.Copy(samples, from, result, 0, length);

        // Bring quiet microphones up (bounded) so Whisper sees a usable level, and never push anything into clipping.
        float peak = 0;
        for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Abs(result[i]));
        if (peak > 0 && peak < 0.5f)
        {
            float gain = Math.Min(0.5f / peak, 8f);
            for (int i = 0; i < length; i++) result[i] *= gain;
        }
        return result;
    }

    internal static string Clean(string text)
    {
        var t = Bracketed.Replace(text, " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length == 0 || !t.Any(char.IsLetterOrDigit)) return "";
        var core = t.Trim(' ', '.', '!', '?', '،', '؟', '"', '\'', '-', '…');
        foreach (var h in KnownHallucinations)
            if (string.Equals(core, h, StringComparison.OrdinalIgnoreCase))
                return "";
        return t;
    }

    private void Reject(UtteranceOutcome outcome)
    {
        try { UtteranceRejected?.Invoke(outcome); }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "UtteranceRejected handler failed", ex); }
        EndPending();
    }

    private void BeginPending()
    {
        if (Interlocked.Increment(ref _pending) == 1) RaiseProcessingChanged();
    }

    private void EndPending()
    {
        int now;
        do
        {
            now = Volatile.Read(ref _pending);
            if (now <= 0) return;
        } while (Interlocked.CompareExchange(ref _pending, now - 1, now) != now);
        if (now == 1) RaiseProcessingChanged();
    }

    private void RaiseProcessingChanged()
    {
        try { ProcessingChanged?.Invoke(); }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "ProcessingChanged handler failed", ex); }
    }

    private void Append(byte[] source, int count)
    {
        if (_length + count > _buffer.Length)
        {
            var bigger = new byte[Math.Max(_buffer.Length * 2, _length + count)];
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _length);
            _buffer = bigger;
        }
        Buffer.BlockCopy(source, 0, _buffer, _length, count);
        _length += count;
    }

    /// <summary>Keeps only the newest <paramref name="ms"/> of audio.</summary>
    private void CapLength(int ms)
    {
        int max = ms * BytesPerMs;
        if (_length <= max) return;
        Buffer.BlockCopy(_buffer, _length - max, _buffer, 0, max);
        _length = max;
    }

    private static float Rms(byte[] pcm, int offset, int samples)
    {
        if (samples <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            float s = BitConverter.ToInt16(pcm, offset + i * 2) / 32768f;
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / samples);
    }

    private static long ProcessMemoryMb()
    {
        using var p = Process.GetCurrentProcess();
        return p.WorkingSet64 / (1024 * 1024);
    }

    private void CloseNative()
    {
        try { _processor?.Dispose(); } catch (Exception ex) { _log.Warn(LogChannel.Speech, "Disposing the Whisper processor failed: " + ex.Message); }
        try { _factory?.Dispose(); } catch (Exception ex) { _log.Warn(LogChannel.Speech, "Disposing the Whisper model failed: " + ex.Message); }
        _processor = null;
        _factory = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _jobs.Writer.TryComplete();
        if (_worker != null)
        {
            // The worker releases the native model itself once its current transcription returns (it cannot be interrupted midway).
            if (!_worker.Wait(TimeSpan.FromSeconds(5)))
                _log.Warn(LogChannel.Speech, $"[{Language}] Whisper is still finishing a transcription; the model is released when it returns.");
        }
        else
        {
            CloseNative();
        }
        Interlocked.Exchange(ref _announced, 0);
        if (Interlocked.Exchange(ref _pending, 0) > 0) RaiseProcessingChanged();
        _log.Info(LogChannel.Speech, $"[{Language}] Whisper engine disposed");
    }

    private sealed record Job(byte[] Pcm);
}
