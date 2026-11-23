using System.Text.Json;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Discovers Baldur's Gate 3 folders and BG3ModManager settings on Windows.
/// </summary>
public static class PathAutoDetector
{
    public static AppSettings DetectDefaults(AppSettings? existing = null)
    {
        bool detectLaunchRenderer = existing is null;
        var settings = existing is null
            ? new AppSettings
            {
                SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion
            }
            : new AppSettings
            {
                SettingsSchemaVersion = existing.SettingsSchemaVersion,
                ModsFolder = existing.ModsFolder,
                GameFolder = existing.GameFolder,
                DataFolder = existing.DataFolder,
                OrdersFolder = existing.OrdersFolder,
                VaultFolder = existing.VaultFolder,
                SwitchMode = existing.SwitchMode,
                SyncModSettingsLsxOnSwitch = existing.SyncModSettingsLsxOnSwitch,
                ModSettingsLsxPath = existing.ModSettingsLsxPath,
                LastSelectedOrderJsonPath = existing.LastSelectedOrderJsonPath,
                LastExportFolder = existing.LastExportFolder,
                Theme = existing.Theme,
                LanguageCode = string.IsNullOrWhiteSpace(existing.LanguageCode) ? "en" : existing.LanguageCode.Trim(),
                LaunchRenderer = existing.LaunchRenderer,
                CheckUpdatesOnStartup = existing.CheckUpdatesOnStartup
            };

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string bg3UserRoot = Path.Combine(localAppData, "Larian Studios", "Baldur's Gate 3");

        if (string.IsNullOrWhiteSpace(settings.ModsFolder))
        {
            string modsCandidate = Path.Combine(bg3UserRoot, "Mods");
            if (Directory.Exists(modsCandidate) || Directory.Exists(bg3UserRoot))
            {
                settings.ModsFolder = modsCandidate;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.ModSettingsLsxPath))
        {
            string lsxCandidate = Path.Combine(bg3UserRoot, "PlayerProfiles", "Public", "modsettings.lsx");
            if (File.Exists(lsxCandidate) || Directory.Exists(bg3UserRoot))
            {
                settings.ModSettingsLsxPath = lsxCandidate;
            }
        }

        TryPopulateFromBg3ModManager(settings, localAppData, detectLaunchRenderer);

        if (string.IsNullOrWhiteSpace(settings.DataFolder) && !string.IsNullOrWhiteSpace(settings.GameFolder))
        {
            string dataCandidate = Path.Combine(settings.GameFolder, "Data");
            if (Directory.Exists(dataCandidate))
            {
                settings.DataFolder = dataCandidate;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.GameFolder) && !string.IsNullOrWhiteSpace(settings.DataFolder))
        {
            string? parent = Path.GetDirectoryName(settings.DataFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                settings.GameFolder = parent;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.GameFolder))
        {
            foreach (string candidate in GetCommonGameFolderCandidates())
            {
                if (Directory.Exists(Path.Combine(candidate, "Data")))
                {
                    settings.GameFolder = candidate;
                    if (string.IsNullOrWhiteSpace(settings.DataFolder))
                    {
                        settings.DataFolder = Path.Combine(candidate, "Data");
                    }

                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(settings.VaultFolder))
        {
            settings.VaultFolder = Path.Combine(AppContext.BaseDirectory, "Vault");
        }

        return settings;
    }

    private static void TryPopulateFromBg3ModManager(AppSettings settings, string localAppData, bool detectLaunchRenderer)
    {
        foreach (string mmRoot in GetBg3MmCandidates(localAppData))
        {
            string mmSettingsPath = Path.Combine(mmRoot, "Data", "settings.json");
            if (!File.Exists(mmSettingsPath))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(mmSettingsPath));
                JsonElement root = doc.RootElement;

                if (string.IsNullOrWhiteSpace(settings.DataFolder) &&
                    root.TryGetProperty("GameDataPath", out JsonElement dataPathEl) &&
                    dataPathEl.ValueKind == JsonValueKind.String)
                {
                    string? p = dataPathEl.GetString();
                    if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    {
                        settings.DataFolder = p;
                    }
                }

                if (root.TryGetProperty("GameExecutablePath", out JsonElement exePathEl) &&
                    exePathEl.ValueKind == JsonValueKind.String)
                {
                    string? exe = exePathEl.GetString();
                    if (detectLaunchRenderer && !string.IsNullOrWhiteSpace(exe))
                    {
                        string fileName = Path.GetFileName(exe.Trim());
                        if (string.Equals(fileName, "bg3.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.LaunchRenderer = Bg3LaunchRenderer.Vulkan;
                        }
                        else if (string.Equals(fileName, "bg3_dx11.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.LaunchRenderer = Bg3LaunchRenderer.DirectX11;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(settings.GameFolder))
                    {
                        string? binDir = !string.IsNullOrWhiteSpace(exe) ? Path.GetDirectoryName(exe) : null;
                        string? gameDir = !string.IsNullOrWhiteSpace(binDir) ? Path.GetDirectoryName(binDir) : null;
                        if (!string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
                        {
                            settings.GameFolder = gameDir;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(settings.OrdersFolder))
                {
                    if (root.TryGetProperty("LastLoadedOrderFilePath", out JsonElement ordersEl) &&
                        ordersEl.ValueKind == JsonValueKind.String)
                    {
                        string? p = ordersEl.GetString();
                        if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                        {
                            settings.OrdersFolder = p;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(settings.OrdersFolder))
                    {
                        string ordersDir = Path.Combine(mmRoot, "Orders");
                        if (Directory.Exists(ordersDir))
                        {
                            settings.OrdersFolder = ordersDir;
                        }
                    }
                }

                break;
            }
            catch
            {
                // Ignore malformed third-party settings files and continue fallback probing.
            }
        }
    }

    private static IEnumerable<string> GetBg3MmCandidates(string localAppData)
    {
        var list = new List<string>
        {
            @"C:\GOG Games\BG3ModManager_Latest",
            @"C:\GOG Games\BG3ModManager",
            Path.Combine(localAppData, "BG3ModManager")
        };

        string[] scanRoots =
        {
            @"C:\GOG Games",
            @"C:\Modding",
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        foreach (string root in scanRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root, "BG3ModManager*"))
                {
                    list.Add(dir);
                }
            }
            catch
            {
                // Skip inaccessible folders.
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetCommonGameFolderCandidates()
    {
        return new[]
        {
            @"C:\Modding\GOGGalaxy\Baldurs Gate 3",
            @"C:\GOG Games\Baldurs Gate 3",
            @"C:\GOG Games\Baldur's Gate 3",
            @"C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3",
            @"C:\Program Files (x86)\GOG Galaxy\Games\Baldurs Gate 3",
            @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3",
            @"D:\GOG Games\Baldurs Gate 3"
        };
    }
}
