using System.Text.Json;
using Vosk;
using VoiceCommander.Core.Infrastructure;

namespace VoiceCommander.Core.Speech;

/// <summary>
/// Offline recognition with Vosk. When the model supports a dynamic graph, recognition is restricted to the command
/// phrases (plus [unk] so unrelated speech is rejected instead of being forced onto a command). A free-form "shadow"
/// recognizer with N-best output hears the same audio, so every utterance yields several competing hypotheses and the
/// deterministic matcher (not the decoder) decides which one corresponds to a registered command. Both are logged,
/// which gives a per-utterance comparison of grammar-constrained vs unrestricted recognition.
/// </summary>
public sealed class VoskEngine : ISpeechRecognitionEngine
{
    public const string Id = "vosk";
    private const float SampleRate = 16000f;
    /// <summary>Hypotheses requested from the free-form decoder.</summary>
    private const int NBest = 3;

    private readonly ILogService _log;
    private Model? _model;
    private VoskRecognizer? _recognizer;      // grammar-constrained when a grammar is active, otherwise free-form N-best
    private VoskRecognizer? _shadow;          // free-form N-best, only alongside a grammar
    private List<SpeechAlternative>? _shadowStash;   // shadow finished an utterance before the primary did
    private bool _supportsGrammar;
    private bool _hasGrammar;

    public VoskEngine(string language, ILogService? log = null)
    {
        Language = language;
        _log = log ?? NullLogService.Instance;
    }

    public string EngineId => Id;
    public string Language { get; }
    public bool UsesGrammar => _hasGrammar;
    public bool SupportsGrammar => _supportsGrammar;
    public ModelVocabulary? Vocabulary { get; private set; }
    public event Action<SpeechResult>? Recognized;

