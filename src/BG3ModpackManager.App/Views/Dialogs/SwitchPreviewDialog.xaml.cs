using System.Windows;
using System.Windows.Media;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class SwitchPreviewDialog : Window
{
    private sealed class PlanRow
    {
        public required SwitchActionItem Item { get; init; }
        public string DisplayName => Item.DisplayName;
        public string Reason => Item.Reason;
        public string SizeText => FormatHelper.FormatBytes(Item.SizeBytes);
        public string PayloadLabel => !string.IsNullOrWhiteSpace(Item.PakFileName)
            ? (Item.TrackedFileCount > 0 ? Loc.F("Dialog.SelectMods.FilePlusFormat", Item.PakFileName, Item.TrackedFileCount) : Item.PakFileName)
            : (Item.TrackedFileCount > 0 ? Loc.F("Common.TrackedFilesCountFormat", Item.TrackedFileCount) : "-");

        public string ActionLabel => Item.ActionType switch
        {
            SwitchActionType.StayInPlace => Loc.T("Dialog.SwitchPreview.ActStay"),
            SwitchActionType.AdoptUntrackedToProfile => Loc.T("Dialog.SwitchPreview.ActAdoptStay"),
            SwitchActionType.MoveToVault => Loc.T("Dialog.SwitchPreview.ActToVault"),
            SwitchActionType.AdoptUntrackedToVault => Loc.T("Dialog.SwitchPreview.ActAdoptVault"),
            SwitchActionType.ActivateFromVault => Loc.T("Dialog.SwitchPreview.ActActivate"),
            SwitchActionType.MissingMod => Loc.T("Dialog.SwitchPreview.ActMissing"),
            SwitchActionType.FileConflict => Loc.T("Dialog.SwitchPreview.ActConflict"),
            _ => Item.ActionType.ToString()
        };

        public Brush BadgeBackground => Item.ActionType switch
        {
            SwitchActionType.StayInPlace or SwitchActionType.AdoptUntrackedToProfile => ThemeManager.GetBrush("BrushBadgeGreenBg"),
            SwitchActionType.MoveToVault or SwitchActionType.AdoptUntrackedToVault => ThemeManager.GetBrush("BrushBadgeAmberBg"),
            SwitchActionType.ActivateFromVault => ThemeManager.GetBrush("BrushBadgeBlueBg"),
            _ => ThemeManager.GetBrush("BrushBadgeCrimsonBg")
        };

        public Brush BadgeForeground => Item.ActionType switch
        {
            SwitchActionType.StayInPlace or SwitchActionType.AdoptUntrackedToProfile => ThemeManager.GetBrush("BrushGreen"),
            SwitchActionType.MoveToVault or SwitchActionType.AdoptUntrackedToVault => ThemeManager.GetBrush("BrushAmber"),
            SwitchActionType.ActivateFromVault => ThemeManager.GetBrush("BrushBlue"),
            _ => ThemeManager.GetBrush("BrushCrimson")
        };
    }

    private readonly List<PlanRow> _allRows;

    public SwitchPreviewDialog(SwitchPlan plan)
    {
        InitializeComponent();
        HeadingText.Text = Loc.F("Dialog.SwitchPreview.Heading", plan.TargetProfileName);
        string modeLabel = plan.SwitchMode == SwitchMode.FullProfile
            ? Loc.T("Dialog.SwitchPreview.ModeFull")
            : Loc.T("Dialog.SwitchPreview.ModeOverridesOnly");
        SubheadingText.Text = Loc.F("Dialog.SwitchPreview.Subheading", modeLabel);

        SharedCountText.Text = plan.SharedCount.ToString();
        SharedSavedText.Text = Loc.F("Dialog.SwitchPreview.KpiSharedSub", FormatHelper.FormatBytes(plan.SharedBytesSaved));
        DeactivateCountText.Text = plan.DeactivateCount.ToString();
        ActivateCountText.Text = plan.ActivateCount.ToString();
        IssueCountText.Text = Loc.F("Dialog.SwitchPreview.IssuesFormat", plan.MissingCount, plan.ConflictCount);
        IssueNoteText.Text = plan.ConflictCount > 0
            ? Loc.T("Dialog.SwitchPreview.NoteBlocked")
            : (plan.MissingCount > 0 ? Loc.T("Dialog.SwitchPreview.NoteMissing") : Loc.T("Dialog.SwitchPreview.NoteReady"));

        ConfirmButton.IsEnabled = plan.CanExecute;
        if (!plan.CanExecute)
        {
            ConflictBanner.Visibility = Visibility.Visible;
        }

        _allRows = plan.Actions
            .OrderBy(a => a.ActionType switch
            {
                SwitchActionType.FileConflict => 0,
                SwitchActionType.MissingMod => 1,
                SwitchActionType.MoveToVault => 2,
                SwitchActionType.AdoptUntrackedToVault => 3,
                SwitchActionType.ActivateFromVault => 4,
                _ => 5
            })
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new PlanRow { Item = a })
            .ToList();

        ApplyFilter();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        bool onlyChanges = FilterChanges.IsChecked == true;
        IEnumerable<PlanRow> filtered = _allRows;
        if (onlyChanges)
        {
            filtered = filtered.Where(r => r.Item.ActionType is not (SwitchActionType.StayInPlace or SwitchActionType.AdoptUntrackedToProfile));
        }

        PlanGrid.ItemsSource = filtered.ToList();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
