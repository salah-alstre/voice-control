using VoiceCommander.Core.Matching;

namespace VoiceCommander.Core.Speech;

/// <summary>
/// The words a Vosk model can recognize (graph/words.txt), indexed by their normalized form so that the
/// grammar can use the model's own spelling (e.g. Arabic "ة" vs "ه") for a normalized command phrase.
/// </summary>
public sealed class ModelVocabulary
{
    private readonly Dictionary<string, string> _byNormalized;

    private ModelVocabulary(Dictionary<string, string> words) => _byNormalized = words;

    public int Count => _byNormalized.Count;

    /// <summary>Returns the model's spelling for a normalized word, or null when the model does not know it.</summary>
    public string? Resolve(string normalizedWord) => _byNormalized.TryGetValue(normalizedWord, out var w) ? w : null;

    public static ModelVocabulary? Load(string modelPath)
    {
        foreach (var rel in new[] { Path.Combine("graph", "words.txt"), "words.txt" })
        {
            var file = Path.Combine(modelPath, rel);
            if (!File.Exists(file)) continue;
            try
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(file))
                {
                    var space = line.IndexOf(' ');
                    var w = space > 0 ? line[..space] : line;
                    if (w.Length == 0 || w[0] == '<' || w[0] == '#') continue;
                    var n = TextNormalizer.Normalize(w);
                    if (n.Length == 0) continue;
                    // Prefer an exact spelling when several words normalize to the same form.
                    if (!map.TryGetValue(n, out var existing) || (w == n && existing != n)) map[n] = w;
                }
                return map.Count > 0 ? new ModelVocabulary(map) : null;
            }
            catch { return null; }
        }
        return null;
    }

    public static ModelVocabulary FromWords(IEnumerable<string> words)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            var n = TextNormalizer.Normalize(w);
            if (n.Length > 0) map.TryAdd(n, w);
        }
        return new ModelVocabulary(map);
    }
}