    /// <summary>True when the folder looks like an extracted Vosk model.</summary>
    public static bool IsModelFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "am")) && Directory.Exists(Path.Combine(path, "conf"));

    /// <summary>True when the model folder contains the graph files needed for a dynamic grammar.</summary>
    public static bool ModelSupportsGrammar(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "graph", "HCLr.fst")) && File.Exists(Path.Combine(path, "graph", "Gr.fst"));

    public void Load(string? modelPath)
    {
        if (!IsModelFolder(modelPath))
            throw new EngineLoadException(EngineFailure.MissingModel, $"No Vosk model found for '{Language}'.");
        try
        {
            global::Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(modelPath!);
        }
        catch (DllNotFoundException ex)
        {
            throw new EngineLoadException(EngineFailure.Unavailable, "The Vosk native library could not be loaded.", ex);
        }
        catch (Exception ex)
        {
            throw new EngineLoadException(EngineFailure.Failed, "The speech model could not be loaded: " + ex.Message, ex);
        }

        Vocabulary = ModelVocabulary.Load(modelPath!);
        _supportsGrammar = ModelSupportsGrammar(modelPath);
        _recognizer = CreateRecognizer(null);
        _log.Info(LogChannel.Speech, $"Vosk model loaded for '{Language}' (dynamic grammar: {_supportsGrammar}, vocabulary: {Vocabulary?.Count ?? 0})");
    }

    public void SetGrammar(GrammarPlan plan)
    {
        if (_model == null) return;
        var old = _recognizer;
        var oldShadow = _shadow;
        string? grammarJson = null;
        if (_supportsGrammar && plan.Phrases.Count > 0)
            grammarJson = JsonSerializer.Serialize(plan.Phrases.Append("[unk]"));

        _shadowStash = null;
        try
        {
            _recognizer = CreateRecognizer(grammarJson);
            _shadow = grammarJson != null ? CreateRecognizer(null) : null;
            _hasGrammar = grammarJson != null;
        }
        catch (Exception ex)
        {
            _log.Error(LogChannel.Speech, "Could not apply the command grammar; recognizing free-form instead", ex);
            _recognizer = CreateRecognizer(null);
            _shadow = null;
            _hasGrammar = false;
        }
        old?.Dispose();
        oldShadow?.Dispose();
        _log.Info(LogChannel.Speech, $"Grammar '{Language}': {(_hasGrammar ? plan.Phrases.Count + " phrases (+ free-form N-best shadow)" : "free-form N-best")}" +
            (plan.Unavailable.Count > 0 ? $", {plan.Unavailable.Count} phrase(s) use words the model does not know" : ""));
        foreach (var u in plan.Unavailable.Take(20)) _log.Info(LogChannel.Speech, "  not in model vocabulary: " + u);
    }

    private VoskRecognizer CreateRecognizer(string? grammarJson)
    {
        var rec = grammarJson == null
            ? new VoskRecognizer(_model!, SampleRate)
            : new VoskRecognizer(_model!, SampleRate, grammarJson);
        rec.SetWords(true);
        // N-best only for free-form decoding: alternatives are what lets the matcher recover a near-miss ("افتتاح" for "افتح").
        if (grammarJson == null) rec.SetMaxAlternatives(NBest);
        return rec;
    }

    public void Feed(byte[] buffer, int count)
    {
        var rec = _recognizer;
        if (rec == null || count <= 0) return;
        var shadow = _shadow;
        if (shadow != null && shadow.AcceptWaveform(buffer, count))
            _shadowStash = ParseHypotheses(shadow.Result(), "free");   // shadow endpointed first; keep until the primary finishes
        if (rec.AcceptWaveform(buffer, count)) Emit(rec.Result());
    }

    /// <summary>Trailing silence fed before finalizing, so the decoder sees the end of the phrase and settles on a stable final hypothesis.</summary>
    private const int EndSilenceMs = 400;

    public void EndUtterance()
    {
        var rec = _recognizer;
        if (rec == null) return;
        var shadow = _shadow;
        string? primaryJson = null;
        try
        {
            // 16-bit mono PCM silence; an endpoint reached on it yields the final result through the normal path.
            var silence = new byte[(int)SampleRate * 2 * EndSilenceMs / 1000];
            if (shadow != null && shadow.AcceptWaveform(silence, silence.Length)) _shadowStash = ParseHypotheses(shadow.Result(), "free");
            if (rec.AcceptWaveform(silence, silence.Length)) primaryJson = rec.Result();
        }
        catch (Exception ex) { _log.Error(LogChannel.Speech, "Could not pad the end of the utterance", ex); }
        primaryJson ??= rec.FinalResult();   // empty text when the padding already produced the final result
        Emit(primaryJson);
        // Whatever the shadow still holds belongs to an utterance the primary has already finished: drop it so it never repeats.
        if (shadow != null) { try { shadow.FinalResult(); } catch { } }
        _shadowStash = null;
    }

    /// <summary>Called when the primary recognizer reached an end of utterance.</summary>
    private void Emit(string json)
    {
        try
        {
            var primary = ParseHypotheses(json, _hasGrammar ? "grammar" : "free");
            List<SpeechAlternative>? free = null;
            if (_shadow != null)
            {
                free = _shadowStash;
                _shadowStash = null;
                // The shadow may still be mid-utterance: finalize it so both decoders stay aligned on utterance boundaries.
                if (free == null || free.Count == 0) free = ParseHypotheses(_shadow.FinalResult(), "free");
                else _shadow.FinalResult();
            }

            var all = new List<SpeechAlternative>(primary);
            if (free != null) all.AddRange(free);
            if (all.Count == 0) return;

            // Primary text: the grammar hypothesis when the grammar accepted something, else the best free-form hypothesis.
            var top = all[0];
            _log.Info(LogChannel.Speech, $"[{Language}] heard \"{top.Text}\" (confidence {top.Confidence:0.00}, {top.Source})" +
                (all.Count > 1 ? " | alternatives: " + string.Join(" ; ", all.Skip(1).Select(a => $"{a.Source}:\"{a.Text}\" {a.Confidence:0.00}")) : ""));
            Recognized?.Invoke(new SpeechResult(top.Text, top.Confidence, Language, all.Skip(1).ToList()));
        }
        catch (Exception ex)
        {
            _log.Error(LogChannel.Speech, "Could not read a recognition result", ex);
        }
    }

    /// <summary>
    /// Reads Vosk output: either {"text":..,"result":[..]} or, with N-best enabled, {"alternatives":[{"text","confidence","result"?}]}.
    /// "[unk]" and empty hypotheses are dropped. Confidence is the mean per-word confidence when present.
    /// </summary>
    public static List<SpeechAlternative> ParseHypotheses(string json, string source)
    {
        var list = new List<SpeechAlternative>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("alternatives", out var alts) && alts.ValueKind == JsonValueKind.Array)
        {
            var rank = 0;
            foreach (var a in alts.EnumerateArray())
            {
                var text = Clean(a);
                if (text.Length == 0) { rank++; continue; }
                var conf = WordConfidence(a) ?? (rank == 0 ? 1.0 : Math.Max(0.3, 0.7 - 0.15 * rank));
                list.Add(new SpeechAlternative(text, conf, rank == 0 ? source : "nbest"));
                rank++;
            }
        }
        else
        {
            var text = Clean(root);
            if (text.Length > 0) list.Add(new SpeechAlternative(text, WordConfidence(root) ?? 1.0, source));
        }
        return list;
    }

    private static string Clean(JsonElement e)
    {
        var text = e.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
        // A hypothesis that is only [unk] is noise.
        return text == "[unk]" ? "" : text;
    }

    private static double? WordConfidence(JsonElement e)
    {
        double sum = 0; var n = 0;
        if (e.TryGetProperty("result", out var words) && words.ValueKind == JsonValueKind.Array)
            foreach (var w in words.EnumerateArray())
                if (w.TryGetProperty("conf", out var c) && c.TryGetDouble(out var conf)) { sum += conf; n++; }
        return n > 0 ? sum / n : null;
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _shadow?.Dispose();
        _shadow = null;
        _model?.Dispose();
        _model = null;
    }
}
