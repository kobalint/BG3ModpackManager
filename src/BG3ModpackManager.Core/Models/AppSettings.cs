using System.Text.Json.Serialization;

namespace BG3ModpackManager.Core.Models;

/// <summary>
/// Controls how profile switching handles non-override PAK files that are not in the target profile.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SwitchMode
{
    /// <summary>
    /// Moves all non-profile PAKs, override PAKs, and Data/Game mods to the Vault while leaving shared mods untouched.
    /// </summary>
    FullProfile,

    /// <summary>
    /// Only moves non-profile override PAKs and Data/Game loose mods to the Vault; regular non-override PAKs remain in Mods.
    /// </summary>
    OverridesAndDataOnly
}

/// <summary>
/// Controls the application UI color theme.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Persisted application paths, UI theme, language, and switching preferences.
/// </summary>
public sealed class AppSettings
{
    public string ModsFolder { get; set; } = string.Empty;
    public string GameFolder { get; set; } = string.Empty;
    public string DataFolder { get; set; } = string.Empty;
    public string OrdersFolder { get; set; } = string.Empty;
    public string VaultFolder { get; set; } = string.Empty;
    public SwitchMode SwitchMode { get; set; } = SwitchMode.FullProfile;
    public bool SyncModSettingsLsxOnSwitch { get; set; } = false;
    public string ModSettingsLsxPath { get; set; } = string.Empty;
    public string? LastSelectedOrderJsonPath { get; set; }
    public string? LastExportFolder { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string LanguageCode { get; set; } = "en";
}
