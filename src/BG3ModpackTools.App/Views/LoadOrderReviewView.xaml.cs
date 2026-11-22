using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BG3ModpackTools.App.Helpers;
using BG3ModpackTools.App.Services;
using BG3ModpackTools.App.Views.Dialogs;
using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;
using Microsoft.Win32;

namespace BG3ModpackTools.App.Views;

public partial class LoadOrderReviewView : UserControl
{
    private sealed class JsonFileOption
    {
        public required string FullPath { get; init; }
        public required string Display { get; init; }
    }

    private sealed class ReviewRow
    {
        public required LoadOrderReviewItem Item { get; init; }
        public string OrderText => Item.OrderIndex?.ToString() ?? "-";
        public string DisplayName => Item.Status is ReviewStatus.Bg3MmOverride or ReviewStatus.UnlistedOverride or ReviewStatus.UnlistedInactive
            ? (Item.PakName ?? Item.PakFileName ?? "(Unknown)")
            : Item.JsonName;
        public string PakFileName => Item.PakFileName ?? "-";
        public string VersionText => Item.VersionString ?? "-";
        public string SizeText => Item.FileSizeBytes > 0 ? FormatHelper.FormatBytes(Item.FileSizeBytes) : "-";

        public bool CanMoveToVault => Item.Status is ReviewStatus.Active or ReviewStatus.ActiveOverride or ReviewStatus.Bg3MmOverride or ReviewStatus.UnlistedOverride or ReviewStatus.UnlistedInactive
                                      && !string.IsNullOrWhiteSpace(Item.PakFullPath);
        public bool CanRestoreFromVault => Item.Status == ReviewStatus.InVault && !string.IsNullOrWhiteSpace(Item.MatchedModId);
        public string VaultActionLabel => CanRestoreFromVault ? Loc.T("Common.RestoreFromVault") : Loc.T("Common.ToVault");
        public Visibility VaultActionVisibility => (CanMoveToVault || CanRestoreFromVault) ? Visibility.Visible : Visibility.Collapsed;

        public string StatusLabel => Item.Status switch
        {
            ReviewStatus.Active => Loc.T("Review.Status.Active"),
            ReviewStatus.ActiveOverride => Loc.T("Review.Status.ActiveOverride"),
            ReviewStatus.Bg3MmOverride => Loc.T("Review.Status.Bg3MmOverride"),
            ReviewStatus.InVault => Loc.T("Review.Status.InVault"),
            ReviewStatus.Missing => Loc.T("Review.Status.Missing"),
            ReviewStatus.UnlistedOverride => Loc.T("Review.Status.UnlistedOverride"),
            ReviewStatus.UnlistedInactive => Loc.T("Review.Status.UnlistedInactive"),
            _ => Item.Status.ToString()
        };

        public Brush StatusBg => Item.Status switch
        {
            ReviewStatus.Active => ThemeManager.GetBrush("BrushBadgeGreenBg"),
            ReviewStatus.ActiveOverride => ThemeManager.GetBrush("BrushBadgeAmberBg"),
            ReviewStatus.Bg3MmOverride => ThemeManager.GetBrush("BrushBadgeStandaloneBg"),
            ReviewStatus.InVault => ThemeManager.GetBrush("BrushBadgeBlueBg"),
            ReviewStatus.Missing => ThemeManager.GetBrush("BrushBadgeCrimsonBg"),
            ReviewStatus.UnlistedOverride => ThemeManager.GetBrush("BrushBadgeCrimsonBg"),
            _ => ThemeManager.GetBrush("BrushBadgeNeutralBg")
        };

        public Brush StatusFg => Item.Status switch
        {
            ReviewStatus.Active => ThemeManager.GetBrush("BrushGreen"),
            ReviewStatus.ActiveOverride => ThemeManager.GetBrush("BrushAmber"),
            ReviewStatus.Bg3MmOverride => ThemeManager.GetBrush("BrushStandaloneOvr"),
            ReviewStatus.InVault => ThemeManager.GetBrush("BrushBlue"),
            ReviewStatus.Missing => ThemeManager.GetBrush("BrushCrimson"),
            ReviewStatus.UnlistedOverride => ThemeManager.GetBrush("BrushCrimson"),
            _ => ThemeManager.GetBrush("BrushBadgeNeutralFg")
        };

