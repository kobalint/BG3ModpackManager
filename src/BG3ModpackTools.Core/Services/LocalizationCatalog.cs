using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// JSON file schema for built-in and community drop-in language packs (languages/*.json).
/// </summary>
public sealed class LanguagePackFile
{
    public string Code { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Metadata describing an available UI language loaded from embedded resources or external JSON packs.
/// </summary>
public sealed record LanguageInfo(
    string Code,
    string NativeName,
    string EnglishName,
    bool IsExternal,
    string? FilePath)
{
    public string DisplayName =>
        string.Equals(NativeName, EnglishName, StringComparison.OrdinalIgnoreCase)
            ? NativeName
            : $"{NativeName} ({EnglishName})";
}

/// <summary>
/// Pure .NET localization catalog loading embedded languages (en, hu) plus drop-in JSON files
/// from a configurable external folder (default: &lt;AppDir&gt;/languages/*.json).
/// </summary>
public sealed class LocalizationCatalog
{
    private const string EmbeddedPrefix = "BG3ModpackTools.Core.Languages.";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class LoadedPack
    {
        public required LanguageInfo Info { get; set; }
        public required Dictionary<string, string> Strings { get; init; }
    }

    private readonly Dictionary<string, LoadedPack> _packs = new(StringComparer.OrdinalIgnoreCase);

    public string ExternalLanguagesDirectory { get; }
    public string CurrentLanguageCode { get; private set; } = "en";
    public List<string> LoadWarnings { get; } = new();
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; private set; } = Array.Empty<LanguageInfo>();

    public LocalizationCatalog(string? externalLanguagesDirectory = null)
    {
        ExternalLanguagesDirectory = string.IsNullOrWhiteSpace(externalLanguagesDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "languages")
            : Path.GetFullPath(externalLanguagesDirectory);

        Reload();
    }

    public void Reload()
    {
        _packs.Clear();
        LoadWarnings.Clear();

        Assembly asm = typeof(LocalizationCatalog).Assembly;
        foreach (string resName in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase) &&
                                 n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using Stream? stream = asm.GetManifestResourceStream(resName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string json = reader.ReadToEnd();
            TryMergePack(json, isExternal: false, sourceLabel: resName, filePath: null);
        }

        if (Directory.Exists(ExternalLanguagesDirectory))
        {
            foreach (string file in Directory.GetFiles(ExternalLanguagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.StartsWith("template_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    TryMergePack(json, isExternal: true, sourceLabel: fileName, filePath: file);
                }
                catch (Exception ex)
                {
                    LoadWarnings.Add($"Failed to load language file '{fileName}': {ex.Message}");
                }
            }
        }

        AvailableLanguages = _packs.Values
            .Select(p => p.Info)
            .OrderBy(info => info.Code.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0
                         : info.Code.Equals("hu", StringComparison.OrdinalIgnoreCase) ? 1
                         : 2)
            .ThenBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!_packs.ContainsKey(CurrentLanguageCode))
        {
            CurrentLanguageCode = _packs.ContainsKey("en")
                ? "en"
                : AvailableLanguages.FirstOrDefault()?.Code ?? "en";
        }
    }

    public bool SetLanguage(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && _packs.TryGetValue(code.Trim(), out LoadedPack? pack))
        {
            CurrentLanguageCode = pack.Info.Code;
            return true;
        }

        CurrentLanguageCode = _packs.ContainsKey("en")
            ? "en"
            : AvailableLanguages.FirstOrDefault()?.Code ?? "en";
        return false;
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_packs.TryGetValue(CurrentLanguageCode, out LoadedPack? current) &&
            current.Strings.TryGetValue(key, out string? value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!string.Equals(CurrentLanguageCode, "en", StringComparison.OrdinalIgnoreCase) &&
            _packs.TryGetValue("en", out LoadedPack? fallback) &&
            fallback.Strings.TryGetValue(key, out string? enValue) &&
            !string.IsNullOrWhiteSpace(enValue))
        {
            return enValue;
        }

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string template = Get(key);
        if (args is null || args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentUICulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public IReadOnlyDictionary<string, string> GetStringsForLanguage(string code)
    {
        return _packs.TryGetValue(code, out LoadedPack? pack)
            ? pack.Strings
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes a canonical English language pack template (template_en.json) so anyone can create
    /// a new translation by copying it to &lt;code&gt;.json inside ExternalLanguagesDirectory.
    /// </summary>
    public string ExportEnglishTemplate(string? destinationPath = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(ExternalLanguagesDirectory, "template_en.json")
            : Path.GetFullPath(destinationPath);

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var orderedStrings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_packs.TryGetValue("en", out LoadedPack? enPack))
        {
            foreach (KeyValuePair<string, string> kv in enPack.Strings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                orderedStrings[kv.Key] = kv.Value;
            }
        }

        var template = new LanguagePackFile
        {
            Code = "en",
            NativeName = "English",
            EnglishName = "English",
            Strings = orderedStrings
        };

        string json = JsonSerializer.Serialize(template, WriteOptions);
        File.WriteAllText(targetPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return targetPath;
    }

    private void TryMergePack(string json, bool isExternal, string sourceLabel, string? filePath)
    {
        LanguagePackFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LanguagePackFile>(json, ReadOptions);
        }
        catch (Exception ex)
        {
            LoadWarnings.Add($"Invalid language pack JSON '{sourceLabel}': {ex.Message}");
            return;
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Code) || parsed.Strings is null || parsed.Strings.Count == 0)
        {
            LoadWarnings.Add($"Skipped language file '{sourceLabel}': missing 'code' or 'strings'.");
            return;
        }

        string code = parsed.Code.Trim();
        string native = string.IsNullOrWhiteSpace(parsed.NativeName) ? code : parsed.NativeName.Trim();
        string english = string.IsNullOrWhiteSpace(parsed.EnglishName) ? native : parsed.EnglishName.Trim();

        if (_packs.TryGetValue(code, out LoadedPack? existing))
        {
            foreach (KeyValuePair<string, string> kv in parsed.Strings)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    existing.Strings[kv.Key] = kv.Value;
                }
            }

            if (isExternal)
            {
                existing.Info = new LanguageInfo(code, native, english, IsExternal: true, filePath);
            }

            return;
        }

        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kv in parsed.Strings)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                strings[kv.Key] = kv.Value;
            }
        }

        _packs[code] = new LoadedPack
        {
            Info = new LanguageInfo(code, native, english, isExternal, filePath),
            Strings = strings
        };
    }
}
