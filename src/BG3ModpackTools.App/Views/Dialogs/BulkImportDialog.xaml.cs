using System.Windows;
using System.Windows.Controls;
using BG3ModpackTools.App.Services;
using Microsoft.Win32;

namespace BG3ModpackTools.App.Views.Dialogs;

public partial class BulkImportDialog : Window
{
    public string SelectedFolder { get; private set; } = string.Empty;
    public bool MoveInsteadOfCopy { get; private set; }

    public BulkImportDialog(string? suggestedFolder = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(suggestedFolder) && Directory.Exists(suggestedFolder))
        {
            FolderBox.Text = suggestedFolder;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.T("Dialog.BulkImport.BrowseTitle"),
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null
        };

        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    private void OnFolderChanged(object sender, TextChangedEventArgs e)
    {
        string path = FolderBox.Text.Trim();
        if (Directory.Exists(path))
        {
            int count = Directory.EnumerateFiles(path, "*.pak", SearchOption.TopDirectoryOnly).Count();
            PreviewText.Text = Loc.F("Dialog.BulkImport.PreviewFound", count, Path.GetFileName(path));
            ImportButton.IsEnabled = count > 0;
        }
        else
        {
            PreviewText.Text = Loc.T("Dialog.BulkImport.PreviewMissing");
            ImportButton.IsEnabled = false;
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        SelectedFolder = FolderBox.Text.Trim();
        MoveInsteadOfCopy = MoveRadio.IsChecked == true;
        DialogResult = true;
    }
}
