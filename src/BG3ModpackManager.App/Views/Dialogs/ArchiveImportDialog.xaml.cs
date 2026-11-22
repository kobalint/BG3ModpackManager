using System.Windows;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class ArchiveImportDialog : Window
{
    public sealed record TargetOption(ImportTargetKind Kind, string Label);

    public sealed class CandidateRow : ObservableObject
    {
        private static TargetOption[] BuildPakTargets() => new[]
        {
            new TargetOption(ImportTargetKind.ModsPak, Loc.T("Dialog.ArchiveImport.Target.ModsPak")),
            new TargetOption(ImportTargetKind.Ignore, Loc.T("Dialog.ArchiveImport.Target.Ignore"))
        };

        private static TargetOption[] BuildLooseTargets() => new[]
        {
            new TargetOption(ImportTargetKind.DataFolder, Loc.T("Dialog.ArchiveImport.Target.DataFolder")),
            new TargetOption(ImportTargetKind.GameFolder, Loc.T("Dialog.ArchiveImport.Target.GameFolder")),
            new TargetOption(ImportTargetKind.Ignore, Loc.T("Dialog.ArchiveImport.Target.Ignore"))
        };

        public required ImportItemCandidate Candidate { get; init; }
        public TargetOption[] AvailableTargets => Candidate.PakInfo is not null ? BuildPakTargets() : BuildLooseTargets();

        public bool IsSelected
        {
            get => Candidate.IsSelected;
            set
            {
                if (Candidate.IsSelected != value)
                {
                    Candidate.IsSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImportTargetKind TargetKind
        {
            get => Candidate.TargetKind;
            set
            {
                if (Candidate.TargetKind != value)
                {
                    Candidate.TargetKind = value;
                    IsSelected = value != ImportTargetKind.Ignore;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusNotes));
                }
            }
        }

        public string DisplayName => Candidate.DisplayName;
        public int FileCount => Candidate.FileCount;
        public string SizeText => FormatHelper.FormatBytes(Candidate.FileSizeBytes);

        public string StatusNotes
        {
            get
            {
                var notes = new List<string>();
                if (Candidate.PakInfo?.IsDetectedOverride == true)
                {
                    notes.Add($"OVERRIDE ({string.Join(", ", Candidate.PakInfo.OverrideReasons)})");
                }
                if (!string.IsNullOrWhiteSpace(Candidate.ExistingDuplicateModId))
                {
                    notes.Add(Loc.F("Dialog.ArchiveImport.DupNote", Candidate.ExistingDuplicateModName));
                }
                if (!string.IsNullOrWhiteSpace(Candidate.WarningMessage))
                {
                    notes.Add(Candidate.WarningMessage);
                }

                return notes.Count == 0 ? Loc.T("Dialog.ArchiveImport.Ready") : string.Join(" | ", notes);
            }
        }
    }

    private sealed record ProfileOption(string? ProfileId, string Label);

    private readonly ArchiveImportPlan _plan;
    private readonly List<CandidateRow> _rows;

    public string? SelectedProfileId { get; private set; }
    public bool InstallActiveNow { get; private set; }
    public bool PackageAsHybrid { get; private set; }

    public ArchiveImportDialog(ArchiveImportPlan plan, LibraryDatabase db)
    {
        InitializeComponent();
        _plan = plan;

        SourceTitleText.Text = Loc.F("Dialog.ArchiveImport.Heading", plan.SourceName);
        SourceSubtitleText.Text = Loc.F("Dialog.ArchiveImport.Subtitle", plan.SourcePath);

        _rows = plan.Items.Select(item => new CandidateRow { Candidate = item }).ToList();
        foreach (CandidateRow row in _rows)
        {
            row.PropertyChanged += (_, _) => RefreshSummary();
        }

        ItemsGrid.ItemsSource = _rows;
        HybridCheckBox.IsChecked = plan.PackageAsSingleHybridMod;
        CustomNameBox.Text = Path.GetFileNameWithoutExtension(plan.SourceName);

        var profileOptions = new List<ProfileOption>
        {
            new(null, Loc.T("Dialog.ArchiveImport.NoProfileOption"))
        };
        foreach (ModProfile p in db.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            profileOptions.Add(new(p.Id, p.Id == db.ActiveProfileId ? Loc.F("Dialog.ArchiveImport.ActiveOptionFormat", p.Name) : p.Name));
        }

        ProfileCombo.ItemsSource = profileOptions;
        ProfileCombo.SelectedIndex = !string.IsNullOrWhiteSpace(db.ActiveProfileId)
            ? profileOptions.FindIndex(o => o.ProfileId == db.ActiveProfileId)
            : 0;
        if (ProfileCombo.SelectedIndex < 0)
        {
            ProfileCombo.SelectedIndex = 0;
        }

        RefreshSummary();
    }

    private void RefreshSummary()
    {
        int selectedCount = _rows.Count(r => r.IsSelected && r.TargetKind != ImportTargetKind.Ignore);
        long totalBytes = _rows.Where(r => r.IsSelected && r.TargetKind != ImportTargetKind.Ignore)
            .Sum(r => r.Candidate.FileSizeBytes);

        int paks = _rows.Count(r => r.IsSelected && r.TargetKind == ImportTargetKind.ModsPak);
        int loose = _rows.Count(r => r.IsSelected && r.TargetKind is ImportTargetKind.DataFolder or ImportTargetKind.GameFolder);
        HybridCheckBox.IsEnabled = paks == 1 && loose > 0;
        if (!HybridCheckBox.IsEnabled)
        {
            HybridCheckBox.IsChecked = false;
        }

        SummaryLabel.Text = Loc.F("Dialog.ArchiveImport.SummaryFormat", selectedCount, FormatHelper.FormatBytes(totalBytes));
        ImportButton.IsEnabled = selectedCount > 0;
        CustomNameBox.IsEnabled = loose > 0 && (paks == 0 || HybridCheckBox.IsChecked != true);
    }

    private void OnHybridCheckChanged(object sender, RoutedEventArgs e) => RefreshSummary();

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        SelectedProfileId = (ProfileCombo.SelectedItem as ProfileOption)?.ProfileId;
        InstallActiveNow = ActiveRadio.IsChecked == true;
        PackageAsHybrid = HybridCheckBox.IsChecked == true;
        _plan.PackageAsSingleHybridMod = PackageAsHybrid;
        _plan.CustomDisplayName = CustomNameBox.IsEnabled ? CustomNameBox.Text.Trim() : null;
        DialogResult = true;
    }
}