        public string OverrideLabel => Item.OverrideKind switch
        {
            OverrideKind.StandaloneOverride => Loc.T("Common.Override.Standalone"),
            OverrideKind.MergedOverride => Loc.T("Common.Override.Merged"),
            _ => Item.IsOverride ? Loc.T("Common.Override.Merged") : Loc.T("Common.Override.Standard")
        };

        public Brush OverrideBg => FormatHelper.GetOverrideBg(
            Item.OverrideKind != OverrideKind.None ? Item.OverrideKind : (Item.IsOverride ? OverrideKind.MergedOverride : OverrideKind.None));

        public Brush OverrideFg => FormatHelper.GetOverrideFg(
            Item.OverrideKind != OverrideKind.None ? Item.OverrideKind : (Item.IsOverride ? OverrideKind.MergedOverride : OverrideKind.None));

        public string OverrideTooltip => Item.OverrideReasons.Count > 0
            ? string.Join("\n", Item.OverrideReasons)
            : Loc.T("Review.LoadsOnlyViaOrder");

        public string Notes
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Item.MultipleVariantsNote))
                {
                    parts.Add(Item.MultipleVariantsNote);
                }
                if (Item.OverrideReasons.Count > 0)
                {
                    parts.Add(string.Join(", ", Item.OverrideReasons));
                }

                return string.Join(" | ", parts);
            }
        }
    }

    private readonly AppState _state;
    private LoadOrderAuditReport? _report;
    private List<ReviewRow> _allRows = new();
    private bool _suppressComboEvent;

    public LoadOrderReviewView(AppState state)
    {
        InitializeComponent();
        _state = state;
        _state.StateChanged += OnStateChanged;
        ThemeManager.ThemeChanged += _ => RefreshLocalizedDisplay();
        LocalizationManager.Instance.LanguageChanged += RefreshLocalizedDisplay;
        PopulateJsonList();
    }

    private void OnStateChanged()
    {
        PopulateJsonList();
    }

    private void RefreshLocalizedDisplay()
    {
        PopulateJsonList();
        if (_report is null)
        {
            return;
        }

        KpiUnlistedInactive.Text = _report.UnlistedOverrideCount > 0
            ? Loc.F("Review.InactiveOvrFormat", _report.UnlistedInactiveCount, _report.UnlistedOverrideCount)
            : _report.UnlistedInactiveCount.ToString();

        _allRows = _report.Items.Select(i => new ReviewRow { Item = i }).ToList();
        ApplyFilter();
        UpdateSelectionButtons();
    }

    public async Task LoadOrRefreshInitialAuditAsync()
    {
        PopulateJsonList();
        string? target = _state.Settings.LastSelectedOrderJsonPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            if (JsonFilesCombo.SelectedItem is JsonFileOption opt)
            {
                target = opt.FullPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            await AuditFileAsync(target);
        }
    }

    public async Task AuditFileAsync(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return;
        }

        _state.Settings.LastSelectedOrderJsonPath = jsonPath;
        _state.Store.SaveSettings(_state.Settings);
        PopulateJsonList();

        try
        {
            _state.SetBusy(true, Loc.F("Review.AuditBusy", Path.GetFileName(jsonPath)));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            LoadOrderAuditReport report = await Task.Run(() => LoadOrderReviewService.AuditLoadOrderAsync(
                jsonPath,
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress));

            _state.Store.SaveCache(_state.Cache);
            _state.LatestAudit = report;
            _report = report;

            KpiJsonTotal.Text = report.JsonTotalCount.ToString();
            KpiActive.Text = report.ActiveCount.ToString();
            KpiActiveOverrides.Text = report.ActiveOverrideCount.ToString();
            KpiBg3MmOverrides.Text = report.Bg3MmOverrideCount.ToString();
            KpiVaultMissing.Text = $"{report.InVaultCount} / {report.MissingCount}";
            KpiUnlistedInactive.Text = report.UnlistedOverrideCount > 0
                ? Loc.F("Review.InactiveOvrFormat", report.UnlistedInactiveCount, report.UnlistedOverrideCount)
                : report.UnlistedInactiveCount.ToString();
            LsxStatusPill.Text = report.ModSettingsLsxSummary;

            MoveUnlistedOverridesButton.IsEnabled = report.UnlistedOverrideCount > 0;
            MoveAllUnlistedButton.IsEnabled = report.UnlistedOverrideCount + report.UnlistedInactiveCount > 0;
            RestoreVaultJsonButton.IsEnabled = report.InVaultCount > 0;

            _allRows = report.Items.Select(i => new ReviewRow { Item = i }).ToList();
            ApplyFilter();
            UpdateSelectionButtons();
            _state.Log(Loc.F(
                "Review.AuditLog",
                Path.GetFileName(jsonPath),
                report.ActiveCount + report.ActiveOverrideCount,
                report.JsonTotalCount,
                report.Bg3MmOverrideCount,
                report.UnlistedInactiveCount));
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Review.AuditErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void PopulateJsonList()
    {
        var list = new List<JsonFileOption>();
        if (!string.IsNullOrWhiteSpace(_state.Settings.OrdersFolder) && Directory.Exists(_state.Settings.OrdersFolder))
        {
            foreach (string file in Directory.GetFiles(_state.Settings.OrdersFolder, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                if (name == ".json")
                {
                    name = Loc.T("Review.UnnamedJson");
                }

                list.Add(new JsonFileOption { FullPath = file, Display = $"{name}  —  {file}" });
            }
        }

        string? selected = _state.Settings.LastSelectedOrderJsonPath;
        if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected) &&
            !list.Any(x => string.Equals(x.FullPath, selected, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, new JsonFileOption { FullPath = selected, Display = $"{Path.GetFileName(selected)}  —  {selected}" });
        }

        _suppressComboEvent = true;
        JsonFilesCombo.ItemsSource = list;
        JsonFileOption? match = !string.IsNullOrWhiteSpace(selected)
            ? list.FirstOrDefault(x => string.Equals(x.FullPath, selected, StringComparison.OrdinalIgnoreCase))
            : null;
        match ??= list.FirstOrDefault(x => !x.FullPath.EndsWith("LastExported.json", StringComparison.OrdinalIgnoreCase) &&
                                           !Path.GetFileName(x.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                  ?? list.FirstOrDefault();

        JsonFilesCombo.SelectedItem = match;
        _suppressComboEvent = false;
    }

    private async void OnJsonComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvent)
        {
            return;
        }

        if (JsonFilesCombo.SelectedItem is JsonFileOption opt && File.Exists(opt.FullPath))
        {
            await AuditFileAsync(opt.FullPath);
        }
    }

    private async void OnBrowseJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Profiles.ImportJson.DialogTitle"),
            Filter = Loc.T("Profiles.ImportJson.Filter"),
            InitialDirectory = Directory.Exists(_state.Settings.OrdersFolder) ? _state.Settings.OrdersFolder : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            await AuditFileAsync(dlg.FileName);
        }
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (JsonFilesCombo.SelectedItem is JsonFileOption opt)
        {
            await AuditFileAsync(opt.FullPath);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ReviewRow> filtered = _allRows;

        if (FilterOverrides.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Item.IsOverride);
        }
        else if (FilterBg3MmOverrides.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Item.Status == ReviewStatus.Bg3MmOverride);
        }
        else if (FilterInactive.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Item.Status is ReviewStatus.UnlistedInactive or ReviewStatus.UnlistedOverride);
        }
        else if (FilterIssues.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Item.Status is ReviewStatus.InVault or ReviewStatus.Missing);
        }

        string q = SearchBox.Text.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(r =>
                r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.PakFileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Item.JsonUuid.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Notes.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        AuditGrid.ItemsSource = filtered.ToList();
    }

    private void OnAuditGridSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionButtons();

    private void UpdateSelectionButtons()
    {
        var selected = AuditGrid.SelectedItems.OfType<ReviewRow>().ToList();
        MoveSelectedToVaultButton.IsEnabled = selected.Any(r => r.CanMoveToVault);
        RestoreSelectedButton.IsEnabled = selected.Any(r => r.CanRestoreFromVault);
    }

    private async void OnRowVaultActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ReviewRow row)
        {
            return;
        }

        if (row.CanMoveToVault)
        {
            await MoveRowsToVaultAsync(new[] { row });
        }
        else if (row.CanRestoreFromVault)
        {
            await RestoreRowsFromVaultAsync(new[] { row });
        }
    }

    private async void OnMoveSelectedToVaultClick(object sender, RoutedEventArgs e)
    {
        var targets = AuditGrid.SelectedItems.OfType<ReviewRow>().Where(r => r.CanMoveToVault).ToList();
        if (targets.Count > 0)
        {
            await MoveRowsToVaultAsync(targets);
        }
    }

    private async void OnRestoreSelectedFromVaultClick(object sender, RoutedEventArgs e)
    {
        var targets = AuditGrid.SelectedItems.OfType<ReviewRow>().Where(r => r.CanRestoreFromVault).ToList();
        if (targets.Count > 0)
        {
            await RestoreRowsFromVaultAsync(targets);
        }
    }

    private async Task MoveRowsToVaultAsync(IReadOnlyList<ReviewRow> rows)
    {
        if (_report is null || rows.Count == 0)
        {
            return;
        }

        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        try
        {
            _state.SetBusy(true, Loc.F("Review.MoveToVaultBusy", rows.Count));
            int moved = 0;
            foreach (ReviewRow row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Item.PakFullPath) || !File.Exists(row.Item.PakFullPath))
                {
                    continue;
                }

                string pakFullPath = row.Item.PakFullPath;
                ModEntry mod = await Task.Run(() => ModLibraryService.AdoptAndMovePakToVaultAsync(
                    pakFullPath,
                    _state.Settings,
                    _state.Db,
                    _state.Cache));
                _state.Log(Loc.F("Review.MovedOneLog", mod.DisplayName));
                moved++;
            }

            _state.SaveLibraryAndCache();
            if (moved > 0)
            {
                await AuditFileAsync(_report.JsonFilePath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Review.MoveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private async Task RestoreRowsFromVaultAsync(IReadOnlyList<ReviewRow> rows)
    {
        if (_report is null || rows.Count == 0)
        {
            return;
        }

        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        int restored = 0;
        var skipped = new List<string>();
        foreach (ReviewRow row in rows)
        {
            ModEntry? mod = !string.IsNullOrWhiteSpace(row.Item.MatchedModId)
                ? _state.Db.Mods.FirstOrDefault(m => m.Id == row.Item.MatchedModId)
                : null;
            if (mod is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mod.PakFileName) && File.Exists(ModLibraryService.GetActivePakPath(_state.Settings, mod)))
            {
                skipped.Add(Loc.F("Review.CollisionOccupied", mod.DisplayName, mod.PakFileName));
                continue;
            }

            ModLibraryService.ActivateModFromVault(mod, _state.Settings);
            _state.Log(Loc.F("Review.RestoreLog", mod.DisplayName));
            restored++;
        }

        _state.SaveLibraryAndCache();
        if (skipped.Count > 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.F("Review.RestorePartialMsg", restored, string.Join("\n", skipped)),
                Loc.T("Review.RestorePartialTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        await AuditFileAsync(_report.JsonFilePath);
    }

    private async void OnMoveUnlistedOverridesClick(object sender, RoutedEventArgs e)
    {
        await QuarantineAsync(includeNonOverrides: false);
    }

    private async void OnMoveAllUnlistedClick(object sender, RoutedEventArgs e)
    {
        await QuarantineAsync(includeNonOverrides: true);
    }

    private async Task QuarantineAsync(bool includeNonOverrides)
    {
        if (_report is null)
        {
            return;
        }

        int count = includeNonOverrides
            ? _report.UnlistedOverrideCount + _report.UnlistedInactiveCount
            : _report.UnlistedOverrideCount;

        if (count == 0)
        {
            return;
        }

        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        string label = Loc.T(includeNonOverrides ? "Review.Quarantine.LabelUnlisted" : "Review.Quarantine.LabelOverrides");
        if (MessageBox.Show(
                Window.GetWindow(this),
                Loc.F("Review.Quarantine.ConfirmMsg", count, label),
                Loc.T("Review.Quarantine.ConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _state.SetBusy(true, Loc.F("Review.Quarantine.Busy", count, label));
            int moved = await Task.Run(() => ModLibraryService.QuarantineUnlistedPaksAsync(
                _report,
                includeNonOverrides,
                _state.Settings,
                _state.Db,
                _state.Cache));

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Review.Quarantine.Log", moved, label));
            await AuditFileAsync(_report.JsonFilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Review.Quarantine.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private async void OnRestoreVaultModsClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        List<string> modIds = _report.Items
            .Where(i => i.Status == ReviewStatus.InVault && !string.IsNullOrWhiteSpace(i.MatchedModId))
            .Select(i => i.MatchedModId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modIds.Count == 0)
        {
            return;
        }

        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        int restored = 0;
        var skipped = new List<string>();
        foreach (string id in modIds)
        {
            ModEntry? mod = _state.Db.Mods.FirstOrDefault(m => m.Id == id);
            if (mod is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mod.PakFileName) && File.Exists(ModLibraryService.GetActivePakPath(_state.Settings, mod)))
            {
                skipped.Add(Loc.F("Review.CollisionOccupied", mod.DisplayName, mod.PakFileName));
                continue;
            }

            ModLibraryService.ActivateModFromVault(mod, _state.Settings);
            restored++;
        }

        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Review.RestoreBatchLog", restored, Path.GetFileName(_report.JsonFilePath)));
        if (skipped.Count > 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.F("Review.RestorePartialMsg", restored, string.Join("\n", skipped)),
                Loc.T("Review.RestorePartialTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        await AuditFileAsync(_report.JsonFilePath);
    }

    private async void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        string defaultName = Path.GetFileNameWithoutExtension(_report.JsonFilePath);
        if (string.IsNullOrWhiteSpace(defaultName))
        {
            defaultName = Loc.T("Profiles.ImportJson.DefaultName");
        }

        var prompt = new TextPromptDialog(
            Loc.T("Review.CreateProfile.Title"),
            Loc.F("Review.CreateProfile.Desc", Path.GetFileName(_report.JsonFilePath)),
            initialValue: defaultName,
            checkbox1Text: Loc.T("Profiles.ImportJson.Opt1"),
            checkbox1Default: true,
            checkbox2Text: Loc.T("Profiles.ImportJson.Opt2"),
            checkbox2Default: true)
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        string jsonPath = _report.JsonFilePath;
        string profileName = prompt.InputText;
        bool includeStandaloneOverrides = prompt.OptionChecked;
        bool includeLooseMods = prompt.Option2Checked;

        try
        {
            _state.SetBusy(true, Loc.T("Review.CreateProfile.Busy"));
            var (profile, missing) = await Task.Run(() => ModLibraryService.CreateOrUpdateProfileFromJsonAsync(
                jsonPath,
                profileName,
                includeStandaloneOverrides,
                includeLooseMods,
                _state.Settings,
                _state.Db,
                _state.Cache));

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Review.CreateProfile.Log", profile.Name, profile.ModIds.Count, missing.Count));
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Review.CreateProfile.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void OnInspectSelectedClick(object sender, RoutedEventArgs e)
    {
        if (AuditGrid.SelectedItem is not ReviewRow row)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(row.Item.MatchedModId))
        {
            ModEntry? mod = _state.Db.Mods.FirstOrDefault(m => m.Id == row.Item.MatchedModId);
            if (mod is not null)
            {
                new PakInspectorDialog(mod, _state.Settings) { Owner = Window.GetWindow(this) }.ShowDialog();
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(row.Item.PakFullPath) && File.Exists(row.Item.PakFullPath))
        {
            PakInfo live = LspkPakReader.ReadPakInfo(row.Item.PakFullPath);
            new PakInspectorDialog(live) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (width <= 0)
        {
            return;
        }

        KpiStrip.Columns = width >= 980 ? 6 : 3;

        if (width < 760)
        {
            Grid.SetRow(LsxStatusPill, 1);
            Grid.SetColumn(LsxStatusPill, 0);
            Grid.SetColumnSpan(LsxStatusPill, 2);
            LsxStatusPill.HorizontalAlignment = HorizontalAlignment.Left;
            LsxStatusPill.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            Grid.SetRow(LsxStatusPill, 0);
            Grid.SetColumn(LsxStatusPill, 1);
            Grid.SetColumnSpan(LsxStatusPill, 1);
            LsxStatusPill.HorizontalAlignment = HorizontalAlignment.Right;
            LsxStatusPill.Margin = new Thickness(0);
        }

        if (width < 960)
        {
            Grid.SetColumnSpan(SearchBox, 2);
            Grid.SetRow(FilterPanel, 1);
            Grid.SetColumn(FilterPanel, 0);
            Grid.SetColumnSpan(FilterPanel, 2);
            FilterPanel.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            Grid.SetColumnSpan(SearchBox, 1);
            Grid.SetRow(FilterPanel, 0);
            Grid.SetColumn(FilterPanel, 1);
            Grid.SetColumnSpan(FilterPanel, 1);
            FilterPanel.Margin = new Thickness(12, 0, 0, 0);
        }
    }
}
