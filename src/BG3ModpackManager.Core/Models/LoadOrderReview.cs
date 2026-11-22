using System.Text.Json.Serialization;

namespace BG3ModpackManager.Core.Models;

/// <summary>
/// Single entry inside a BG3ModManager exported load order JSON file.
/// </summary>
public sealed class Bg3OrderEntry
{
    [JsonPropertyName("UUID")]
    public string Uuid { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Root object of a BG3ModManager exported load order JSON file.
/// </summary>
public sealed class Bg3LoadOrderFile
{
    [JsonPropertyName("Order")]
    public List<Bg3OrderEntry> Order { get; set; } = new();
}

/// <summary>
/// Status of a mod when cross-checking a BG3ModManager JSON against the live Mods folder and Vault.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewStatus
{
    Active,
    ActiveOverride,
    Bg3MmOverride,
    InVault,
    Missing,
    UnlistedOverride,
    UnlistedInactive
}

/// <summary>
/// Row in the Load Order Review audit report.
/// </summary>
public sealed class LoadOrderReviewItem
{
    public int? OrderIndex { get; set; }
    public string JsonName { get; set; } = string.Empty;
    public string JsonUuid { get; set; } = string.Empty;
    public string? MatchedModId { get; set; }
    public string? PakFileName { get; set; }
    public string? PakFullPath { get; set; }
    public string? PakName { get; set; }
    public string? PakUuid { get; set; }
    public string? VersionString { get; set; }
    public long FileSizeBytes { get; set; }
    public bool IsOverride { get; set; }
    public OverrideKind OverrideKind { get; set; } = OverrideKind.None;
    public List<string> OverrideReasons { get; set; } = new();
    public ReviewStatus Status { get; set; }
    public string? MultipleVariantsNote { get; set; }
}

/// <summary>
/// Full cross-check audit report for a BG3ModManager load order JSON against the Mods folder and modsettings.lsx.
/// </summary>
public sealed class LoadOrderAuditReport
{
    public string JsonFilePath { get; set; } = string.Empty;
    public List<LoadOrderReviewItem> Items { get; set; } = new();
    public int JsonTotalCount { get; set; }
    public int ActiveCount { get; set; }
    public int ActiveOverrideCount { get; set; }
    public int Bg3MmOverrideCount { get; set; }
    public int InVaultCount { get; set; }
    public int MissingCount { get; set; }
    public int UnlistedOverrideCount { get; set; }
    public int UnlistedInactiveCount { get; set; }
    public bool? ModSettingsLsxMatchesJson { get; set; }
    public string ModSettingsLsxSummary { get; set; } = string.Empty;
}
