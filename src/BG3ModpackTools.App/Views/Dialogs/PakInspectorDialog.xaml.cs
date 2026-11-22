using System.Windows;
using System.Windows.Controls;
using BG3ModpackTools.App.Helpers;
using BG3ModpackTools.App.Services;
using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.App.Views.Dialogs;

public partial class PakInspectorDialog : Window
{
    private readonly List<string> _allPaths = new();

    public PakInspectorDialog(ModEntry mod, AppSettings settings)
    {
        InitializeComponent();

        TitleText.Text = mod.DisplayName;
        FileText.Text = !string.IsNullOrWhiteSpace(mod.PakFileName)
            ? Loc.F("Dialog.Inspector.PakSummary", mod.PakFileName, FormatHelper.FormatBytes(mod.TotalSizeBytes))
            : Loc.F("Dialog.Inspector.LooseSummary", mod.TrackedFiles.Count, FormatHelper.FormatBytes(mod.TotalSizeBytes));

        UuidText.Text = Loc.F("Dialog.Inspector.Uuid", string.IsNullOrWhiteSpace(mod.Uuid) ? Loc.T("Common.None") : mod.Uuid);
        MetaText.Text = Loc.F("Dialog.Inspector.Meta", mod.FolderName ?? Loc.T("Common.None"), mod.VersionString ?? "-", mod.Author ?? "-");
        HashText.Text = Loc.F("Dialog.Inspector.Hash", mod.Id, mod.ContentHash);

        if (mod.EffectiveIsOverride)
        {
            OverrideBadgeText.Text = mod.ManualOverrideMode == ManualOverrideMode.ForceOverride
                ? Loc.T("Dialog.Inspector.BadgeOverrideManual")
                : Loc.T("Dialog.Inspector.BadgeOverrideAuto");
            OverrideBadgeText.Foreground = ThemeManager.GetBrush("BrushAmber");
            OverrideReasonsText.Text = mod.OverrideReasons.Count > 0
                ? string.Join(" | ", mod.OverrideReasons)
                : Loc.T("Dialog.Inspector.MarkedOverride");
        }
        else
        {
            OverrideBadgeText.Text = mod.ManualOverrideMode == ManualOverrideMode.ForceNonOverride
                ? Loc.T("Dialog.Inspector.BadgeStandardManual")
                : Loc.T("Dialog.Inspector.BadgeStandard");
            OverrideBadgeText.Foreground = ThemeManager.GetBrush("BrushGreen");
            OverrideReasonsText.Text = mod.HasGeneratedSharedAssets
                ? Loc.T("Dialog.Inspector.GeneratedShared")
                : Loc.T("Dialog.Inspector.NoOverrides");
        }

        if (!string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            string? pakPath = null;
            string active = ModLibraryService.GetActivePakPath(settings, mod);
            string vault = ModLibraryService.GetVaultPakPath(settings.VaultFolder, mod);
            if (File.Exists(active))
            {
                pakPath = active;
            }
            else if (File.Exists(vault))
            {
                pakPath = vault;
            }

            if (pakPath is not null)
            {
                PakInfo live = LspkPakReader.ReadPakInfo(pakPath);
                foreach (string internalPath in live.InternalFilePaths)
                {
                    _allPaths.Add($"[PAK] {internalPath}");
                }
            }
        }

        foreach (TrackedFileRecord tf in mod.TrackedFiles)
        {
            string prefix = tf.Target == TrackedFileTarget.DataFolder ? "[Data]" : "[Game]";
            _allPaths.Add($"{prefix} {tf.RelativePath} ({FormatHelper.FormatBytes(tf.FileSizeBytes)})");
        }

        foreach (ReplacedOriginalFileRecord orig in mod.ReplacedOriginalFiles)
        {
            string state = orig.IsCurrentlyRestored
                ? Loc.T("Dialog.Inspector.OrigRestored")
                : Loc.F("Dialog.Inspector.OrigBackedUp", orig.BackupRelativePath);
            _allPaths.Add(Loc.F("Dialog.Inspector.OrigLine", orig.RelativePath, state));
        }

        ApplyFilter();
    }

    public PakInspectorDialog(PakInfo pakInfo)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(pakInfo.Name) ? pakInfo.FileName : pakInfo.Name;
        FileText.Text = Loc.F("Dialog.Inspector.PakSummary", pakInfo.FileName, FormatHelper.FormatBytes(pakInfo.FileSizeBytes));
        UuidText.Text = Loc.F("Dialog.Inspector.Uuid", string.IsNullOrWhiteSpace(pakInfo.Uuid) ? Loc.T("Common.None") : pakInfo.Uuid);
        MetaText.Text = Loc.F("Dialog.Inspector.MetaLive", pakInfo.Folder ?? Loc.T("Common.None"), pakInfo.VersionString ?? "-", pakInfo.LspkVersion);
        HashText.Text = Loc.F("Dialog.Inspector.Path", pakInfo.FilePath);

        if (pakInfo.IsDetectedOverride)
        {
            OverrideBadgeText.Text = Loc.T("Dialog.Inspector.BadgeOverrideAuto");
            OverrideBadgeText.Foreground = ThemeManager.GetBrush("BrushAmber");
            OverrideReasonsText.Text = string.Join(" | ", pakInfo.OverrideReasons);
        }
        else
        {
            OverrideBadgeText.Text = Loc.T("Dialog.Inspector.BadgeStandard");
            OverrideBadgeText.Foreground = ThemeManager.GetBrush("BrushGreen");
            OverrideReasonsText.Text = pakInfo.HasGeneratedSharedAssets
                ? Loc.T("Dialog.Inspector.GeneratedShared")
                : Loc.T("Dialog.Inspector.NoOverrides");
        }

        _allPaths.AddRange(pakInfo.InternalFilePaths);
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim();
        List<string> shown = string.IsNullOrEmpty(q)
            ? _allPaths
            : _allPaths.Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        FilesListBox.ItemsSource = shown;
        FileCountLabel.Text = Loc.F("Dialog.Inspector.CountFormat", shown.Count, _allPaths.Count);
    }
}
