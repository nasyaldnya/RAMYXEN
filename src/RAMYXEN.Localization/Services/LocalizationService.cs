using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RAMYXEN.Localization.Services;

public sealed record LanguageMetadata(
    string Locale,
    string NativeName,
    string EnglishName,
    string Direction,
    string? FallbackLocale,
    string Availability,
    IReadOnlyList<string> PluralCategories,
    double CompletenessPercent);

public interface ILocalizationService : INotifyPropertyChanged
{
    string CurrentLocale { get; }
    bool IsRightToLeft { get; }
    CultureInfo CurrentCulture { get; }
    IReadOnlyList<LanguageMetadata> AvailableLanguages { get; }
    event EventHandler<string>? LanguageChanged;

    string GetString(string key);
    string Format(string key, IReadOnlyDictionary<string, object?> parameters);
    string FormatPlural(string key, int count, IReadOnlyDictionary<string, object?>? extraParameters = null);
    string IsolateTechnicalToken(string technicalValue);
    bool TrySetLanguage(string locale);
    void ReloadLanguagePacks();
}

public sealed class LocalizationService : ILocalizationService
{
    private const long MaxPackBytes = 5 * 1024 * 1024;
    private static readonly Regex KeyPattern = new(@"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$", RegexOptions.Compiled);
    private static readonly Regex BidiOverridePattern = new(@"[\u202A-\u202E]", RegexOptions.Compiled);

    private readonly string _bundledLocRoot;
    private readonly string _userOverrideLocRoot;
    private readonly object _sync = new();

    private Dictionary<string, Dictionary<string, string>> _packs = new(StringComparer.OrdinalIgnoreCase);
    private List<LanguageMetadata> _languages = [];
    private string _currentLocale = "en-US";
    private bool _isRtl;
    private CultureInfo _currentCulture = new("en-US");

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    public string CurrentLocale => _currentLocale;
    public bool IsRightToLeft => _isRtl;
    public CultureInfo CurrentCulture => _currentCulture;
    public IReadOnlyList<LanguageMetadata> AvailableLanguages => _languages;

    public LocalizationService(string? bundledLocalizationRoot = null, string? userOverrideRoot = null)
    {
        _bundledLocRoot = bundledLocalizationRoot ?? ResolveDefaultBundledRoot();
        _userOverrideLocRoot = userOverrideRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAMYXEN", "localization");

        ReloadLanguagePacks();
    }

