namespace BG3ModpackTools.Core.Models;

/// <summary>
/// Staged loose Data/Game file belonging to an imported modpack profile item.
/// </summary>
public sealed class ModpackImportLooseFile
{
    public TrackedFileTarget Target { get; set; }
    public string StagingFullPath { get; set; } = string.Empty;
    public string DestRelativePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool WillBackupOriginal { get; set; }
    public bool MatchesExistingOnDisk { get; set; }
}

/// <summary>
/// Single mod entry (PAK, Hybrid, LooseData, or LooseGame) inspected inside an exported .7z modpack archive.
/// </summary>
public sealed class ModpackImportItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public ModKind Kind { get; set; }
    public OverrideKind OverrideKind { get; set; } = OverrideKind.None;
    public bool IsSelected { get; set; } = true;

    public string? PakArchiveRelativePath { get; set; }
    public string? PakFileName { get; set; }
    public string? PakStagingFullPath { get; set; }
    public PakInfo? PakInfo { get; set; }
    public string? PakSha256 { get; set; }

    public List<ModpackImportLooseFile> LooseFiles { get; set; } = new();
    public long TotalSizeBytes { get; set; }
    public int FileCount { get; set; }

    public string? ExistingDuplicateModId { get; set; }
    public string? ExistingDuplicateModName { get; set; }
    public string? Uuid { get; set; }
    public string? VersionString { get; set; }
    public string? CompositeHash { get; set; }

    public List<string> ExistingOriginalFilesToBackup { get; set; } = new();
    public string? Notes { get; set; }
}

/// <summary>
/// Inspected contents of an exported .7z modpack archive ready for confirmation and profile import.
/// </summary>
public sealed class ModpackImportPlan
{
    public string SourceArchivePath { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string StagingDirectory { get; set; } = string.Empty;
    public bool StagingOwnedByPlan { get; set; } = true;
    public bool IsModpackArchive { get; set; }
    public bool HasManifest { get; set; }
    public string SuggestedProfileName { get; set; } = string.Empty;
    public List<ModpackImportItem> Items { get; set; } = new();
    public List<string> OrderJsonUuids { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Options selected by the user when importing a .7z modpack archive.
/// </summary>
public sealed class ModpackImportOptions
{
    public string ProfileName { get; set; } = string.Empty;
    public bool InstallActiveNow { get; set; } = true;
    public bool SetAsActiveProfile { get; set; } = true;
}

/// <summary>
/// Outcome summary after importing a .7z modpack profile into the library and disk folders.
/// </summary>
public sealed class ModpackImportResult
{
    public ModProfile? CreatedProfile { get; set; }
    public int ImportedModCount { get; set; }
    public int ReusedModCount { get; set; }
    public int ImportedPakCount { get; set; }
    public int ImportedDataFileCount { get; set; }
    public int ImportedGameFileCount { get; set; }
    public List<string> BackedUpOriginalGameFiles { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
