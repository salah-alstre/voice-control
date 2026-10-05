using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace VoiceCommander.Core.Localization;

public interface ILocalizer : INotifyPropertyChanged
{
    string Language { get; }
    bool IsRtl { get; }
    /// <summary>Indexer used by XAML bindings (Item[]); raising PropertyChanged("Item[]") refreshes every bound string live.</summary>
    string this[string key] { get; }
    string Get(string key, params object?[] args);
    bool Has(string key);
    void SetLanguage(string language);
    IReadOnlyList<(string Code, string NativeName)> SupportedLanguages { get; }
}

/// <summary>Embedded en.json / ar.json string tables. Missing keys fall back to English, then to the key itself.</summary>
public sealed class Localizer : ILocalizer
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new();
    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<(string Code, string NativeName)> SupportedLanguages { get; } =
        new[] { ("en", "English"), ("ar", "العربية") };

    public Localizer()
    {
        foreach (var (code, _) in SupportedLanguages) _tables[code] = Load(code);
    }

    public string Language => _language;
    public bool IsRtl => _language == "ar";

    public string this[string key] => Get(key);

    public bool Has(string key) => _tables[_language].ContainsKey(key) || _tables["en"].ContainsKey(key);

    public string Get(string key, params object?[] args)
    {
        if (!_tables[_language].TryGetValue(key, out var text) && !_tables["en"].TryGetValue(key, out text))
            return key;
        if (args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }

    public void SetLanguage(string language)
    {
        if (!_tables.ContainsKey(language)) language = "en";
        if (language == _language) return;
        _language = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRtl)));
    }

    /// <summary>Merges every embedded "*.{code}.json" table (en.json, ui.en.json, ...) into one dictionary.</summary>
    private static Dictionary<string, string> Load(string code)
    {
        var asm = typeof(Localizer).Assembly;
        var result = new Dictionary<string, string>();
        foreach (var resource in asm.GetManifestResourceNames().Where(n => n.EndsWith($".{code}.json", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n))
        {
            using var stream = asm.GetManifestResourceStream(resource)!;
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (table == null) continue;
            foreach (var kv in table) result[kv.Key] = kv.Value;
        }
        return result;
    }
}
