using System.Windows;
using System.Windows.Controls;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class SelectModsDialog : Window
{
    public sealed class SelectableModRow : ObservableObject
    {
        private bool _isSelected;
        public required ModEntry Mod { get; init; }
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
        public string Name => Mod.DisplayName;
        public string KindLabel => FormatHelper.FormatModKind(Mod.Kind);
        public string OverrideLabel => FormatHelper.FormatOverrideLabel(Mod.EffectiveOverrideKind, Mod.ManualOverrideMode);
        public string LocationLabel => FormatHelper.FormatModLocation(Mod.Location);
        public string FileSummary => !string.IsNullOrWhiteSpace(Mod.PakFileName)
            ? (Mod.TrackedFiles.Count > 0 ? Loc.F("Dialog.SelectMods.FilePlusFormat", Mod.PakFileName, Mod.TrackedFiles.Count) : Mod.PakFileName)
            : Loc.F("Dialog.SelectMods.TrackedFormat", Mod.TrackedFiles.Count);
        public string SizeText => FormatHelper.FormatBytes(Mod.TotalSizeBytes);
    }

    private readonly List<SelectableModRow> _allRows;
    public List<string> SelectedModIds { get; private set; } = new();

    public SelectModsDialog(IEnumerable<ModEntry> availableMods)
    {
        InitializeComponent();
        _allRows = availableMods
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new SelectableModRow { Mod = m })
            .ToList();

        foreach (SelectableModRow row in _allRows)
        {
            row.PropertyChanged += (_, _) => UpdateCount();
        }

        ApplyFilter();
        UpdateCount();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = SearchBox.Text.Trim();
        IEnumerable<SelectableModRow> filtered = _allRows;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.FileSummary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Mod.Uuid?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        ModsGrid.ItemsSource = filtered.ToList();
    }

    private void OnSelectVisibleClick(object sender, RoutedEventArgs e)
    {
        if (ModsGrid.ItemsSource is IEnumerable<SelectableModRow> visible)
        {
            foreach (SelectableModRow row in visible)
            {
                row.IsSelected = true;
            }
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        int count = _allRows.Count(r => r.IsSelected);
        CountLabel.Text = Loc.F("Dialog.SelectMods.CountFormat", count);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        SelectedModIds = _allRows.Where(r => r.IsSelected).Select(r => r.Mod.Id).ToList();
        DialogResult = true;
    }
}
