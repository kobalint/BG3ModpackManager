using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static partial class LocalizationTests
{
    [GeneratedRegex(@"\{(\d+)(?:[^}]*)\}")]
    private static partial Regex PlaceholderRegex();

    public static void RunAll()
    {
        EmbeddedLanguagesLoadWithZeroWarningsAndFullParity();
        ExternalLanguagePackExtendsAndFallsBackToEnglish();
        ExternalLanguagePackCanOverrideBuiltInStrings();
        TemplateExportProducesValidUtf8RoundTripPack();
        PathAutoDetector_PreservesLanguageThemeAndExportFolder_AndDefaultsToEnglish();
    }

    private static void EmbeddedLanguagesLoadWithZeroWarningsAndFullParity()
    {
        using var sandbox = new TempSandbox();
        string emptyLangDir = Path.Combine(sandbox.Root, "langs_empty");
        var catalog = new LocalizationCatalog(emptyLangDir);

        TestAssert.Equal(0, catalog.LoadWarnings.Count, "Embedded language packs should load with zero warnings");
        TestAssert.True(catalog.AvailableLanguages.Any(l => l.Code == "en"), "English pack must be available");
        TestAssert.True(catalog.AvailableLanguages.Any(l => l.Code == "hu"), "Hungarian pack must be available");

        IReadOnlyDictionary<string, string> en = catalog.GetStringsForLanguage("en");
        IReadOnlyDictionary<string, string> hu = catalog.GetStringsForLanguage("hu");

        TestAssert.True(en.Count > 150, "English catalog should contain complete UI strings");

        var missingInHu = en.Keys.Where(k => !hu.ContainsKey(k)).ToList();
        var extraInHu = hu.Keys.Where(k => !en.ContainsKey(k)).ToList();
        TestAssert.Equal(0, missingInHu.Count, $"Keys missing in hu.json: {string.Join(", ", missingInHu)}");
        TestAssert.Equal(0, extraInHu.Count, $"Unexpected extra keys in hu.json: {string.Join(", ", extraInHu)}");

        foreach ((string key, string enVal) in en)
        {
            TestAssert.False(string.IsNullOrWhiteSpace(enVal), $"Empty English string for key '{key}'");
            string huVal = hu[key];
            TestAssert.False(string.IsNullOrWhiteSpace(huVal), $"Empty Hungarian string for key '{key}'");

            var enPlaceholders = ExtractPlaceholderIndexes(enVal);
            var huPlaceholders = ExtractPlaceholderIndexes(huVal);
            TestAssert.True(
                enPlaceholders.SetEquals(huPlaceholders),
                $"Format placeholder mismatch on key '{key}': en=[{string.Join(",", enPlaceholders)}] vs hu=[{string.Join(",", huPlaceholders)}]");
        }
    }

    private static void ExternalLanguagePackExtendsAndFallsBackToEnglish()
    {
        using var sandbox = new TempSandbox();
        string langDir = Path.Combine(sandbox.Root, "languages");
        Directory.CreateDirectory(langDir);

        var germanPack = new
        {
            code = "de",
            nativeName = "Deutsch",
            englishName = "German",
            strings = new Dictionary<string, string>
            {
                ["Nav.Profiles"] = "Profile",
                ["App.SidebarStatsFormat"] = "{0} Mods • {1} Profile"
            }
        };

        File.WriteAllText(
            Path.Combine(langDir, "de.json"),
            JsonSerializer.Serialize(germanPack),
            Encoding.UTF8);

        var catalog = new LocalizationCatalog(langDir);
        TestAssert.Equal(0, catalog.LoadWarnings.Count, "Valid external de.json should load cleanly");
        TestAssert.True(catalog.AvailableLanguages.Any(l => l.Code == "de" && l.IsExternal), "External German pack discovered");

        TestAssert.True(catalog.SetLanguage("de"), "Selecting 'de' succeeds");
        TestAssert.Equal("Profile", catalog.Get("Nav.Profiles"), "Returns translated German value");
        TestAssert.Equal("5 Mods • 2 Profile", catalog.Format("App.SidebarStatsFormat", 5, 2), "Formats German string properly");
        TestAssert.Equal("Settings", catalog.Get("Nav.Settings"), "Untranslated key falls back to English");
    }

    private static void ExternalLanguagePackCanOverrideBuiltInStrings()
    {
        using var sandbox = new TempSandbox();
        string langDir = Path.Combine(sandbox.Root, "languages");
        Directory.CreateDirectory(langDir);

        var customHu = new
        {
            code = "hu",
            nativeName = "Magyar",
            englishName = "Hungarian",
            strings = new Dictionary<string, string>
            {
                ["Nav.Settings"] = "Egyéni beállítások"
            }
        };

        File.WriteAllText(
            Path.Combine(langDir, "hu.json"),
            JsonSerializer.Serialize(customHu),
            Encoding.UTF8);

        var catalog = new LocalizationCatalog(langDir);
        TestAssert.True(catalog.SetLanguage("hu"), "Selecting 'hu' succeeds");
        TestAssert.Equal("Egyéni beállítások", catalog.Get("Nav.Settings"), "External hu.json overrides targeted key");
        TestAssert.Equal("Profilok", catalog.Get("Nav.Profiles"), "Remaining embedded Hungarian keys stay intact");
    }

    private static void TemplateExportProducesValidUtf8RoundTripPack()
    {
        using var sandbox = new TempSandbox();
        string langDir = Path.Combine(sandbox.Root, "languages");
        var catalog = new LocalizationCatalog(langDir);

        string templatePath = catalog.ExportEnglishTemplate();
        TestAssert.True(File.Exists(templatePath), "template_en.json should be created");

        // template_*.json must NOT show up as a duplicate language when reloaded
        catalog.Reload();
        TestAssert.Equal(2, catalog.AvailableLanguages.Count, "template_en.json ignored during scan");

        // Copying template to a new code creates a usable language pack
        string content = File.ReadAllText(templatePath, Encoding.UTF8);
        LanguagePackFile? parsed = JsonSerializer.Deserialize<LanguagePackFile>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        TestAssert.True(parsed is not null && parsed.Strings.Count > 150, "Template round-trips all strings cleanly");
    }

    private static void PathAutoDetector_PreservesLanguageThemeAndExportFolder_AndDefaultsToEnglish()
    {
        using var sandbox = new TempSandbox();
        string customExport = Path.Combine(sandbox.Root, "exports");

        AppSettings fresh = PathAutoDetector.DetectDefaults(null);
        TestAssert.Equal("en", fresh.LanguageCode, "Fresh settings default LanguageCode to English ('en')");
        TestAssert.Equal(AppTheme.Dark, fresh.Theme, "Fresh settings default Theme to Dark");

        var existing = new AppSettings
        {
            ModsFolder = sandbox.Settings.ModsFolder,
            GameFolder = sandbox.Settings.GameFolder,
            DataFolder = sandbox.Settings.DataFolder,
            OrdersFolder = sandbox.Settings.OrdersFolder,
            VaultFolder = sandbox.Settings.VaultFolder,
            LastExportFolder = customExport,
            Theme = AppTheme.Light,
            LanguageCode = "hu"
        };

        AppSettings preserved = PathAutoDetector.DetectDefaults(existing);
        TestAssert.Equal("hu", preserved.LanguageCode, "LanguageCode 'hu' must survive PathAutoDetector.DetectDefaults");
        TestAssert.Equal(AppTheme.Light, preserved.Theme, "AppTheme.Light must survive PathAutoDetector.DetectDefaults");
        TestAssert.Equal(customExport, preserved.LastExportFolder, "LastExportFolder must survive PathAutoDetector.DetectDefaults");

        existing.LanguageCode = "   ";
        AppSettings blankFallback = PathAutoDetector.DetectDefaults(existing);
        TestAssert.Equal("en", blankFallback.LanguageCode, "Whitespace LanguageCode falls back to 'en'");
    }

    private static HashSet<int> ExtractPlaceholderIndexes(string text)
    {
        var result = new HashSet<int>();
        foreach (Match match in PlaceholderRegex().Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, out int idx))
            {
                result.Add(idx);
            }
        }

        return result;
    }
}
