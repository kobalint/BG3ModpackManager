namespace BG3ModpackTools.Core.Models;

/// <summary>
/// Persisted database of all single-copy tracked mods and modpack profiles.
/// </summary>
public sealed class LibraryDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveProfileId { get; set; }
    public List<ModEntry> Mods { get; set; } = new();
    public List<ModProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Persistently links BG3ModManager load-order .json files (by normalized path and file:<FileName> key)
    /// to companion mod IDs (LooseData, LooseGame, Hybrid without meta.lsx, and StandaloneOverride PAKs)
    /// that BG3ModManager omits from its exported Order JSON.
    /// </summary>
    public Dictionary<string, List<string>> JsonOrderCompanionModIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