    private static string ResolveDefaultBundledRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "localization");
        if (Directory.Exists(candidate)) return candidate;

        // Walk up during local development/test runs
        DirectoryInfo? dir = new(baseDir);
        while (dir != null)
        {
            string test = Path.Combine(dir.FullName, "localization");
            if (Directory.Exists(test) && File.Exists(Path.Combine(test, "languages.json")))
                return test;
            dir = dir.Parent;
        }
        return candidate;
    }

    public void ReloadLanguagePacks()
    {
        lock (_sync)
        {
            var newPacks = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var rawMeta = LoadCatalogueMetadata();

            // Load canonical en-US first
            var canonical = LoadSafeLocalePack("en-US");
            if (canonical.Count > 0)
                newPacks["en-US"] = canonical;

            int canonicalCount = Math.Max(1, canonical.Count);
            var computedList = new List<LanguageMetadata>();

            foreach (var meta in rawMeta)
            {
                var pack = meta.Locale.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                    ? canonical
                    : LoadSafeLocalePack(meta.Locale);

                if (pack.Count > 0)
                {
                    newPacks[meta.Locale] = pack;
                    int translated = canonical.Keys.Count(k => pack.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));
                    double pct = Math.Round((double)translated / canonicalCount * 100.0, 1);
                    computedList.Add(meta with { CompletenessPercent = pct });
                }
            }

            if (computedList.Count == 0)
            {
                computedList.Add(new LanguageMetadata("en-US", "English", "English (United States)", "ltr", null, "official", ["one", "other"], 100.0));
            }

            _packs = newPacks;
            _languages = computedList;

            if (!_languages.Any(l => l.Locale.Equals(_currentLocale, StringComparison.OrdinalIgnoreCase)))
                _currentLocale = "en-US";

            UpdateCultureAndDirection(_currentLocale);
        }

        NotifyLanguageChanged();
    }

    public bool TrySetLanguage(string locale)
    {
        lock (_sync)
        {
            var match = _languages.FirstOrDefault(l => l.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;

            _currentLocale = match.Locale;
            UpdateCultureAndDirection(match.Locale);
        }

        NotifyLanguageChanged();
        return true;
    }

    private void UpdateCultureAndDirection(string locale)
    {
        var meta = _languages.FirstOrDefault(l => l.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase));
        _isRtl = string.Equals(meta?.Direction, "rtl", StringComparison.OrdinalIgnoreCase);
        try
        {
            _currentCulture = new CultureInfo(locale);
        }
        catch
        {
            _currentCulture = CultureInfo.InvariantCulture;
        }
    }

    private void NotifyLanguageChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLocale)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, _currentLocale);
    }

    public string this[string key] => GetString(key);

    public string GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        lock (_sync)
        {
            // 1. Selected Locale
            if (_packs.TryGetValue(_currentLocale, out var activePack) &&
                activePack.TryGetValue(key, out var val) &&
                !string.IsNullOrWhiteSpace(val))
            {
                return val;
            }

            // 2. Configured Parent Fallback Locale
            var meta = _languages.FirstOrDefault(l => l.Locale.Equals(_currentLocale, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(meta?.FallbackLocale) &&
                _packs.TryGetValue(meta.FallbackLocale, out var fallbackPack) &&
                fallbackPack.TryGetValue(key, out var fbVal) &&
                !string.IsNullOrWhiteSpace(fbVal))
            {
                return fbVal;
            }

            // 3. Canonical en-US
            if (_packs.TryGetValue("en-US", out var enPack) &&
                enPack.TryGetValue(key, out var enVal) &&
                !string.IsNullOrWhiteSpace(enVal))
            {
                return enVal;
            }

            return $"[missing:{key}]";
        }
    }

    public string Format(string key, IReadOnlyDictionary<string, object?> parameters)
    {
        string template = GetString(key);
        foreach (var kv in parameters)
        {
            string replacement = kv.Value switch
            {
                IFormattable f => f.ToString(null, _currentCulture),
                null => string.Empty,
                _ => kv.Value.ToString() ?? string.Empty
            };
            template = template.Replace($"{{{kv.Key}}}", replacement, StringComparison.Ordinal);
        }
        return template;
    }

    public string FormatPlural(string key, int count, IReadOnlyDictionary<string, object?>? extraParameters = null)
    {
        string raw = GetString(key);
        string category = GetCldrPluralCategory(_currentLocale, count);
        string countFormatted = count.ToString("N0", _currentCulture);

        // Preserve any prefix/suffix outside {var, plural, ...} and support nested {placeholders} inside branches (F-043)
        string result = ResolveIcuPluralBlock(raw, category, countFormatted);

        if (extraParameters != null)
        {
            foreach (var kv in extraParameters)
            {
                string val = kv.Value switch
                {
                    IFormattable f => f.ToString(null, _currentCulture),
                    null => string.Empty,
                    _ => kv.Value.ToString() ?? string.Empty
                };
                result = result.Replace($"{{{kv.Key}}}", val, StringComparison.Ordinal);
            }
        }
        return result;
    }

    /// <summary>
    /// Normalizes Arabic orthographic variants (Alef forms, Teh Marbuta, Alef Maksura, diacritics, tatweel)
    /// for accurate case- and diacritic-insensitive search across Command Palette and Process lists (F-041).
    /// </summary>
    public static string NormalizeForSearch(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            // Strip Arabic harakat / diacritics (U+064B-U+065F, U+0670) and tatweel (U+0640)
            if ((c >= '\u064B' && c <= '\u065F') || c == '\u0670' || c == '\u0640')
                continue;

            char mapped = c switch
            {
                '\u0622' or '\u0623' or '\u0625' or '\u0671' => '\u0627', // آ أ إ ٱ -> ا
                '\u0629' => '\u0647', // ة -> ه
                '\u0649' => '\u064A', // ى -> ي
                _ => char.ToLowerInvariant(c)
            };
            sb.Append(mapped);
        }
        return sb.ToString();
    }

    public string IsolateTechnicalToken(string technicalValue)
    {
        if (string.IsNullOrEmpty(technicalValue)) return string.Empty;
        // Wrap in Unicode LRI (U+2066) + LRM (U+200E) ... LRM (U+200E) + PDI (U+2069) when RTL is active
        // so DirectWrite contextual digit substitution never scrambles numeric+unit+percentage groups (F-008)
        return _isRtl ? $"\u2066\u200E{technicalValue}\u200E\u2069" : technicalValue;
    }

    public static string GetCldrPluralCategory(string locale, int n)
    {
        int abs = Math.Abs(n);
        if (locale.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
        {
            if (abs == 0) return "zero";
            if (abs == 1) return "one";
            if (abs == 2) return "two";
            int mod100 = abs % 100;
            if (mod100 >= 3 && mod100 <= 10) return "few";
            if (mod100 >= 11 && mod100 <= 99) return "many";
            return "other";
        }
        return abs == 1 ? "one" : "other";
    }

    private static string ResolveIcuPluralBlock(string icuMessage, string category, string countFormatted)
    {
        var header = Regex.Match(icuMessage, @"\{\s*([a-zA-Z0-9_]+)\s*,\s*plural\s*,");
        if (!header.Success)
        {
            return icuMessage.Replace("#", countFormatted, StringComparison.Ordinal)
                             .Replace("{count}", countFormatted, StringComparison.Ordinal);
        }

        int blockStart = header.Index;
        int bodyStart = header.Index + header.Length;
        int depth = 1;
        int blockEnd = -1;
        for (int i = bodyStart; i < icuMessage.Length; i++)
        {
            if (icuMessage[i] == '{') depth++;
            else if (icuMessage[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    blockEnd = i;
                    break;
                }
            }
        }

        if (blockEnd < 0)
            return icuMessage.Replace("#", countFormatted, StringComparison.Ordinal);

        string prefix = icuMessage[..blockStart];
        string body = icuMessage[bodyStart..blockEnd];
        string suffix = icuMessage[(blockEnd + 1)..];

        var branches = ParsePluralBranches(body);
        string chosen = branches.TryGetValue(category, out var exact)
            ? exact
            : (branches.TryGetValue("other", out var other) ? other : body);

        string renderedBranch = chosen.Replace("#", countFormatted, StringComparison.Ordinal)
                                      .Replace("{count}", countFormatted, StringComparison.Ordinal);
        return prefix + renderedBranch + suffix;
    }

    private static Dictionary<string, string> ParsePluralBranches(string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length) break;

            int keyStart = i;
            while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] != '{') i++;
            string key = body[keyStart..i].Trim();

            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length || body[i] != '{') break;

            i++; // skip '{'
            int valStart = i;
            int depth = 1;
            while (i < body.Length && depth > 0)
            {
                if (body[i] == '{') depth++;
                else if (body[i] == '}') depth--;
                if (depth > 0) i++;
            }
            string value = body[valStart..i];
            if (i < body.Length && body[i] == '}') i++;

            if (!string.IsNullOrEmpty(key))
                map[key] = value;
        }
        return map;
    }

    private List<LanguageMetadata> LoadCatalogueMetadata()
    {
        string userCat = Path.Combine(_userOverrideLocRoot, "languages.json");
        string bundledCat = Path.Combine(_bundledLocRoot, "languages.json");
        string? targetPath = File.Exists(userCat) ? userCat : File.Exists(bundledCat) ? bundledCat : null;

        var list = new List<LanguageMetadata>();
        if (targetPath == null) return list;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(targetPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in langs.EnumerateArray())
                {
                    string locale = el.GetProperty("locale").GetString() ?? "";
                    string native = el.GetProperty("nativeName").GetString() ?? locale;
                    string english = el.GetProperty("englishName").GetString() ?? locale;
                    string dir = el.GetProperty("direction").GetString() ?? "ltr";
                    string? fallback = el.TryGetProperty("fallbackLocale", out var fb) && fb.ValueKind == JsonValueKind.String ? fb.GetString() : null;
                    string avail = el.TryGetProperty("availability", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString()! : "official";
                    var plurals = new List<string>();
                    if (el.TryGetProperty("pluralCategories", out var pc) && pc.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pc.EnumerateArray())
                            if (p.GetString() is string s) plurals.Add(s);
                    }
                    if (!string.IsNullOrWhiteSpace(locale))
                        list.Add(new LanguageMetadata(locale, native, english, dir, fallback, avail, plurals, 0.0));
                }
            }
        }
        catch
        {
            // Safe fallback if external languages.json is malformed
        }
        return list;
    }

    private Dictionary<string, string> LoadSafeLocalePack(string locale)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string bundledPath = Path.Combine(_bundledLocRoot, "locales", $"{locale}.json");
        string overridePath = Path.Combine(_userOverrideLocRoot, "locales", $"{locale}.json");

        // Load bundled first, then overlay user override if valid
        if (File.Exists(bundledPath))
            MergeValidatedJsonPack(bundledPath, result);

        if (File.Exists(overridePath))
            MergeValidatedJsonPack(overridePath, result);

        return result;
    }

    private static void MergeValidatedJsonPack(string filePath, Dictionary<string, string> target)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxPackBytes) return;

            string raw = File.ReadAllText(filePath, Encoding.UTF8);
            if (raw.Contains('\uFFFD') || BidiOverridePattern.IsMatch(raw)) return;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!KeyPattern.IsMatch(prop.Name)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                string? val = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(val) && val.Length <= 2000)
                {
                    target[prop.Name] = val;
                }
            }
        }
        catch
        {
            // Untrusted/malformed pack rejected safely without throwing
        }
    }
}
