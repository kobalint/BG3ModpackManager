using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BG3ModpackTools.App.Helpers;
using BG3ModpackTools.App.Services;
using BG3ModpackTools.App.Views.Dialogs;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;
using Microsoft.Win32;

namespace BG3ModpackTools.App.Views;

public partial class LibraryView : UserControl
{
    private sealed record OverrideModeOption(ManualOverrideMode Mode, string Label);

    private sealed class LibraryRow
    {
        public required ModEntry Mod { get; init; }
        public string ModId => Mod.Id;
        public string DisplayName => Mod.DisplayName;
        public string KindLabel => FormatHelper.FormatModKind(Mod.Kind);
        public string LocationLabel => FormatHelper.FormatModLocation(Mod.Location);
        public Brush LocationBg => FormatHelper.GetLocationBg(Mod.Location);
        public Brush LocationFg => FormatHelper.GetLocationFg(Mod.Location);

        public string OverrideLabel => FormatHelper.FormatOverrideLabel(Mod.EffectiveOverrideKind, Mod.ManualOverrideMode);
        public Brush OverrideBg => FormatHelper.GetOverrideBg(Mod.EffectiveOverrideKind);
        public Brush OverrideFg => FormatHelper.GetOverrideFg(Mod.EffectiveOverrideKind);

        public string VaultActionLabel => Mod.Location == ModLocation.Vault
            ? Loc.T("Common.ActivateFromVault")
            : Loc.T("Common.ToVault");

        public Visibility VaultActionVisibility => Mod.Location != ModLocation.Missing
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string OverrideTooltip => Mod.OverrideReasons.Count > 0
            ? string.Join("\n", Mod.OverrideReasons)
            : Loc.T("Common.NoBaseGameOverrides");

        public string FileSummary => !string.IsNullOrWhiteSpace(Mod.PakFileName)
            ? (Mod.TrackedFiles.Count > 0 ? $"{Mod.PakFileName} (+{Mod.TrackedFiles.Count} files)" : Mod.PakFileName)
            : Loc.F("Common.TrackedFilesCountFormat", Mod.TrackedFiles.Count);

        public string VersionText => Mod.VersionString ?? "-";
        public string SizeText => FormatHelper.FormatBytes(Mod.TotalSizeBytes);
        public required string ProfilesSummary { get; init; }
    }

    private readonly AppState _state;
    private List<LibraryRow> _allRows = new();
    private bool _suppressOverrideCombo;

    public event Action<string[]>? ArchiveImportBatchRequested;

    public LibraryView(AppState state)
    {
        InitializeComponent();
        _state = state;
        PopulateOverrideModeOptions();
        _state.StateChanged += RefreshView;
        ThemeManager.ThemeChanged += _ => RefreshView();
        LocalizationManager.Instance.LanguageChanged += () =>
        {
            PopulateOverrideModeOptions();
            RefreshView();
        };
        RefreshView();
    }

    private void PopulateOverrideModeOptions()
    {
        _suppressOverrideCombo = true;
        ManualOverrideMode currentMode = OverrideModeCombo.SelectedValue is ManualOverrideMode m
            ? m
            : ManualOverrideMode.Auto;

        OverrideModeCombo.ItemsSource = new List<OverrideModeOption>
        {
            new(ManualOverrideMode.Auto, Loc.T("Common.OverrideRule.Auto")),
            new(ManualOverrideMode.ForceOverride, Loc.T("Common.OverrideRule.ForceOverride")),
            new(ManualOverrideMode.ForceNonOverride, Loc.T("Common.OverrideRule.ForceNonOverride"))
        };
        OverrideModeCombo.SelectedValue = currentMode;
        _suppressOverrideCombo = false;
    }

    public void RefreshView()
    {
        var profilesByModId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (ModProfile p in _state.Db.Profiles)
        {
            foreach (string id in p.ModIds)
            {
                if (!profilesByModId.TryGetValue(id, out List<string>? list))
                {
                    list = new List<string>();
                    profilesByModId[id] = list;
                }

                if (!list.Contains(p.Name))
                {
                    list.Add(p.Name);
                }
            }
        }

        string noneLabel = Loc.T("Common.None");
        _allRows = _state.Db.Mods
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new LibraryRow
            {
                Mod = m,
                ProfilesSummary = profilesByModId.TryGetValue(m.Id, out List<string>? names) && names.Count > 0
                    ? string.Join(", ", names)
                    : noneLabel
            })
            .ToList();

