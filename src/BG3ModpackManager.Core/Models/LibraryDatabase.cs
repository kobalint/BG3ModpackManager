namespace BG3ModpackManager.Core.Models;

/// <summary>
/// Persisted database of all single-copy tracked mods and modpack profiles.
/// </summary>
public sealed class LibraryDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveProfileId { get; set; }
    public List<ModEntry> Mods { get; set; } = new();
    public List<ModProfile> Profiles { get; set; } = new();
}
