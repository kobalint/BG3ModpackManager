namespace BG3ModpackTools.Core.Models;

/// <summary>
/// Ordered collection of mod references belonging to a named modpack profile.
/// </summary>
public sealed class ModProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LinkedOrderJsonPath { get; set; }
    public DateTime? LastSyncedFromJsonUtc { get; set; }
    public List<string> ModIds { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
