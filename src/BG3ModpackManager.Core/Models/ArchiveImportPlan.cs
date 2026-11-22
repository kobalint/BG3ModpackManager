using System.Text.Json.Serialization;

namespace BG3ModpackManager.Core.Models;

/// <summary>
/// Destination bucket classified for an item inside a dropped archive or folder.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportTargetKind
{
    ModsPak,
    DataFolder,
    GameFolder,
    Ignore
}

/// <summary>
/// Candidate file or folder tree discovered inside a dropped mod archive or directory.
/// </summary>
public sealed class ImportItemCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string ArchiveRelativePath { get; set; } = string.Empty;
    public ImportTargetKind TargetKind { get; set; }
    public string DestinationRelativePath { get; set; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public long FileSizeBytes { get; set; }
    public int FileCount { get; set; } = 1;
    public string StagingFullPath { get; set; } = string.Empty;
    public PakInfo? PakInfo { get; set; }
    public string? ContentHash { get; set; }
    public string? ExistingDuplicateModId { get; set; }
    public string? ExistingDuplicateModName { get; set; }
    public bool OverwritesUnmanagedFile { get; set; }
    public string? WarningMessage { get; set; }
    public List<string> ContainedRelativePaths { get; set; } = new();
}

/// <summary>
/// Inspected staging plan for importing a dropped archive, .pak, or folder.
/// </summary>
public sealed class ArchiveImportPlan
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string StagingDirectory { get; set; } = string.Empty;
    public bool StagingOwnedByPlan { get; set; } = true;
    public List<ImportItemCandidate> Items { get; set; } = new();
    public bool PackageAsSingleHybridMod { get; set; } = true;
    public string? CustomDisplayName { get; set; }
}

/// <summary>
/// Result of committing an archive import plan into active folders or the Vault.
/// </summary>
public sealed class ArchiveImportResult
{
    public List<string> ImportedModIds { get; set; } = new();
    public List<string> ReusedDuplicateModIds { get; set; } = new();
    public List<string> BackedUpUnmanagedFiles { get; set; } = new();
    public List<string> RenamedOriginalFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
