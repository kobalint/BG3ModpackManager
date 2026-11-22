using System.Windows;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class ImportModpackDialog : Window
{
    public sealed class RowItem : ObservableObject
    {
        public required ModpackImportItem Item { get; init; }

        public bool IsSelected
        {
            get => Item.IsSelected;
            set
            {
                if (Item.IsSelected != value)
                {
                    Item.IsSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayName => Item.DisplayName;
        public string KindLabel => FormatHelper.FormatModKind(Item.Kind);
        public int FileCount => Item.FileCount;
        public string SizeText => FormatHelper.FormatBytes(Item.TotalSizeBytes);

        public string NotesText
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Item.ExistingDuplicateModName))
                {
                    parts.Add(Loc.F("Dialog.ImportPack.NoteReused", Item.ExistingDuplicateModName));
                }
                else
                {
                    parts.Add(Loc.T("Dialog.ImportPack.NoteNew"));
                }

                if (Item.ExistingOriginalFilesToBackup.Count > 0)
                {
                    parts.Add(Loc.F("Dialog.ImportPack.NoteBackupCount", Item.ExistingOriginalFilesToBackup.Count));
                }

                return string.Join(" | ", parts);
            }
        }
    }

    private readonly List<RowItem> _rows;

    public string ProfileName { get; private set; } = string.Empty;
    public bool InstallActiveNow { get; private set; }

    public ImportModpackDialog(ModpackImportPlan plan, LibraryDatabase db)
    {
        InitializeComponent();

        HeadingText.Text = Loc.F("Dialog.ImportPack.Heading", plan.SourceName);
        SubtitleText.Text = Loc.F("Dialog.ImportPack.Subtitle", plan.SourceArchivePath);
        ProfileNameBox.Text = plan.SuggestedProfileName;

        int totalPaks = plan.Items.Count(i => !string.IsNullOrWhiteSpace(i.PakFileName));
        int reusedPaks = plan.Items.Count(i => !string.IsNullOrWhiteSpace(i.PakFileName) && i.ExistingDuplicateModId is not null);
        int newPaks = totalPaks - reusedPaks;
        int dataFiles = plan.Items.Sum(i => i.LooseFiles.Count(f => f.Target == TrackedFileTarget.DataFolder));
        int gameFiles = plan.Items.Sum(i => i.LooseFiles.Count(f => f.Target == TrackedFileTarget.GameFolder));
        long totalBytes = plan.Items.Sum(i => i.TotalSizeBytes);

        SummaryText.Text = Loc.F(
            "Dialog.ImportPack.SummaryFormat",
            totalPaks,
            newPaks,
            reusedPaks,
            dataFiles,
            gameFiles,
            FormatHelper.FormatBytes(totalBytes));

        List<string> backupList = plan.Items
            .SelectMany(i => i.ExistingOriginalFilesToBackup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (backupList.Count > 0)
        {
            string preview = string.Join("\n", backupList.Take(6).Select(p => $"• {p}"));
            if (backupList.Count > 6)
            {
                preview += $"\n... (+{backupList.Count - 6})";
            }

            BackupBannerText.Text = Loc.F("Dialog.ImportPack.BackupBannerFormat", backupList.Count, preview);
            BackupBanner.Visibility = Visibility.Visible;
        }

        _rows = plan.Items.Select(i => new RowItem { Item = i }).ToList();
        foreach (RowItem row in _rows)
        {
            row.PropertyChanged += (_, _) => UpdateSelectionSummary();
        }

        ItemsGrid.ItemsSource = _rows;

        bool defaultActive = db.Mods.Count == 0;
        InstallActiveRadio.IsChecked = defaultActive;
        StoreVaultRadio.IsChecked = !defaultActive;

        ProfileName = plan.SuggestedProfileName;
        InstallActiveNow = defaultActive;
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        List<RowItem> selected = _rows.Where(r => r.IsSelected).ToList();
        long bytes = selected.Sum(r => r.Item.TotalSizeBytes);
        SelectedSummaryText.Text = Loc.F("Dialog.ImportPack.SelectedFormat", selected.Count, FormatHelper.FormatBytes(bytes));
        ImportButton.IsEnabled = selected.Count > 0;
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        string name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, Loc.T("Dialog.ImportPack.ValProfileName"), Loc.T("Common.ValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Snapshot UI values on the UI thread before closing.
        ProfileName = name;
        InstallActiveNow = InstallActiveRadio.IsChecked == true;
        DialogResult = true;
    }
}
