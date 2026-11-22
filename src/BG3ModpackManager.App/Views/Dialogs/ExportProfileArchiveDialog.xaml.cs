using System.Windows;
using System.Windows.Controls;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;
using Microsoft.Win32;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class ExportProfileArchiveDialog : Window
{
    public string OutputArchivePath { get; private set; } = string.Empty;
    public bool IncludePaks { get; private set; }
    public bool IncludeLooseMods { get; private set; }
    public bool IncludeGeneratedFolder { get; private set; }
    public bool IncludeBinMods { get; private set; }
    public bool IncludeOrderJson { get; private set; }
    public bool IncludeManifestAndReadme { get; private set; }
    public int CompressionLevel { get; private set; } = 1;

    public ExportProfileArchiveDialog(ProfileExportPreview preview, string defaultOutputPath)
    {
        InitializeComponent();

        long totalBytes = preview.PakTotalBytes +
                          preview.LooseDataTotalBytes +
                          preview.LooseGameTotalBytes +
                          preview.LiveGeneratedTotalBytes +
                          preview.LiveBinTotalBytes;
        SubtitleText.Text = Loc.F("Dialog.Export7z.Subtitle", preview.ProfileName, FormatHelper.FormatBytes(totalBytes));

        PakSummaryText.Text = Loc.F("Dialog.Export7z.SummaryPaksFormat", preview.PakModCount, FormatHelper.FormatBytes(preview.PakTotalBytes));
        DataSummaryText.Text = Loc.F("Dialog.Export7z.SummaryLooseFormat", preview.LooseDataModCount, preview.LooseDataFileCount, FormatHelper.FormatBytes(preview.LooseDataTotalBytes));
        GameSummaryText.Text = Loc.F("Dialog.Export7z.SummaryLooseFormat", preview.LooseGameModCount, preview.LooseGameFileCount, FormatHelper.FormatBytes(preview.LooseGameTotalBytes));
        LiveGeneratedSummaryText.Text = Loc.F("Dialog.Export7z.SummaryFilesFormat", preview.LiveGeneratedFileCount, FormatHelper.FormatBytes(preview.LiveGeneratedTotalBytes));
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

        bool hasGen = preview.LiveGeneratedFileCount > 0;
        IncludeGeneratedCheck.IsChecked = hasGen;
        IncludeGeneratedCheck.IsEnabled = hasGen;

        bool hasBin = preview.LiveBinFileCount > 0;
        IncludeBinCheck.IsChecked = hasBin;
        IncludeBinCheck.IsEnabled = hasBin;

        OutputArchivePath = defaultOutputPath;
        IncludePaks = hasPaks;
        IncludeLooseMods = hasLoose;
        IncludeGeneratedFolder = hasGen;
        IncludeBinMods = hasBin;
        IncludeOrderJson = true;
        IncludeManifestAndReadme = true;
        CompressionLevel = 1;
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

        bool paks = IncludePaksCheck.IsChecked == true && IncludePaksCheck.IsEnabled;
        bool loose = IncludeLooseCheck.IsChecked == true && IncludeLooseCheck.IsEnabled;
        bool gen = IncludeGeneratedCheck.IsChecked == true && IncludeGeneratedCheck.IsEnabled;
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
        IncludeBinMods = bin;
        IncludeOrderJson = json;
        IncludeManifestAndReadme = manifest;
        CompressionLevel = level;
        DialogResult = true;
    }
}
