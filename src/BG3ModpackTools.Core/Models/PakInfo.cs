using System.Text.Json.Serialization;

namespace BG3ModpackTools.Core.Models;

/// <summary>
/// Distinguishes how BG3ModManager handles an override package.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverrideKind
{
    /// <summary>
    /// Standard mod loaded only via modsettings.lsx / load order.
    /// </summary>
    None,

    /// <summary>
    /// Has its own mod assets plus base-game overrides; BG3MM lists it in Active Mods and load order JSON.
    /// </summary>
    MergedOverride,

    /// <summary>
    /// Loose override mod without mod-specific folders/meta; BG3MM places it in the bottom Overrides section (excluded from JSON/modsettings).
    /// </summary>
    StandaloneOverride
}

/// <summary>
/// Metadata and override analysis parsed from a Baldur's Gate 3 LSPK (.pak) archive.
/// </summary>
public sealed class PakInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public uint LspkVersion { get; set; }
    public int FileCount { get; set; }

    public bool HasMetaLsx { get; set; }
    public bool HasMetaLsf { get; set; }
    public string? Name { get; set; }
    public string? Uuid { get; set; }
    public string? Folder { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version64 { get; set; }
    public string? VersionString { get; set; }
    public string? Md5 { get; set; }
    public string? PublishHandle { get; set; }

    public bool IsDetectedOverride { get; set; }
    public OverrideKind OverrideKind { get; set; } = OverrideKind.None;
    public List<string> OverrideReasons { get; set; } = new();
    public bool HasGeneratedSharedAssets { get; set; }
    public List<string> InternalFilePaths { get; set; } = new();
    public string? ParseError { get; set; }
}