        KpiTotal.Text = _state.Db.Mods.Count.ToString();
        KpiActive.Text = _state.Db.Mods.Count(m => m.Location == ModLocation.Active).ToString();
        KpiVault.Text = _state.Db.Mods.Count(m => m.Location == ModLocation.Vault).ToString();
        KpiOverrides.Text = _state.Db.Mods.Count(m => m.EffectiveIsOverride).ToString();
        KpiLoose.Text = _state.Db.Mods.Count(m => m.Kind is ModKind.LooseData or ModKind.LooseGame or ModKind.Hybrid).ToString();
        KpiSize.Text = FormatHelper.FormatBytes(_state.Db.Mods.Sum(m => m.TotalSizeBytes));

        ApplyFilter();
        UpdateRowButtons();
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
        IEnumerable<LibraryRow> filtered = _allRows;

        if (FilterPaks.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Mod.Kind == ModKind.Pak);
        }
        else if (FilterOverrides.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Mod.EffectiveIsOverride);
        }
        else if (FilterLoose.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Mod.Kind is ModKind.LooseData or ModKind.LooseGame or ModKind.Hybrid);
        }
        else if (FilterActive.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Mod.Location == ModLocation.Active);
        }
        else if (FilterVault.IsChecked == true)
        {
            filtered = filtered.Where(r => r.Mod.Location == ModLocation.Vault);
        }

        string q = SearchBox.Text.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(r =>
                r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.FileSummary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.ProfilesSummary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Mod.Uuid?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        LibraryGrid.ItemsSource = filtered.ToList();
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRowButtons();

    private void UpdateRowButtons()
    {
        var selectedRows = LibraryGrid.SelectedItems.OfType<LibraryRow>().ToList();
        ModEntry? primary = selectedRows.FirstOrDefault()?.Mod;

        InspectButton.IsEnabled = selectedRows.Count == 1;
        DeleteModButton.IsEnabled = selectedRows.Count == 1;
        OverrideModeCombo.IsEnabled = selectedRows.Count == 1;
        MoveToVaultButton.IsEnabled = selectedRows.Any(r => r.Mod.Location is ModLocation.Active or ModLocation.Partial);
        ActivateButton.IsEnabled = selectedRows.Any(r => r.Mod.Location is ModLocation.Vault or ModLocation.Partial);

        _suppressOverrideCombo = true;
        OverrideModeCombo.SelectedValue = primary?.ManualOverrideMode ?? ManualOverrideMode.Auto;
        _suppressOverrideCombo = false;
    }

    private void OnImportArchiveClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Library.ImportDialog.Title"),
            Filter = Loc.T("Library.ImportDialog.Filter"),
            Multiselect = true
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true && dlg.FileNames.Length > 0)
        {
            ArchiveImportBatchRequested?.Invoke(dlg.FileNames);
        }
    }

    private async void OnScanActiveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _state.SetBusy(true, Loc.T("Library.ScanBusy"));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            var (adopted, updated) = await Task.Run(() => ModLibraryService.ScanAndAdoptActiveModsAsync(
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress));

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Library.ScanLog", adopted, updated));
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Library.ScanErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private async void OnBulkImportClick(object sender, RoutedEventArgs e)
    {
        string? suggested = null;
        if (!string.IsNullOrWhiteSpace(_state.Settings.ModsFolder))
        {
            string? bg3User = Path.GetDirectoryName(_state.Settings.ModsFolder);
            if (!string.IsNullOrWhiteSpace(bg3User))
            {
                string modsOff = Path.Combine(bg3User, "Mods-off");
                if (Directory.Exists(modsOff))
                {
                    suggested = modsOff;
                }
            }
        }

        var dlg = new BulkImportDialog(suggested) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        string selectedFolder = dlg.SelectedFolder;
        bool moveInsteadOfCopy = dlg.MoveInsteadOfCopy;

        if (moveInsteadOfCopy && !_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        try
        {
            _state.SetBusy(true, Loc.F("Library.BulkBusy", Path.GetFileName(selectedFolder)));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            var (imported, duplicates, errors) = await Task.Run(() => ModLibraryService.BulkImportFromBackupFolderAsync(
                selectedFolder,
                _state.Settings,
                _state.Db,
                _state.Cache,
                moveInsteadOfCopy,
                progress));

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Library.BulkLog", imported, duplicates, errors.Count));
            _state.NotifyStateChanged();

            string summary = Loc.F("Library.BulkSummary", imported, duplicates);
            if (errors.Count > 0)
            {
                summary += Loc.T("Library.BulkErrorsHeader") + string.Join("\n", errors.Take(10));
            }

            MessageBox.Show(Window.GetWindow(this), summary, Loc.T("Library.BulkCompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Library.BulkErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private async void OnRegisterLooseFolderClick(object sender, RoutedEventArgs e)
    {
        var folderDlg = new OpenFolderDialog
        {
            Title = Loc.T("Library.RegisterFolder.DialogTitle"),
            InitialDirectory = Directory.Exists(_state.Settings.DataFolder) ? _state.Settings.DataFolder : null
        };

        if (folderDlg.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        string chosen = folderDlg.FolderName;
        TrackedFileTarget target;
        if (!string.IsNullOrWhiteSpace(_state.Settings.DataFolder) &&
            FileTransactionHelper.IsPathStrictlyInsideRoot(chosen, _state.Settings.DataFolder))
        {
            target = TrackedFileTarget.DataFolder;
        }
        else if (!string.IsNullOrWhiteSpace(_state.Settings.GameFolder) &&
                 FileTransactionHelper.IsPathStrictlyInsideRoot(chosen, _state.Settings.GameFolder))
        {
            target = TrackedFileTarget.GameFolder;
        }
        else
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.T("Library.RegisterFolder.InvalidMsg"),
                Loc.T("Library.RegisterFolder.InvalidTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var prompt = new TextPromptDialog(
            Loc.T("Library.RegisterFolder.PromptTitle"),
            Loc.F("Library.RegisterFolder.PromptDesc", Path.GetFileName(chosen), target),
            Path.GetFileName(chosen))
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        string displayName = prompt.InputText;

        try
        {
            _state.SetBusy(true, Loc.F("Library.RegisterFolder.Busy", Path.GetFileName(chosen)));
            ModEntry entry = await Task.Run(() => ModLibraryService.RegisterExistingLooseFolderAsync(
                chosen,
                target,
                displayName,
                _state.Settings,
                _state.Db));

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Library.RegisterFolder.Log", entry.DisplayName, entry.TrackedFiles.Count));
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Library.RegisterFolder.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void OnReconcileClick(object sender, RoutedEventArgs e)
    {
        int changed = ModLibraryService.ReconcileDiskState(_state.Settings, _state.Db);
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Library.ReconcileLog", changed));
        _state.NotifyStateChanged();
    }

    private void OnRowVaultActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not LibraryRow row)
        {
            return;
        }

        if (row.Mod.Location == ModLocation.Vault)
        {
            ActivateMods(new[] { row.Mod });
        }
        else if (row.Mod.Location is ModLocation.Active or ModLocation.Partial)
        {
            MoveModsToVault(new[] { row.Mod });
        }
    }

    private void OnMoveToVaultClick(object sender, RoutedEventArgs e)
    {
        var mods = LibraryGrid.SelectedItems.OfType<LibraryRow>()
            .Select(r => r.Mod)
            .Where(m => m.Location is ModLocation.Active or ModLocation.Partial)
            .ToList();

        if (mods.Count > 0)
        {
            MoveModsToVault(mods);
        }
    }

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        var mods = LibraryGrid.SelectedItems.OfType<LibraryRow>()
            .Select(r => r.Mod)
            .Where(m => m.Location is ModLocation.Vault or ModLocation.Partial)
            .ToList();

        if (mods.Count > 0)
        {
            ActivateMods(mods);
        }
    }

    private void MoveModsToVault(IReadOnlyList<ModEntry> mods)
    {
        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        try
        {
            foreach (ModEntry mod in mods)
            {
                ModLibraryService.MoveModToVault(mod, _state.Settings);
                _state.Log(Loc.F("Review.MovedOneLog", mod.DisplayName));
            }

            _state.SaveLibraryAndCache();
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Library.MoveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ActivateMods(IReadOnlyList<ModEntry> mods)
    {
        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        try
        {
            var skipped = new List<string>();
            int activated = 0;

            foreach (ModEntry mod in mods)
            {
                if (!string.IsNullOrWhiteSpace(mod.PakFileName) &&
                    File.Exists(ModLibraryService.GetActivePakPath(_state.Settings, mod)))
                {
                    skipped.Add(Loc.F("Library.CollisionMsg", mod.DisplayName, mod.PakFileName));
                    continue;
                }

                ModLibraryService.ActivateModFromVault(mod, _state.Settings);
                _state.Log(Loc.F("Profiles.VaultMove.ActivatedLog", mod.DisplayName));
                activated++;
            }

            _state.SaveLibraryAndCache();
            _state.NotifyStateChanged();

            if (skipped.Count > 0)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    Loc.F("Library.ActivateCollisionMsg", activated, string.Join("\n", skipped)),
                    Loc.T("Profiles.VaultMove.CollisionTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Library.ActivateErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOverrideModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverrideCombo || LibraryGrid.SelectedItem is not LibraryRow row)
        {
            return;
        }

        if (OverrideModeCombo.SelectedValue is ManualOverrideMode mode && row.Mod.ManualOverrideMode != mode)
        {
            row.Mod.ManualOverrideMode = mode;
            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Library.OverrideRuleUpdatedLog", row.DisplayName, mode));
            _state.NotifyStateChanged();
        }
    }

    private void OnInspectClick(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            return;
        }

        new PakInspectorDialog(row.Mod, _state.Settings) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnDeleteModClick(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            return;
        }

        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        ModEntry mod = row.Mod;
        string restoreSummary = mod.ReplacedOriginalFiles.Count > 0
            ? Loc.T("Library.Uninstall.OriginalsHeader") +
              string.Join("\n", mod.ReplacedOriginalFiles.Select(r => $"• {r.RelativePath} ({r.BackupRelativePath})"))
            : string.Empty;

        MessageBoxResult confirm = MessageBox.Show(
            Window.GetWindow(this),
            Loc.F("Library.Uninstall.ConfirmMsg", mod.DisplayName, FormatHelper.FormatModLocation(mod.Location), restoreSummary),
            Loc.T("Library.Uninstall.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            ModLibraryService.ModUninstallResult uninstallResult = ModLibraryService.UninstallMod(mod, _state.Settings, _state.Db);
            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Library.Uninstall.Log", mod.DisplayName, uninstallResult.RemovedFilesCount, uninstallResult.RestoredOriginalFiles.Count));
            _state.NotifyStateChanged();

            if (uninstallResult.RestoredOriginalFiles.Count > 0 || uninstallResult.Warnings.Count > 0)
            {
                string msg = Loc.F("Library.Uninstall.CompleteMsg", mod.DisplayName);
                if (uninstallResult.RestoredOriginalFiles.Count > 0)
                {
                    msg += Loc.T("Library.Uninstall.RestoredHeader") + string.Join("\n", uninstallResult.RestoredOriginalFiles.Select(f => $"• {f}"));
                }

                if (uninstallResult.Warnings.Count > 0)
                {
                    msg += Loc.T("Library.Uninstall.WarningsHeader") + string.Join("\n", uninstallResult.Warnings);
                }

                MessageBox.Show(Window.GetWindow(this), msg, Loc.T("Library.Uninstall.CompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Library.Uninstall.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (width <= 0)
        {
            return;
        }

        if (width < 1100)
        {
            Grid.SetRow(ToolbarButtonsPanel, 1);
            Grid.SetColumn(ToolbarButtonsPanel, 0);
            Grid.SetColumnSpan(ToolbarButtonsPanel, 2);
            ToolbarButtonsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            ToolbarButtonsPanel.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            Grid.SetRow(ToolbarButtonsPanel, 0);
            Grid.SetColumn(ToolbarButtonsPanel, 1);
            Grid.SetColumnSpan(ToolbarButtonsPanel, 1);
            ToolbarButtonsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            ToolbarButtonsPanel.Margin = new Thickness(0);
        }

        KpiStrip.Columns = width >= 960 ? 6 : 3;

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
