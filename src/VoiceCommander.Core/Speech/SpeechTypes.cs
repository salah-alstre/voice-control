namespace VoiceCommander.Core.Speech;

/// <summary>One competing transcription of an utterance. Source is "grammar", "free" or "nbest".</summary>
public sealed record SpeechAlternative(string Text, double Confidence, string Source);

/// <summary>
/// One finished utterance from an engine. Text is the engine's primary hypothesis (raw; matching normalizes it later).
/// Alternatives holds every other hypothesis the engine produced (grammar-constrained, free-form, N-best) so the
/// command matcher can pick the one that actually corresponds to a registered command.
/// </summary>
public sealed record SpeechResult(string Text, double Confidence, string Language, IReadOnlyList<SpeechAlternative>? Alternatives = null)
{
    /// <summary>Primary hypothesis first, then the alternatives, de-duplicated by text.</summary>
    public IReadOnlyList<SpeechAlternative> AllHypotheses()
    {
        var list = new List<SpeechAlternative> { new(Text, Confidence, "primary") };
        if (Alternatives != null)
            foreach (var a in Alternatives)
                if (!string.IsNullOrWhiteSpace(a.Text) && !list.Any(x => string.Equals(x.Text, a.Text, StringComparison.Ordinal)))
                    list.Add(a);
        return list;
    }
}

public enum EngineStatus { NotLoaded, Loading, Ready, MissingModel, Unavailable, Failed }

public sealed record LanguageEngineState(string Language, EngineStatus Status, string? Detail = null, bool UsesGrammar = false, string? EngineId = null);

public enum EngineFailure { MissingModel, Unavailable, Failed }

public sealed class EngineLoadException : Exception
{
    public EngineFailure Kind { get; }
    public EngineLoadException(EngineFailure kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>
/// What an engine is asked to listen for: every spoken form of every enabled command (templates already expanded).
/// </summary>
public sealed class GrammarPlan
{
    public string Language { get; init; } = "en";
    public IReadOnlyList<string> Phrases { get; init; } = Array.Empty<string>();
    /// <summary>Phrases that could not be put in the grammar because a word is unknown to the speech model.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = Array.Empty<string>();

    public static GrammarPlan Empty(string language) => new() { Language = language };
}

/// <summary>
/// A speech engine for one language. Not thread-safe: <see cref="SpeechEngineManager"/> drives each instance
/// from a single worker thread. Audio is 16 kHz, mono, 16-bit PCM.
/// </summary>
public interface ISpeechRecognitionEngine : IDisposable
{
    string EngineId { get; }
    string Language { get; }
    /// <summary>True when recognition is restricted to the command grammar.</summary>
    bool UsesGrammar { get; }
    /// <summary>True when the loaded model can restrict recognition to the command grammar.</summary>
    bool SupportsGrammar { get; }
    ModelVocabulary? Vocabulary { get; }

    /// <summary>Raised for each finished utterance (possibly from another thread). Empty text is never raised.</summary>
    event Action<SpeechResult>? Recognized;

    /// <exception cref="EngineLoadException"/>
    void Load(string? modelPath);
    void SetGrammar(GrammarPlan plan);
    void Feed(byte[] buffer, int count);
    /// <summary>Marks the end of speech (push-to-talk released): the engine finalizes what it has heard.</summary>
    void EndUtterance();
}

/// <summary>Why an utterance produced no result.</summary>
public enum UtteranceOutcome
{
    /// <summary>Nothing but silence/noise was recorded, or the recognizer returned no text.</summary>
    Empty,
    /// <summary>The recognizer failed while transcribing (model error, out of memory, ...).</summary>
    Failed,
}

/// <summary>
/// An engine that transcribes a whole finished utterance on its own thread (Whisper) instead of streaming results
/// while audio arrives (Vosk). Results arrive some time after <see cref="ISpeechRecognitionEngine.EndUtterance"/>, so the
/// engine reports when it is busy and when an utterance was dropped, letting the UI show "Processing speech..." honestly.
/// </summary>
public interface IAsyncSpeechEngine
{
    /// <summary>True from the moment an utterance ends until its result (or rejection) has been published.</summary>
    bool IsProcessing { get; }
    event Action? ProcessingChanged;
    /// <summary>Raised when an utterance ended without a recognition result.</summary>
    event Action<UtteranceOutcome>? UtteranceRejected;
    /// <summary>When true the engine finds utterance boundaries itself (always-listening); otherwise only <c>EndUtterance</c> ends one.</summary>
    bool AutoEndpoint { get; set; }
    /// <summary>
    /// Called synchronously, from any thread, as soon as an utterance is known to end. The matching queued
    /// <c>EndUtterance</c> call then continues the same unit of work, so <see cref="IsProcessing"/> never dips in between.
    /// </summary>
    void NotifyUtteranceEnding();
}
