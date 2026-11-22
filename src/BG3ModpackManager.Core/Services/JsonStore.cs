using System.Text.Json;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.Core.Services;

/// <summary>
/// Persists application settings, the single-copy mod library, and the PAK scan cache using atomic JSON writes.
/// </summary>
public sealed class JsonStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LibraryPath => Path.Combine(DataDirectory, "library.json");
    public string CachePath => Path.Combine(DataDirectory, "pak-cache.json");

    public JsonStore(string? dataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.GetFullPath(dataDirectory);
    }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings
            {
                SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion,
                SyncModSettingsLsxOnSwitch = true
            };
        }

        AppSettings loaded = LoadOrDefault(SettingsPath, static () => new AppSettings());
        if (loaded.SettingsSchemaVersion < AppSettings.CurrentSettingsSchemaVersion)
        {
            loaded.SyncModSettingsLsxOnSwitch = true;
            loaded.SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion;
            SaveSettings(loaded);
        }

        return loaded;
    }

    public void SaveSettings(AppSettings settings)
    {
        SaveAtomic(SettingsPath, settings);
    }

    public LibraryDatabase LoadLibrary()
    {
        return LoadOrDefault(LibraryPath, static () => new LibraryDatabase());
    }

    public void SaveLibrary(LibraryDatabase library)
    {
        SaveAtomic(LibraryPath, library);
    }

    public PakMetadataCache LoadCache()
    {
        PakMetadataCache cache = LoadOrDefault(CachePath, static () => new PakMetadataCache());
        if (cache.SchemaVersion < 2)
        {
            return new PakMetadataCache { SchemaVersion = 2 };
        }

        return cache;
    }

    public void SaveCache(PakMetadataCache cache)
    {
        SaveAtomic(CachePath, cache);
    }

    private static T LoadOrDefault<T>(string path, Func<T> factory) where T : class
    {
        if (!File.Exists(path))
        {
            return factory();
        }

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return factory();
        }

        return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? factory();
    }

    private static void SaveAtomic<T>(string path, T data)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = path + ".tmp";
        string json = JsonSerializer.Serialize(data, SerializerOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }
}
