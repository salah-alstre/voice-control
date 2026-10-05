using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Speech;

/// <summary>
/// Alternative engine using the speech recognizers installed with Windows (System.Speech). It needs a Windows
/// speech language pack for the chosen language; without one <see cref="Load"/> reports it as unavailable.
/// </summary>
public sealed class WindowsSpeechEngine : ISpeechRecognitionEngine
{
    public const string Id = "windows";
    private const int SilencePaddingMs = 700;

    private readonly ILogService _log;
    private SpeechRecognitionEngine? _engine;
    private PushStream? _stream;
    private bool _hasGrammar;

    public WindowsSpeechEngine(string language, ILogService? log = null)
    {
        Language = language;
        _log = log ?? NullLogService.Instance;
    }

    public string EngineId => Id;
    public string Language { get; }
    public bool UsesGrammar => _hasGrammar;
    public bool SupportsGrammar => true;
    public ModelVocabulary? Vocabulary => null;
    public event Action<SpeechResult>? Recognized;

    public static bool HasRecognizerFor(string language)
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().Any(r => r.Culture.TwoLetterISOLanguageName == language); }
        catch { return false; }
    }

    public void Load(string? modelPath)
    {
        try
        {
            var info = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == Language);
            if (info == null)
                throw new EngineLoadException(EngineFailure.Unavailable,
                    $"Windows has no speech recognizer installed for '{Language}'.");

            _stream = new PushStream();
            _engine = new SpeechRecognitionEngine(info);
            _engine.SetInputToAudioStream(_stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _engine.SpeechRecognized += OnRecognized;
            _engine.LoadGrammar(new DictationGrammar { Name = "dictation" });
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            _log.Info(LogChannel.Speech, $"Windows speech recognizer loaded for '{Language}' ({info.Culture.Name})");
        }
        catch (EngineLoadException) { throw; }
        catch (Exception ex)
        {
            throw new EngineLoadException(EngineFailure.Failed, "The Windows speech recognizer could not be started: " + ex.Message, ex);
        }
    }

    public void SetGrammar(GrammarPlan plan)
    {
        var engine = _engine;
        if (engine == null) return;
        try
        {
            foreach (var g in engine.Grammars.Where(g => g.Name == "commands").ToList()) engine.UnloadGrammar(g);
            _hasGrammar = false;
            if (plan.Phrases.Count == 0) return;

            var choices = new Choices(plan.Phrases.ToArray());
            var builder = new System.Speech.Recognition.GrammarBuilder { Culture = engine.RecognizerInfo.Culture };
            builder.Append(choices);
            engine.LoadGrammar(new Grammar(builder) { Name = "commands", Priority = 10 });
            _hasGrammar = true;
        }
        catch (Exception ex)
        {
            _log.Error(LogChannel.Speech, "Could not load the command grammar into the Windows recognizer", ex);
        }
    }

    public void Feed(byte[] buffer, int count) => _stream?.Push(buffer, count);

    /// <summary>The recognizer finalizes on silence, so push-to-talk release pads the stream with silence.</summary>
    public void EndUtterance() => _stream?.Push(new byte[16000 * 2 * SilencePaddingMs / 1000], 16000 * 2 * SilencePaddingMs / 1000);

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _log.Info(LogChannel.Speech, $"[{Language}] heard \"{text}\" (confidence {e.Result.Confidence:0.00})");
        Recognized?.Invoke(new SpeechResult(text, e.Result.Confidence, Language));
    }

    public void Dispose()
    {
        try
        {
            _stream?.Complete();
            if (_engine != null)
            {
                _engine.SpeechRecognized -= OnRecognized;
                _engine.RecognizeAsyncCancel();
                _engine.Dispose();
            }
        }
        catch { /* shutting down */ }
        _engine = null;
        _stream = null;
    }

    /// <summary>A blocking stream the recognizer reads from while the microphone pushes into it.</summary>
    private sealed class PushStream : Stream
    {
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _chunks = new();
        private byte[] _current = Array.Empty<byte>();
        private int _offset;

        public void Push(byte[] buffer, int count)
        {
            if (_chunks.IsAddingCompleted) return;
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            try { _chunks.Add(copy); } catch (InvalidOperationException) { }
        }

        public void Complete() => _chunks.CompleteAdding();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset >= _current.Length)
            {
                try { _current = _chunks.Take(); _offset = 0; }
                catch (InvalidOperationException) { return 0; }
            }
            var n = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
