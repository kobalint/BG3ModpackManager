using System.Text.Json.Serialization;

namespace BG3ModpackTools.Core.Models;

/// <summary>
/// Planned operation for a mod during a profile switch.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SwitchActionType
{
    StayInPlace,
    MoveToVault,
    ActivateFromVault,
    AdoptUntrackedToProfile,
    AdoptUntrackedToVault,
    MissingMod,
    FileConflict
}

/// <summary>
/// Single mod or conflict entry in a profile switch dry-run plan.
/// </summary>
public sealed class SwitchActionItem
{
    public SwitchActionType ActionType { get; set; }
    public string? ModId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? PakFileName { get; set; }
    public int TrackedFileCount { get; set; }
    public long SizeBytes { get; set; }
    public bool IsOverride { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public string? DestinationPath { get; set; }
}

/// <summary>
/// Dry-run plan describing every file move, shared mod kept in place, missing mod, or conflict before switching profiles.
/// </summary>
public sealed class SwitchPlan
{
    public string TargetProfileId { get; set; } = string.Empty;
    public string TargetProfileName { get; set; } = string.Empty;
    public SwitchMode SwitchMode { get; set; }
    public List<SwitchActionItem> Actions { get; set; } = new();

    public int SharedCount => Actions.Count(a => a.ActionType is SwitchActionType.StayInPlace or SwitchActionType.AdoptUntrackedToProfile);
    public int DeactivateCount => Actions.Count(a => a.ActionType is SwitchActionType.MoveToVault or SwitchActionType.AdoptUntrackedToVault);
    public int ActivateCount => Actions.Count(a => a.ActionType == SwitchActionType.ActivateFromVault);
    public int MissingCount => Actions.Count(a => a.ActionType == SwitchActionType.MissingMod);
    public int ConflictCount => Actions.Count(a => a.ActionType == SwitchActionType.FileConflict);

    public long SharedBytesSaved => Actions
        .Where(a => a.ActionType is SwitchActionType.StayInPlace or SwitchActionType.AdoptUntrackedToProfile)
        .Sum(a => a.SizeBytes);

    public bool CanExecute => ConflictCount == 0;
}

/// <summary>
/// Result of executing a profile switch plan.
/// </summary>
public sealed class SwitchExecutionResult
{
    public bool Success { get; set; }
    public int MovedToVaultCount { get; set; }
    public int ActivatedCount { get; set; }
    public int StayedInPlaceCount { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> LogLines { get; set; } = new();
}
