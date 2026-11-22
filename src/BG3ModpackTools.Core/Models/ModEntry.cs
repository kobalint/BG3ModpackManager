using System.Text.Json.Serialization;

namespace BG3ModpackTools.Core.Models;

/// <summary>
/// Categorizes the filesystem payload managed by a mod entry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModKind
{
    Pak,
    LooseData,
    LooseGame,
    Hybrid
}

/// <summary>
/// Allows the user to override automatic override detection per mod.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualOverrideMode
{
    Auto,
    ForceOverride,
    ForceNonOverride
}

/// <summary>
/// Current active/parked state of a mod on disk.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModLocation
{
    Active,
    Vault,
    Missing,
    Partial
}

/// <summary>
/// Target root directory for loose tracked files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackedFileTarget
{
    DataFolder,
    GameFolder
}

/// <summary>
/// Represents a single loose file installed into the BG3 Data or Game directory and tracked by a mod entry.
/// </summary>
public sealed class TrackedFileRecord
{
    public TrackedFileTarget Target { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Tracks an original game file that was renamed in place (e.g. bink2w64.dll -> bink2w64.dll.bg3mpm_orig)
/// when installing a mod, so deactivating or uninstalling the mod automatically restores the original file.
/// </summary>
public sealed class ReplacedOriginalFileRecord
{
    public TrackedFileTarget Target { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string BackupRelativePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool IsCurrentlyRestored { get; set; }
}

/// <summary>
/// Single-copy canonical record of a mod in the library, keyed by content hash.
/// </summary>
public sealed class ModEntry
{
    public string Id { get; set; } = string.Empty;
    public ModKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Uuid { get; set; }
    public string? FolderName { get; set; }
    public string? Author { get; set; }
    public string? Version64 { get; set; }
    public string? VersionString { get; set; }
    public string? Md5 { get; set; }
    public string? PublishHandle { get; set; }

    public string? PakFileName { get; set; }
    public string? PakSha256 { get; set; }
    public string ContentHash { get; set; } = string.Empty;

    [JsonIgnore]
    public string? EffectivePakSha256 => !string.IsNullOrEmpty(PakSha256)
        ? PakSha256
        : (Kind == ModKind.Pak ? ContentHash : null);
    public long TotalSizeBytes { get; set; }
    public bool HasMetaLsx { get; set; }
    public bool IsDetectedOverride { get; set; }
    public OverrideKind DetectedOverrideKind { get; set; } = OverrideKind.None;
    public List<string> OverrideReasons { get; set; } = new();
    public bool HasGeneratedSharedAssets { get; set; }
    public ManualOverrideMode ManualOverrideMode { get; set; } = ManualOverrideMode.Auto;

    [JsonIgnore]
    public OverrideKind EffectiveOverrideKind => ManualOverrideMode switch
    {
        ManualOverrideMode.ForceNonOverride => OverrideKind.None,
        ManualOverrideMode.ForceOverride => DetectedOverrideKind == OverrideKind.StandaloneOverride
            ? OverrideKind.StandaloneOverride
            : OverrideKind.MergedOverride,
        _ => DetectedOverrideKind != OverrideKind.None
            ? DetectedOverrideKind
            : (IsDetectedOverride ? OverrideKind.MergedOverride : OverrideKind.None)
    };

    [JsonIgnore]
    public bool EffectiveIsOverride => EffectiveOverrideKind != OverrideKind.None;

    public List<TrackedFileRecord> TrackedFiles { get; set; } = new();
    public List<ReplacedOriginalFileRecord> ReplacedOriginalFiles { get; set; } = new();
    public bool BackupReplacedOriginals { get; set; } = true;
    public ModLocation Location { get; set; } = ModLocation.Active;
    public string? SourceArchiveName { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
}
