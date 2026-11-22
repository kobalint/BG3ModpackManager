namespace BG3ModpackManager.Core.Models;

/// <summary>
/// Options controlling what a modpack profile .7z export includes and how tightly it is compressed.
/// </summary>
public sealed class ProfileExportOptions
{
    public string OutputArchivePath { get; set; } = string.Empty;
    public bool IncludePaks { get; set; } = true;
    public bool IncludeLooseMods { get; set; } = true;
    public bool IncludeGeneratedFolder { get; set; } = true;
    public HashSet<string>? SelectedGeneratedGroupKeys { get; set; }
    public bool IncludeBinMods { get; set; } = true;
    public bool IncludeOrderJson { get; set; } = true;
    public bool IncludeManifestAndReadme { get; set; } = true;
    public int CompressionLevel { get; set; } = 1;
}

/// <summary>
/// Represents a discovered subfolder inside BG3 Data\Generated\ (such as Public\Shared or a Toolkit project folder)
/// so real loose override mods are checked by default while Toolkit project folders are unchecked by default.
/// </summary>
public sealed class GeneratedExportFolderGroup
{
    public string GroupKey { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool IsSelectedByDefault { get; set; }
    public bool IsToolkitProject { get; set; }
    public List<(string FullPath, string RelativePath, long Size)> Files { get; set; } = new();
}

/// <summary>
/// Pre-flight inventory of all PAKs and loose Data/Game mods belonging to a profile prior to exporting to .7z.
/// </summary>
public sealed class ProfileExportPreview
{
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public int PakModCount { get; set; }
    public long PakTotalBytes { get; set; }
    public int LooseDataModCount { get; set; }
    public int LooseDataFileCount { get; set; }
    public long LooseDataTotalBytes { get; set; }
    public int LooseGameModCount { get; set; }
    public int LooseGameFileCount { get; set; }
    public long LooseGameTotalBytes { get; set; }
    public int LiveGeneratedFileCount { get; set; }
    public long LiveGeneratedTotalBytes { get; set; }
    public List<GeneratedExportFolderGroup> GeneratedGroups { get; set; } = new();
    public int LiveBinModCount { get; set; }
    public int LiveBinFileCount { get; set; }
    public long LiveBinTotalBytes { get; set; }
    public int MissingModCount { get; set; }
    public int MissingFileCount { get; set; }
    public List<string> Warnings { get; set; } = new();
    public bool SevenZipAvailable { get; set; }
    public string? SevenZipPath { get; set; }
}

/// <summary>
/// Per-mod metadata record written into profile_manifest.json inside an exported .7z archive.
/// </summary>
public sealed class ProfileExportedModRecord
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ModKind Kind { get; set; }
    public OverrideKind OverrideKind { get; set; }
    public string? Uuid { get; set; }
    public string? VersionString { get; set; }
    public string? PakFileName { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public List<string> DataFiles { get; set; } = new();
    public List<string> GameFiles { get; set; } = new();
    public bool IsMissing { get; set; }
}

/// <summary>
/// Top-level manifest embedded in profile_manifest.json when exporting a modpack profile to .7z.
/// </summary>
public sealed class ProfileExportManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileName { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public int CompressionLevel { get; set; }
    public int ExportedPakCount { get; set; }
    public int ExportedDataFileCount { get; set; }
    public int ExportedGameFileCount { get; set; }
    public List<ProfileExportedModRecord> Mods { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Outcome summary returned after exporting a modpack profile to a .7z archive.
/// </summary>
public sealed class ProfileExportResult
{
    public bool Success { get; set; }
    public string OutputArchivePath { get; set; } = string.Empty;
    public long ArchiveSizeBytes { get; set; }
    public int ExportedModCount { get; set; }
    public int ExportedPakCount { get; set; }
    public int ExportedDataFileCount { get; set; }
    public int ExportedGameFileCount { get; set; }
    public bool IncludedOrderJson { get; set; }
    public List<string> MissingItems { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
