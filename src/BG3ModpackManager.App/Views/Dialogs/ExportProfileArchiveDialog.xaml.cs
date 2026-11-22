using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;
using Microsoft.Win32;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class ExportProfileArchiveDialog : Window
{
    public sealed class GeneratedGroupRow : ObservableObject
    {
        private bool _isSelected;

        public required GeneratedExportFolderGroup Group { get; init; }
        public string GroupKey => Group.GroupKey;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LabelText
        {
            get
            {
                string tag = Group.IsSelectedByDefault
                    ? Loc.T("Dialog.Export7z.GenTagMod")
                    : (Group.IsToolkitProject
                        ? Loc.T("Dialog.Export7z.GenTagToolkit")
                        : Loc.T("Dialog.Export7z.GenTagOther"));
                return Loc.F(
                    "Dialog.Export7z.GenRowFormat",
                    Group.GroupKey,
                    Group.FileCount,
                    FormatHelper.FormatBytes(Group.TotalBytes),
                    tag);
            }
        }

        public Brush LabelBrush => ThemeManager.GetBrush(Group.IsSelectedByDefault ? "BrushTextPrimary" : "BrushTextSecondary");
    }

    private readonly ProfileExportPreview _preview;
    private readonly List<GeneratedGroupRow> _generatedRows;

    public string OutputArchivePath { get; private set; } = string.Empty;
    public bool IncludePaks { get; private set; }
    public bool IncludeLooseMods { get; private set; }
    public bool IncludeGeneratedFolder { get; private set; }
    public HashSet<string> SelectedGeneratedGroupKeys { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IncludeBinMods { get; private set; }
    public bool IncludeOrderJson { get; private set; }
    public bool IncludeManifestAndReadme { get; private set; }
    public int CompressionLevel { get; private set; } = 1;

    public ExportProfileArchiveDialog(ProfileExportPreview preview, string defaultOutputPath)
    {
        InitializeComponent();
        _preview = preview;

        _generatedRows = preview.GeneratedGroups
            .Select(g => new GeneratedGroupRow
            {
                Group = g,
                IsSelected = g.IsSelectedByDefault
            })
            .ToList();

        foreach (GeneratedGroupRow row in _generatedRows)
        {
            row.PropertyChanged += (_, _) => RefreshGeneratedSummary();
        }

        if (_generatedRows.Count > 0)
        {
            GeneratedGroupsList.ItemsSource = _generatedRows;
            GeneratedGroupsBorder.Visibility = Visibility.Visible;
        }

        PakSummaryText.Text = Loc.F("Dialog.Export7z.SummaryPaksFormat", preview.PakModCount, FormatHelper.FormatBytes(preview.PakTotalBytes));
        DataSummaryText.Text = Loc.F("Dialog.Export7z.SummaryLooseFormat", preview.LooseDataModCount, preview.LooseDataFileCount, FormatHelper.FormatBytes(preview.LooseDataTotalBytes));
        GameSummaryText.Text = Loc.F("Dialog.Export7z.SummaryLooseFormat", preview.LooseGameModCount, preview.LooseGameFileCount, FormatHelper.FormatBytes(preview.LooseGameTotalBytes));
        LiveBinSummaryText.Text = Loc.F("Dialog.Export7z.SummaryLooseFormat", preview.LiveBinModCount, preview.LiveBinFileCount, FormatHelper.FormatBytes(preview.LiveBinTotalBytes));

        if (preview.MissingModCount > 0 || preview.MissingFileCount > 0 || preview.Warnings.Count > 0)
        {
            string head = Loc.F("Dialog.Export7z.MissingHead", preview.MissingModCount, preview.MissingFileCount);
            IEnumerable<string> previewWarnings = preview.Warnings.Take(5);
            WarningBannerText.Text = preview.Warnings.Count > 0
                ? head + "\n" + string.Join("\n", previewWarnings) + (preview.Warnings.Count > 5 ? "\n..." : "")
                : head;
            WarningBanner.Visibility = Visibility.Visible;
        }

        OutputPathBox.Text = defaultOutputPath;

        bool hasPaks = preview.PakModCount > 0;
        IncludePaksCheck.IsChecked = hasPaks;
        IncludePaksCheck.IsEnabled = hasPaks;

        bool hasLoose = preview.LooseDataFileCount + preview.LooseGameFileCount > 0;
        IncludeLooseCheck.IsChecked = hasLoose;
        IncludeLooseCheck.IsEnabled = hasLoose;

        bool hasAnyGenGroups = _generatedRows.Count > 0;
        bool hasDefaultGen = _generatedRows.Any(r => r.IsSelected);
        IncludeGeneratedCheck.IsChecked = hasDefaultGen;
        IncludeGeneratedCheck.IsEnabled = hasAnyGenGroups;
        IncludeGeneratedCheck.Checked += (_, _) => RefreshGeneratedSummary();
        IncludeGeneratedCheck.Unchecked += (_, _) => RefreshGeneratedSummary();

        bool hasBin = preview.LiveBinFileCount > 0;
        IncludeBinCheck.IsChecked = hasBin;
        IncludeBinCheck.IsEnabled = hasBin;

        OutputArchivePath = defaultOutputPath;
        IncludePaks = hasPaks;
        IncludeLooseMods = hasLoose;
        IncludeGeneratedFolder = hasDefaultGen;
        SelectedGeneratedGroupKeys = _generatedRows
            .Where(r => r.IsSelected)
            .Select(r => r.GroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IncludeBinMods = hasBin;
        IncludeOrderJson = true;
        IncludeManifestAndReadme = true;
        CompressionLevel = 1;

        RefreshGeneratedSummary();
    }

    private void RefreshGeneratedSummary()
    {
        bool genChecked = IncludeGeneratedCheck.IsChecked == true && IncludeGeneratedCheck.IsEnabled;
        List<GeneratedGroupRow> activeGen = genChecked
            ? _generatedRows.Where(r => r.IsSelected).ToList()
            : new List<GeneratedGroupRow>();

        int genFiles = activeGen.Sum(r => r.Group.FileCount);
        long genBytes = activeGen.Sum(r => r.Group.TotalBytes);

        LiveGeneratedSummaryText.Text = Loc.F("Dialog.Export7z.SummaryFilesFormat", genFiles, FormatHelper.FormatBytes(genBytes));

        long totalBytes = _preview.PakTotalBytes +
                          _preview.LooseDataTotalBytes +
                          _preview.LooseGameTotalBytes +
                          genBytes +
                          _preview.LiveBinTotalBytes;
        SubtitleText.Text = Loc.F("Dialog.Export7z.Subtitle", _preview.ProfileName, FormatHelper.FormatBytes(totalBytes));
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        string current = OutputPathBox.Text.Trim();
        string? dir = !string.IsNullOrWhiteSpace(current) ? Path.GetDirectoryName(current) : null;
        string file = !string.IsNullOrWhiteSpace(current) ? Path.GetFileName(current) : "ModpackProfile.7z";

        var dlg = new SaveFileDialog
        {
            Title = Loc.T("Dialog.Export7z.SaveTitle"),
            Filter = Loc.T("Dialog.Export7z.Filter"),
            DefaultExt = ".7z",
            AddExtension = true,
            FileName = file,
            InitialDirectory = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null
        };

        if (dlg.ShowDialog(this) == true)
        {
            OutputPathBox.Text = dlg.FileName;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        string path = OutputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, Loc.T("Dialog.Export7z.ValPath"), Loc.T("Common.ValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            path += ".7z";
        }

        HashSet<string> selectedGenKeys = _generatedRows
            .Where(r => r.IsSelected)
            .Select(r => r.GroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool paks = IncludePaksCheck.IsChecked == true && IncludePaksCheck.IsEnabled;
        bool loose = IncludeLooseCheck.IsChecked == true && IncludeLooseCheck.IsEnabled;
        bool gen = IncludeGeneratedCheck.IsChecked == true && IncludeGeneratedCheck.IsEnabled && selectedGenKeys.Count > 0;
        bool bin = IncludeBinCheck.IsChecked == true && IncludeBinCheck.IsEnabled;
        bool json = IncludeOrderJsonCheck.IsChecked == true;
        bool manifest = IncludeManifestCheck.IsChecked == true;

        if (!paks && !loose && !gen && !bin && !json && !manifest)
        {
            MessageBox.Show(this, Loc.T("Dialog.Export7z.ValItems"), Loc.T("Common.ValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int level = 1;
        if (CompressionCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            int.TryParse(tagStr, out int parsed))
        {
            level = parsed;
        }

        // Snapshot all UI values cleanly on the UI thread before closing.
        OutputArchivePath = path;
        IncludePaks = paks;
        IncludeLooseMods = loose;
        IncludeGeneratedFolder = gen;
        SelectedGeneratedGroupKeys = selectedGenKeys;
        IncludeBinMods = bin;
        IncludeOrderJson = json;
        IncludeManifestAndReadme = manifest;
        CompressionLevel = level;
        DialogResult = true;
    }
}
