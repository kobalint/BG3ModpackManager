using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BG3ModpackManager.App.Helpers;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.App.Views.Dialogs;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;
using Microsoft.Win32;

namespace BG3ModpackManager.App.Views;

public partial class ProfilesView : UserControl
{
    private sealed class ProfileListItem
    {
        public required ModProfile Profile { get; init; }
        public required bool IsActive { get; init; }
        public required string SummaryLine { get; init; }
        public string Name => Profile.Name;
        public Brush ItemBorderBrush => ThemeManager.GetBrush(IsActive ? "BrushGreen" : "BrushBorder");
    }

    private sealed class ProfileModRow
    {
        public int OrderNumber { get; init; }
        public required string ModId { get; init; }
        public ModEntry? Mod { get; init; }

        public string DisplayName => Mod?.DisplayName ?? Loc.F("Profiles.MissingLibraryModFormat", ModId);
        public string KindLabel => Mod is null ? "-" : FormatHelper.FormatModKind(Mod.Kind);

        public string OverrideLabel => Mod is null
            ? "-"
            : FormatHelper.FormatOverrideLabel(Mod.EffectiveOverrideKind);

        public Brush OverrideBg => Mod is null
            ? ThemeManager.GetBrush("BrushBadgeNeutralBg")
            : FormatHelper.GetOverrideBg(Mod.EffectiveOverrideKind);

        public Brush OverrideFg => Mod is null
            ? ThemeManager.GetBrush("BrushBadgeNeutralFg")
            : FormatHelper.GetOverrideFg(Mod.EffectiveOverrideKind);

        public string OverrideTooltip => Mod is null
            ? Loc.T("Common.Location.Missing")
            : (Mod.OverrideReasons.Count > 0 ? string.Join("\n", Mod.OverrideReasons) : Loc.T("Common.NoBaseGameOverrides"));

        public string VaultActionLabel => Mod?.Location == ModLocation.Vault
            ? Loc.T("Common.ActivateFromVault")
            : Loc.T("Common.ToVault");

        public Visibility VaultActionVisibility => Mod is not null && Mod.Location != ModLocation.Missing
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string LocationLabel => Mod is null ? Loc.T("Common.Location.Missing") : FormatHelper.FormatModLocation(Mod.Location);
        public Brush LocationBg => Mod is null ? ThemeManager.GetBrush("BrushBadgeCrimsonBg") : FormatHelper.GetLocationBg(Mod.Location);
        public Brush LocationFg => Mod is null ? ThemeManager.GetBrush("BrushCrimson") : FormatHelper.GetLocationFg(Mod.Location);

        public string VersionText => Mod?.VersionString ?? "-";
        public string SizeText => Mod is null ? "-" : FormatHelper.FormatBytes(Mod.TotalSizeBytes);
        public string FileSummary => Mod is null
            ? ModId
            : (!string.IsNullOrWhiteSpace(Mod.PakFileName)
                ? (Mod.TrackedFiles.Count > 0 ? $"{Mod.PakFileName} (+{Mod.TrackedFiles.Count} files)" : Mod.PakFileName)
                : Loc.F("Common.TrackedFilesCountFormat", Mod.TrackedFiles.Count));
    }

    private const string InternalModReorderDragFormat = "BG3ModpackManager.ProfileModReorder";

    private readonly AppState _state;
    private string? _selectedProfileId;
    private bool _suppressSelectionChanged;
    private bool? _lastStackedMode;
    private bool? _lastCompactHeader;
    private double _savedLeftPaneWidth = 280;
    private Point _dragStartPoint;
    private ProfileModRow? _dragSourceRow;
    private bool _deferSingleSelectOnMouseUp;
    private bool _isDraggingRows;
    private DateTime _lastAutoScrollUtc = DateTime.MinValue;

    public ProfilesView(AppState state)
    {
        InitializeComponent();
        _state = state;
        _state.StateChanged += RefreshView;
        ThemeManager.ThemeChanged += _ => RefreshView();
        LocalizationManager.Instance.LanguageChanged += RefreshView;
        RefreshView();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (width <= 0)
        {
            return;
        }

        bool stacked = width < 820;
        bool compactHeader = (!stacked && width < 980) || width < 660;

        if (_lastStackedMode != stacked)
        {
            _lastStackedMode = stacked;
            if (stacked)
            {
                if (LeftPaneColumn.Width.IsAbsolute && LeftPaneColumn.Width.Value >= 240)
                {
                    _savedLeftPaneWidth = Math.Clamp(LeftPaneColumn.Width.Value, 240, 400);
                }

                ProfilesSubtitleText.Visibility = Visibility.Collapsed;
                ProfilesListBox.MaxHeight = 88;
                ProfileActionsGrid.Columns = 4;
                ProfilesSplitter.Visibility = Visibility.Collapsed;
                LeftPaneColumn.MinWidth = 0;
                LeftPaneColumn.Width = new GridLength(0);
                SplitterColumn.Width = new GridLength(0);

                Grid.SetRow(ProfilesListCard, 0);
                Grid.SetRowSpan(ProfilesListCard, 1);
                Grid.SetColumn(ProfilesListCard, 0);
                Grid.SetColumnSpan(ProfilesListCard, 3);
                ProfilesListCard.Padding = new Thickness(10, 8, 10, 8);
                ProfilesListCard.Margin = new Thickness(0, 0, 0, 8);

                Grid.SetRow(ProfileDetailsGrid, 2);
                Grid.SetRowSpan(ProfileDetailsGrid, 1);
                Grid.SetColumn(ProfileDetailsGrid, 0);
                Grid.SetColumnSpan(ProfileDetailsGrid, 3);
            }
            else
            {
                ProfilesSubtitleText.Visibility = Visibility.Visible;
                ProfilesListBox.ClearValue(MaxHeightProperty);
                ProfileActionsGrid.Columns = 2;
                ProfilesSplitter.Visibility = Visibility.Visible;
                LeftPaneColumn.MinWidth = 240;
                LeftPaneColumn.Width = new GridLength(_savedLeftPaneWidth);
                SplitterColumn.Width = new GridLength(10);

                Grid.SetRow(ProfilesListCard, 0);
                Grid.SetRowSpan(ProfilesListCard, 3);
                Grid.SetColumn(ProfilesListCard, 0);
                Grid.SetColumnSpan(ProfilesListCard, 1);
                ProfilesListCard.Padding = new Thickness(12);
                ProfilesListCard.Margin = new Thickness(0);

                Grid.SetRow(ProfileDetailsGrid, 0);
                Grid.SetRowSpan(ProfileDetailsGrid, 3);
                Grid.SetColumn(ProfileDetailsGrid, 2);
                Grid.SetColumnSpan(ProfileDetailsGrid, 1);
            }
        }

        if (_lastCompactHeader != compactHeader)
        {
            _lastCompactHeader = compactHeader;
            if (compactHeader)
            {
                Grid.SetRow(SwitchProfileButton, 1);
                Grid.SetColumn(SwitchProfileButton, 0);
                Grid.SetColumnSpan(SwitchProfileButton, 2);
                SwitchProfileButton.HorizontalAlignment = HorizontalAlignment.Left;
                SwitchProfileButton.Margin = new Thickness(0, 6, 0, 0);
            }
            else
            {
                Grid.SetRow(SwitchProfileButton, 0);
                Grid.SetColumn(SwitchProfileButton, 1);
                Grid.SetColumnSpan(SwitchProfileButton, 1);
                SwitchProfileButton.HorizontalAlignment = HorizontalAlignment.Right;
                SwitchProfileButton.Margin = new Thickness(8, 0, 0, 0);
            }
        }
    }

    public void RefreshView()
    {
        var modMap = _state.Db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var items = _state.Db.Profiles
            .OrderByDescending(p => p.Id == _state.Db.ActiveProfileId)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                int ovr = p.ModIds.Count(id => modMap.TryGetValue(id, out ModEntry? m) && m.EffectiveIsOverride);
                int loose = p.ModIds.Count(id => modMap.TryGetValue(id, out ModEntry? m) && m.TrackedFiles.Count > 0);
                return new ProfileListItem
                {
                    Profile = p,
                    IsActive = p.Id == _state.Db.ActiveProfileId,
                    SummaryLine = Loc.F("Profiles.SummaryFormat", p.ModIds.Count, ovr, loose)
                };
            })
            .ToList();

        _suppressSelectionChanged = true;
        ProfilesListBox.ItemsSource = items;

        if (string.IsNullOrWhiteSpace(_selectedProfileId) || items.All(i => i.Profile.Id != _selectedProfileId))
        {
            _selectedProfileId = _state.Db.ActiveProfileId ?? items.FirstOrDefault()?.Profile.Id;
        }

        ProfilesListBox.SelectedItem = items.FirstOrDefault(i => i.Profile.Id == _selectedProfileId);
        _suppressSelectionChanged = false;

        RefreshSelectedProfileDetails(modMap);
    }

    private ModProfile? GetSelectedProfile()
    {
        return _state.Db.Profiles.FirstOrDefault(p => p.Id == _selectedProfileId);
    }

    private void RefreshSelectedProfileDetails(Dictionary<string, ModEntry>? modMap = null)
    {
        modMap ??= _state.Db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        ModProfile? profile = GetSelectedProfile();

        bool hasProfile = profile is not null;
        SwitchProfileButton.IsEnabled = hasProfile;
        DuplicateButton.IsEnabled = hasProfile;
        RenameButton.IsEnabled = hasProfile;
        DeleteButton.IsEnabled = hasProfile;
        LinkJsonButton.IsEnabled = hasProfile;
        SyncJsonButton.IsEnabled = hasProfile && !string.IsNullOrWhiteSpace(profile?.LinkedOrderJsonPath);
        ExportJsonButton.IsEnabled = hasProfile;
        ExportArchiveButton.IsEnabled = hasProfile;
        AddModsButton.IsEnabled = hasProfile;
        RemoveModButton.IsEnabled = hasProfile;
        MoveUpButton.IsEnabled = hasProfile;
        MoveDownButton.IsEnabled = hasProfile;
        InspectModButton.IsEnabled = hasProfile;

        if (profile is null)
        {
            SelectedProfileTitle.Text = Loc.T("Profiles.NoProfileSelected");
            SelectedProfileSubtitle.Text = Loc.T("Profiles.NoProfileSubtitle");
            SelectedActivePill.Visibility = Visibility.Collapsed;
            ProfileModsGrid.ItemsSource = null;
            ProfileModsCountText.Text = string.Empty;
            return;
        }

        bool isActive = profile.Id == _state.Db.ActiveProfileId;
        SelectedActivePill.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        SelectedProfileTitle.Text = profile.Name;
        SelectedProfileSubtitle.Text = string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath)
            ? Loc.F("Profiles.NotLinkedFormat", profile.UpdatedAtUtc.ToLocalTime().ToString("g"))
            : Loc.F("Profiles.LinkedFormat", profile.LinkedOrderJsonPath);

        string query = ProfileSearchBox.Text.Trim();
        var rows = new List<ProfileModRow>();
        for (int i = 0; i < profile.ModIds.Count; i++)
        {
            string modId = profile.ModIds[i];
            modMap.TryGetValue(modId, out ModEntry? mod);
            var row = new ProfileModRow
            {
                OrderNumber = i + 1,
                ModId = modId,
                Mod = mod
            };

            if (string.IsNullOrEmpty(query) ||
                row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.FileSummary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (mod?.Uuid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                rows.Add(row);
            }
        }

        ProfileModsGrid.ItemsSource = rows;
        ProfileModsCountText.Text = Loc.F("Profiles.CountFormat", rows.Count, profile.ModIds.Count);
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (ProfilesListBox.SelectedItem is ProfileListItem item)
        {
            _selectedProfileId = item.Profile.Id;
            RefreshSelectedProfileDetails();
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefreshSelectedProfileDetails();

    private async void OnCaptureCurrentClick(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptDialog(
            Loc.T("Profiles.Capture.Title"),
            Loc.T("Profiles.Capture.Desc"),
            initialValue: Loc.T("Profiles.Capture.DefaultName"),
            checkbox1Text: Loc.T("Profiles.Capture.Opt1"),
            checkbox1Default: true,
            checkbox2Text: Loc.T("Profiles.Capture.Opt2"),
            checkbox2Default: true)
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        string profileName = prompt.InputText;
        bool useModSettingsOrder = prompt.OptionChecked;
        bool includeExtras = prompt.Option2Checked;

        try
        {
            _state.SetBusy(true, Loc.T("Profiles.Capture.Busy"));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            ModProfile profile = await Task.Run(() => ModLibraryService.CaptureActiveProfileFromModSettingsAsync(
                profileName,
                useModSettingsOrder,
                includeExtras,
                includeExtras,
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress));

            _state.Db.ActiveProfileId = profile.Id;
            _selectedProfileId = profile.Id;
            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Profiles.Capture.Log", profile.Name, profile.ModIds.Count));
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Profiles.Capture.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private async void OnImportJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Profiles.ImportJson.DialogTitle"),
            Filter = Loc.T("Profiles.ImportJson.Filter"),
            InitialDirectory = Directory.Exists(_state.Settings.OrdersFolder) ? _state.Settings.OrdersFolder : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        string defaultName = Path.GetFileNameWithoutExtension(dlg.FileName);
        if (string.IsNullOrWhiteSpace(defaultName))
        {
            defaultName = Loc.T("Profiles.ImportJson.DefaultName");
        }

        var prompt = new TextPromptDialog(
            Loc.T("Profiles.ImportJson.PromptTitle"),
            Loc.F("Profiles.ImportJson.PromptDesc", Path.GetFileName(dlg.FileName)),
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

        string jsonPath = dlg.FileName;
        string profileName = prompt.InputText;
        bool includeStandaloneOverrides = prompt.OptionChecked;
        bool includeLooseMods = prompt.Option2Checked;

        try
        {
            _state.SetBusy(true, Loc.T("Profiles.ImportJson.Busy"));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            var (profile, missing) = await Task.Run(() => ModLibraryService.CreateOrUpdateProfileFromJsonAsync(
                jsonPath,
                profileName,
                includeStandaloneOverrides,
                includeLooseMods,
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress: progress));

            _selectedProfileId = profile.Id;
            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Profiles.ImportJson.Log", profile.Name, profile.ModIds.Count, missing.Count));
            _state.NotifyStateChanged();

            if (missing.Count > 0)
            {
                string missingList = string.Join("\n", missing.Take(12)) + (missing.Count > 12 ? "\n..." : "");
                MessageBox.Show(
                    Window.GetWindow(this),
                    Loc.F("Profiles.ImportJson.PartialMsg", profile.Name, profile.ModIds.Count, missing.Count, missingList),
                    Loc.T("Profiles.ImportJson.PartialTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Profiles.ImportJson.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private async void OnImportModpackClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Profiles.ImportPack.DialogTitle"),
            Filter = Loc.T("Profiles.ImportPack.Filter"),
            InitialDirectory = !string.IsNullOrWhiteSpace(_state.Settings.LastExportFolder) &&
                               Directory.Exists(_state.Settings.LastExportFolder)
                ? _state.Settings.LastExportFolder
                : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        _ = await ImportModpackArchiveAsync(dlg.FileName);
    }

    public async Task<bool> ImportModpackArchiveAsync(string archivePath)
    {
        ModpackImportPlan? plan = null;
        try
        {
            string archiveFileName = Path.GetFileName(archivePath);
            _state.SetBusy(true, Loc.F("Profiles.ImportPack.InspectBusy", archiveFileName));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            plan = await Task.Run(() => ProfileImportService.InspectModpackArchiveAsync(
                archivePath,
                _state.Settings,
                _state.Db,
                progress));
            _state.SetBusy(false);

            if (plan.Items.Count == 0)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    Loc.F("Profiles.ImportPack.EmptyMsg", archiveFileName),
                    Loc.T("Profiles.ImportPack.ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            var dlg = new ImportModpackDialog(plan, _state.Db)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true)
            {
                return false;
            }

            string profileName = dlg.ProfileName;
            bool installActive = dlg.InstallActiveNow;

            if (installActive && !_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
            {
                _state.Log(Loc.F("Profiles.ImportPack.CancelledBg3Running", plan.SourceName));
                return false;
            }

            _state.SetBusy(true, Loc.F("Profiles.ImportPack.CommitBusy", profileName));
            ModpackImportPlan capturedPlan = plan;
            ModpackImportResult res = await Task.Run(() => ProfileImportService.CommitModpackImportAsync(
                capturedPlan,
                new ModpackImportOptions
                {
                    ProfileName = profileName,
                    InstallActiveNow = installActive,
                    SetAsActiveProfile = installActive
                },
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress));

            _state.SaveLibraryAndCache();
            if (res.CreatedProfile is not null)
            {
                _selectedProfileId = res.CreatedProfile.Id;
            }

            _state.Log(Loc.F(
                "Profiles.ImportPack.LogSuccess",
                profileName,
                res.ImportedModCount,
                res.ReusedModCount,
                res.ImportedPakCount,
                res.ImportedDataFileCount,
                res.ImportedGameFileCount,
                res.BackedUpOriginalGameFiles.Count));
            _state.NotifyStateChanged();

            if (res.Errors.Count > 0)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    string.Join("\n", res.Errors),
                    Loc.T("Profiles.ImportPack.ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            string successMsg = Loc.F(
                "Profiles.ImportPack.SuccessMsg",
                profileName,
                res.ImportedModCount,
                res.ReusedModCount,
                res.ImportedPakCount,
                res.ImportedDataFileCount,
                res.ImportedGameFileCount,
                res.BackedUpOriginalGameFiles.Count);
            if (res.Warnings.Count > 0)
            {
                successMsg += "\n\n" + string.Join("\n", res.Warnings.Take(8));
            }

            MessageBox.Show(
                Window.GetWindow(this),
                successMsg,
                Loc.T("Profiles.ImportPack.SuccessTitle"),
                MessageBoxButton.OK,
                res.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                Loc.T("Profiles.ImportPack.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _state.SetBusy(false);
            if (plan is not null)
            {
                ProfileImportService.CleanupStaging(plan);
            }
        }
    }

    private void OnNewEmptyClick(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptDialog(
            Loc.T("Profiles.NewEmpty.Title"),
            Loc.T("Profiles.NewEmpty.Desc"),
            Loc.T("Profiles.NewEmpty.DefaultName"))
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var profile = new ModProfile { Name = prompt.InputText };
        _state.Db.Profiles.Add(profile);
        _selectedProfileId = profile.Id;
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.NewEmpty.Log", profile.Name));
        _state.NotifyStateChanged();
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        ModProfile? src = GetSelectedProfile();
        if (src is null)
        {
            return;
        }

        var prompt = new TextPromptDialog(
            Loc.T("Profiles.Duplicate.Title"),
            Loc.F("Profiles.Duplicate.Desc", src.Name),
            Loc.F("Profiles.Duplicate.DefaultName", src.Name))
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var copy = new ModProfile
        {
            Name = prompt.InputText,
            Description = src.Description,
            LinkedOrderJsonPath = src.LinkedOrderJsonPath,
            ModIds = src.ModIds.ToList()
        };
        _state.Db.Profiles.Add(copy);
        _selectedProfileId = copy.Id;
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.Duplicate.Log", src.Name, copy.Name));
        _state.NotifyStateChanged();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        var prompt = new TextPromptDialog(Loc.T("Profiles.Rename.Title"), Loc.T("Profiles.Rename.Desc"), profile.Name)
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        profile.Name = prompt.InputText;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.Rename.Log", profile.Name));
        _state.NotifyStateChanged();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        if (MessageBox.Show(
                Window.GetWindow(this),
                Loc.F("Profiles.Delete.Msg", profile.Name),
                Loc.T("Profiles.Delete.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _state.Db.Profiles.Remove(profile);
        if (_state.Db.ActiveProfileId == profile.Id)
        {
            _state.Db.ActiveProfileId = null;
        }

        _selectedProfileId = _state.Db.Profiles.FirstOrDefault()?.Id;
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.Delete.Log", profile.Name));
        _state.NotifyStateChanged();
    }

    private void OnLinkJsonClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Profiles.LinkJson.Title"),
            Filter = Loc.T("Profiles.ImportJson.Filter"),
            InitialDirectory = Directory.Exists(_state.Settings.OrdersFolder) ? _state.Settings.OrdersFolder : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            List<string> existingLinkedCompanions = ModLibraryService.GetLinkedCompanionModIdsForOrderJson(_state.Db, dlg.FileName, profile);
            var seen = new HashSet<string>(profile.ModIds, StringComparer.OrdinalIgnoreCase);
            foreach (string companionId in existingLinkedCompanions)
            {
                if (seen.Add(companionId))
                {
                    profile.ModIds.Add(companionId);
                }
            }

            profile.LinkedOrderJsonPath = dlg.FileName;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            ModLibraryService.LinkCompanionModsToOrderJson(_state.Db, profile.LinkedOrderJsonPath, profile.ModIds);
            ModLibraryService.ReconcileActiveProfileId(_state.Settings, _state.Db, preferredProfileId: profile.Id);
            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Profiles.LinkJson.Log", profile.Name, Path.GetFileName(dlg.FileName)));
            _state.NotifyStateChanged();
        }
    }

    private async void OnSyncJsonClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null || string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath) || !File.Exists(profile.LinkedOrderJsonPath))
        {
            MessageBox.Show(Window.GetWindow(this), Loc.T("Profiles.Sync.NotFoundMsg"), Loc.T("Profiles.Sync.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _state.SetBusy(true, Loc.T("Profiles.Sync.Busy"));
            var (_, missing) = await Task.Run(() => ModLibraryService.CreateOrUpdateProfileFromJsonAsync(
                profile.LinkedOrderJsonPath,
                profile.Name,
                includeActiveStandaloneOverrides: false,
                includeActiveLooseMods: false,
                _state.Settings,
                _state.Db,
                _state.Cache,
                existingProfile: profile));

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("Profiles.Sync.Log", profile.Name, Path.GetFileName(profile.LinkedOrderJsonPath), profile.ModIds.Count, missing.Count));
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Profiles.Sync.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = Loc.T("Profiles.ExportJson.DialogTitle"),
            Filter = Loc.T("Profiles.ExportJson.Filter"),
            FileName = $"{profile.Name}.json",
            InitialDirectory = Directory.Exists(_state.Settings.OrdersFolder) ? _state.Settings.OrdersFolder : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var modMap = _state.Db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var entries = profile.ModIds
            .Select(id => modMap.TryGetValue(id, out ModEntry? m) ? m : null)
            .Where(m => m is not null &&
                        !string.IsNullOrWhiteSpace(m.Uuid) &&
                        m.HasMetaLsx &&
                        m.EffectiveOverrideKind != OverrideKind.StandaloneOverride)
            .Cast<ModEntry>()
            .Select(m => new Bg3OrderEntry { Uuid = m.Uuid!, Name = m.DisplayName })
            .ToList();

        Bg3LoadOrderService.SaveOrderJson(dlg.FileName, entries);
        if (string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath))
        {
            profile.LinkedOrderJsonPath = dlg.FileName;
        }

        ModLibraryService.LinkCompanionModsToOrderJson(_state.Db, dlg.FileName, profile.ModIds);
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.ExportJson.Log", profile.Name, entries.Count, dlg.FileName));
        _state.NotifyStateChanged();
        MessageBox.Show(
            Window.GetWindow(this),
            Loc.F("Profiles.ExportJson.CompleteMsg", entries.Count, dlg.FileName),
            Loc.T("Profiles.ExportJson.CompleteTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OnExportArchiveClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        ProfileExportPreview preview = ProfileExportService.BuildExportPreview(profile, _state.Settings, _state.Db);
        if (!preview.SevenZipAvailable)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.T("Profiles.ExportArchive.No7ZipMsg"),
                Loc.T("Profiles.ExportArchive.No7ZipTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string defaultFolder = !string.IsNullOrWhiteSpace(_state.Settings.LastExportFolder) &&
                               Directory.Exists(_state.Settings.LastExportFolder)
            ? _state.Settings.LastExportFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string safeName = ProfileExportService.SanitizeFileName(profile.Name);
        string defaultOutputPath = Path.Combine(defaultFolder, $"{safeName}.7z");

        var dlg = new ExportProfileArchiveDialog(preview, defaultOutputPath)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var options = new ProfileExportOptions
        {
            OutputArchivePath = dlg.OutputArchivePath,
            IncludePaks = dlg.IncludePaks,
            IncludeLooseMods = dlg.IncludeLooseMods,
            IncludeGeneratedFolder = dlg.IncludeGeneratedFolder,
            SelectedGeneratedGroupKeys = dlg.SelectedGeneratedGroupKeys,
            IncludeBinMods = dlg.IncludeBinMods,
            IncludeOrderJson = dlg.IncludeOrderJson,
            IncludeManifestAndReadme = dlg.IncludeManifestAndReadme,
            CompressionLevel = dlg.CompressionLevel
        };

        try
        {
            _state.SetBusy(true, Loc.F("Profiles.ExportArchive.Busy", profile.Name));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            ProfileExportResult result = await Task.Run(() => ProfileExportService.ExportProfileTo7zAsync(
                profile,
                options,
                _state.Settings,
                _state.Db,
                progress));

            string? savedDir = Path.GetDirectoryName(result.OutputArchivePath);
            if (!string.IsNullOrWhiteSpace(savedDir))
            {
                _state.Settings.LastExportFolder = savedDir;
                _state.Store.SaveSettings(_state.Settings);
            }

            _state.Log(Loc.F(
                "Profiles.ExportArchive.Log",
                profile.Name,
                result.OutputArchivePath,
                FormatHelper.FormatBytes(result.ArchiveSizeBytes),
                result.ExportedPakCount,
                result.ExportedDataFileCount,
                result.ExportedGameFileCount));

            string yesNoText = result.IncludedOrderJson ? Loc.T("Profiles.ExportArchive.Yes") : Loc.T("Profiles.ExportArchive.No");
            string summary = Loc.F(
                "Profiles.ExportArchive.CompleteMsg",
                profile.Name,
                result.OutputArchivePath,
                FormatHelper.FormatBytes(result.ArchiveSizeBytes),
                result.ExportedPakCount,
                result.ExportedDataFileCount,
                result.ExportedGameFileCount,
                yesNoText);

            if (result.Warnings.Count > 0)
            {
                summary += Loc.T("Profiles.ExportArchive.WarningsHeader") + string.Join("\n", result.Warnings.Take(8));
                if (result.Warnings.Count > 8)
                {
                    summary += "\n...";
                }
            }

            summary += Loc.T("Profiles.ExportArchive.OpenFolderQuestion");

            if (MessageBox.Show(
                    Window.GetWindow(this),
                    summary,
                    Loc.T("Profiles.ExportJson.CompleteTitle"),
                    MessageBoxButton.YesNo,
                    result.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.OutputArchivePath}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Profiles.ExportArchive.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    public async void SwitchToProfile(ModProfile profile)
    {
        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        try
        {
            _state.SetBusy(true, Loc.F("Profiles.Switch.PlanBusy", profile.Name));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            SwitchPlan plan = await Task.Run(() => ProfileSwitchService.BuildSwitchPlanAsync(
                profile,
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress));

            _state.SetBusy(false);

            var preview = new SwitchPreviewDialog(plan)
            {
                Owner = Window.GetWindow(this)
            };

            if (preview.ShowDialog() != true)
            {
                return;
            }

            _state.SetBusy(true, Loc.F("Profiles.Switch.ExecBusy", profile.Name));
            SwitchExecutionResult res = await Task.Run(() => ProfileSwitchService.ExecuteSwitchPlanAsync(
                plan,
                _state.Settings,
                _state.Db,
                _state.Cache,
                _state.Store,
                progress));

            _state.SaveAll();
            _state.NotifyStateChanged();

            if (res.Success)
            {
                _state.Log(Loc.F("Profiles.Switch.LogSuccess", profile.Name, res.MovedToVaultCount, res.ActivatedCount, res.StayedInPlaceCount));
                MessageBox.Show(
                    Window.GetWindow(this),
                    Loc.F("Profiles.Switch.SuccessMsg", profile.Name, res.StayedInPlaceCount, res.MovedToVaultCount, res.ActivatedCount),
                    Loc.T("Profiles.Switch.SuccessTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                string errs = string.Join("; ", res.Errors);
                _state.Log(Loc.F("Profiles.Switch.LogFail", errs));
                MessageBox.Show(
                    Window.GetWindow(this),
                    Loc.F("Profiles.Switch.ErrorMsg", string.Join("\n", res.Errors)),
                    Loc.T("Profiles.Switch.ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Profiles.Switch.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void OnSwitchToSelectedClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is not null)
        {
            SwitchToProfile(profile);
        }
    }

    private void OnAddModsClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        var existingSet = new HashSet<string>(profile.ModIds, StringComparer.OrdinalIgnoreCase);
        List<ModEntry> available = _state.Db.Mods.Where(m => !existingSet.Contains(m.Id)).ToList();

        var dlg = new SelectModsDialog(available) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.SelectedModIds.Count == 0)
        {
            return;
        }

        foreach (string id in dlg.SelectedModIds)
        {
            ModLibraryService.InsertModIntoProfile(profile, id, _state.Db);
        }

        CommitProfileLoadOrderEdit(
            profile,
            restoredSelectionIds: dlg.SelectedModIds,
            logMessage: Loc.F("Profiles.AddMods.Log", dlg.SelectedModIds.Count, profile.Name));
    }

    private void OnRemoveModClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        var selectedRows = ProfileModsGrid.SelectedItems.OfType<ProfileModRow>().ToList();
        if (selectedRows.Count == 0 && ProfileModsGrid.SelectedItem is ProfileModRow singleRow)
        {
            selectedRows.Add(singleRow);
        }

        if (selectedRows.Count == 0)
        {
            return;
        }

        var removeSet = new HashSet<string>(selectedRows.Select(r => r.ModId), StringComparer.OrdinalIgnoreCase);
        profile.ModIds.RemoveAll(id => removeSet.Contains(id));
        profile.UpdatedAtUtc = DateTime.UtcNow;

        string removedLabel = selectedRows.Count == 1 ? selectedRows[0].DisplayName : $"{selectedRows.Count} mods";
        CommitProfileLoadOrderEdit(
            profile,
            restoredSelectionIds: null,
            logMessage: Loc.F("Profiles.RemoveMod.Log", removedLabel, profile.Name));
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelectedMods(-1);
    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelectedMods(1);

    private void MoveSelectedMods(int delta)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        var selectedIds = ProfileModsGrid.SelectedItems
            .OfType<ProfileModRow>()
            .Select(r => r.ModId)
            .ToList();
        if (selectedIds.Count == 0 && ProfileModsGrid.SelectedItem is ProfileModRow singleRow)
        {
            selectedIds.Add(singleRow.ModId);
        }

        if (selectedIds.Count == 0 || !ModLibraryService.MoveProfileModsByDelta(profile, selectedIds, delta))
        {
            return;
        }

        CommitProfileLoadOrderEdit(
            profile,
            restoredSelectionIds: selectedIds,
            logMessage: Loc.F("Profiles.Reorder.Log", selectedIds.Count, profile.Name));
    }

    private void CommitProfileLoadOrderEdit(
        ModProfile profile,
        IReadOnlyList<string>? restoredSelectionIds,
        string logMessage)
    {
        if (!string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath))
        {
            ModLibraryService.LinkCompanionModsToOrderJson(_state.Db, profile.LinkedOrderJsonPath, profile.ModIds);
        }

        ModLibraryService.ReconcileActiveProfileId(_state.Settings, _state.Db, preferredProfileId: profile.Id);

        ModLibraryService.LoadOrderSyncResult? sync = null;
        string? syncError = null;
        try
        {
            sync = ModLibraryService.WriteProfileLoadOrderFiles(profile, _state.Settings, _state.Db);
        }
        catch (Exception ex)
        {
            syncError = ex.Message;
        }

        _state.SaveLibraryAndCache();
        _state.Log(logMessage);
        LogSyncResult(sync, syncError);
        _state.NotifyStateChanged();
        RestoreProfileGridSelection(restoredSelectionIds);
    }

    private void LogSyncResult(ModLibraryService.LoadOrderSyncResult? sync, string? syncError)
    {
        if (!string.IsNullOrWhiteSpace(syncError))
        {
            _state.Log(Loc.F("Profiles.LoadOrderSync.ErrorLog", syncError));
            return;
        }

        if (sync is null)
        {
            return;
        }

        if (sync.JsonWritten && sync.LsxWritten && !string.IsNullOrWhiteSpace(sync.JsonPath))
        {
            _state.Log(Loc.F(
                "Profiles.LoadOrderSync.JsonAndLsxLog",
                Path.GetFileName(sync.JsonPath),
                sync.JsonEntryCount,
                sync.LsxEntryCount));
        }
        else if (sync.JsonWritten && !string.IsNullOrWhiteSpace(sync.JsonPath))
        {
            _state.Log(Loc.F(
                "Profiles.LoadOrderSync.JsonOnlyLog",
                Path.GetFileName(sync.JsonPath),
                sync.JsonEntryCount));
        }
        else if (sync.LsxWritten)
        {
            _state.Log(Loc.F("Profiles.LoadOrderSync.LsxOnlyLog", sync.LsxEntryCount));
        }
    }

    private void RestoreProfileGridSelection(IReadOnlyList<string>? modIds)
    {
        if (modIds is null || modIds.Count == 0 || ProfileModsGrid.ItemsSource is not List<ProfileModRow> rows)
        {
            return;
        }

        var idSet = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        ProfileModsGrid.SelectedItems.Clear();
        ProfileModRow? firstMatch = null;
        foreach (ProfileModRow row in rows)
        {
            if (idSet.Contains(row.ModId))
            {
                ProfileModsGrid.SelectedItems.Add(row);
                firstMatch ??= row;
            }
        }

        if (firstMatch is not null)
        {
            ProfileModsGrid.ScrollIntoView(firstMatch);
        }
    }

    private void OnProfileGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveSelectedMods(-1);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveSelectedMods(1);
        }
    }

    private void OnProfileGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragSourceRow = null;
        _deferSingleSelectOnMouseUp = false;
        _isDraggingRows = false;

        if (e.ClickCount >= 2)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindVisualParent<ButtonBase>(source) is not null ||
            FindVisualParent<ScrollBar>(source) is not null ||
            FindVisualParent<DataGridColumnHeader>(source) is not null)
        {
            return;
        }

        DataGridRow? rowContainer = FindVisualParent<DataGridRow>(source);
        if (rowContainer?.Item is not ProfileModRow clickedRow)
        {
            return;
        }

        _dragStartPoint = e.GetPosition(ProfileModsGrid);
        _dragSourceRow = clickedRow;

        bool noModifiers = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0;
        if (noModifiers &&
            ProfileModsGrid.SelectedItems.Count > 1 &&
            ProfileModsGrid.SelectedItems.Contains(clickedRow))
        {
            _deferSingleSelectOnMouseUp = true;
            e.Handled = true;
        }
    }

    private void OnProfileGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSingleSelectOnMouseUp && !_isDraggingRows && _dragSourceRow is not null)
        {
            ProfileModsGrid.SelectedItems.Clear();
            ProfileModsGrid.SelectedItem = _dragSourceRow;
        }

        _dragSourceRow = null;
        _deferSingleSelectOnMouseUp = false;
        _isDraggingRows = false;
    }

    private void OnProfileGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSourceRow is null || _isDraggingRows)
        {
            return;
        }

        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        Vector diff = e.GetPosition(ProfileModsGrid) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var selectedSet = ProfileModsGrid.SelectedItems
            .OfType<ProfileModRow>()
            .Select(r => r.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedSet.Add(_dragSourceRow.ModId);

        List<string> draggedIds = profile.ModIds
            .Where(id => selectedSet.Contains(id))
            .ToList();
        if (draggedIds.Count == 0)
        {
            return;
        }

        _isDraggingRows = true;
        try
        {
            var data = new DataObject(InternalModReorderDragFormat, draggedIds);
            DragDrop.DoDragDrop(ProfileModsGrid, data, DragDropEffects.Move);
        }
        finally
        {
            _isDraggingRows = false;
            _dragSourceRow = null;
            _deferSingleSelectOnMouseUp = false;
            HideDropIndicator();
        }
    }

    private void OnProfileGridDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(InternalModReorderDragFormat))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        ScrollViewer? sv = FindVisualChild<ScrollViewer>(ProfileModsGrid);
        DataGridColumnHeadersPresenter? header = FindVisualChild<DataGridColumnHeadersPresenter>(ProfileModsGrid);
        double headerHeight = header?.ActualHeight ?? 28;
        Point posInGrid = e.GetPosition(ProfileModsGrid);

        if (sv is not null && (DateTime.UtcNow - _lastAutoScrollUtc).TotalMilliseconds >= 55)
        {
            if (posInGrid.Y >= headerHeight && posInGrid.Y < headerHeight + 34 && sv.VerticalOffset > 0)
            {
                sv.LineUp();
                _lastAutoScrollUtc = DateTime.UtcNow;
            }
            else if (posInGrid.Y > ProfileModsGrid.ActualHeight - 34 && posInGrid.Y <= ProfileModsGrid.ActualHeight)
            {
                sv.LineDown();
                _lastAutoScrollUtc = DateTime.UtcNow;
            }
        }

        if (TryResolveDropTarget(e, out _, out _, out double indicatorY))
        {
            DropIndicatorLine.Margin = new Thickness(0, Math.Max(0, indicatorY - 1.25), 0, 0);
            DropIndicatorLine.Visibility = Visibility.Visible;
        }
        else
        {
            HideDropIndicator();
        }
    }

    private void OnProfileGridDragLeave(object sender, DragEventArgs e)
    {
        Point pos = e.GetPosition(ProfileModsGrid);
        if (pos.X < 0 || pos.Y < 0 || pos.X > ProfileModsGrid.ActualWidth || pos.Y > ProfileModsGrid.ActualHeight)
        {
            HideDropIndicator();
        }
    }

    private void OnProfileGridDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(InternalModReorderDragFormat))
        {
            return;
        }

        HideDropIndicator();
        e.Handled = true;

        if (e.Data.GetData(InternalModReorderDragFormat) is not List<string> draggedIds || draggedIds.Count == 0)
        {
            return;
        }

        ModProfile? profile = GetSelectedProfile();
        if (profile is null)
        {
            return;
        }

        if (!TryResolveDropTarget(e, out ProfileModRow? targetRow, out bool insertAfter, out _) || targetRow is null)
        {
            return;
        }

        if (!ModLibraryService.ReorderProfileMods(profile, draggedIds, targetRow.ModId, insertAfter))
        {
            return;
        }

        CommitProfileLoadOrderEdit(
            profile,
            restoredSelectionIds: draggedIds,
            logMessage: Loc.F("Profiles.Reorder.Log", draggedIds.Count, profile.Name));
    }

    private bool TryResolveDropTarget(
        DragEventArgs e,
        out ProfileModRow? targetRow,
        out bool insertAfter,
        out double indicatorY)
    {
        targetRow = null;
        insertAfter = false;
        indicatorY = 0;

        if (ProfileModsGrid.ItemsSource is not List<ProfileModRow> rows || rows.Count == 0)
        {
            return false;
        }

        DataGridColumnHeadersPresenter? header = FindVisualChild<DataGridColumnHeadersPresenter>(ProfileModsGrid);
        double headerBottom = header is not null
            ? header.TranslatePoint(new Point(0, header.ActualHeight), ProfileModsGridContainer).Y
            : 28;
        double maxBottom = Math.Max(headerBottom, ProfileModsGridContainer.ActualHeight - 3);

        Point posInGrid = e.GetPosition(ProfileModsGrid);
        HitTestResult? hit = VisualTreeHelper.HitTest(ProfileModsGrid, posInGrid);
        DataGridRow? hitRow = FindVisualParent<DataGridRow>(hit?.VisualHit);

        if (hitRow?.Item is ProfileModRow row)
        {
            Point rowTopLeft = hitRow.TranslatePoint(new Point(0, 0), ProfileModsGridContainer);
            insertAfter = e.GetPosition(hitRow).Y >= hitRow.ActualHeight / 2.0;
            indicatorY = Math.Clamp(insertAfter ? rowTopLeft.Y + hitRow.ActualHeight : rowTopLeft.Y, headerBottom, maxBottom);
            targetRow = row;
            return true;
        }

        Point posInContainer = e.GetPosition(ProfileModsGridContainer);
        if (posInContainer.Y <= headerBottom)
        {
            targetRow = rows[0];
            insertAfter = false;
            indicatorY = headerBottom;
            return true;
        }

        targetRow = rows[^1];
        insertAfter = true;
        if (ProfileModsGrid.ItemContainerGenerator.ContainerFromItem(targetRow) is DataGridRow lastRowContainer)
        {
            double lastBottom = lastRowContainer.TranslatePoint(new Point(0, lastRowContainer.ActualHeight), ProfileModsGridContainer).Y;
            indicatorY = Math.Clamp(lastBottom, headerBottom, maxBottom);
        }
        else
        {
            indicatorY = maxBottom;
        }

        return true;
    }

    private void HideDropIndicator()
    {
        DropIndicatorLine.Visibility = Visibility.Collapsed;
    }

    private static T? FindVisualParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnInspectModClick(object sender, RoutedEventArgs e)
    {
        if (ProfileModsGrid.SelectedItem is not ProfileModRow row || row.Mod is null)
        {
            return;
        }

        var dlg = new PakInspectorDialog(row.Mod, _state.Settings) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void OnRowVaultActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ProfileModRow row || row.Mod is null)
        {
            return;
        }

        if (!_state.EnsureGameClosedBeforeFileOps(Window.GetWindow(this)))
        {
            return;
        }

        try
        {
            ModEntry mod = row.Mod;
            if (mod.Location == ModLocation.Vault)
            {
                if (!string.IsNullOrWhiteSpace(mod.PakFileName) &&
                    File.Exists(ModLibraryService.GetActivePakPath(_state.Settings, mod)))
                {
                    MessageBox.Show(
                        Window.GetWindow(this),
                        Loc.F("Profiles.VaultMove.CollisionMsg", mod.DisplayName, mod.PakFileName),
                        Loc.T("Profiles.VaultMove.CollisionTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                ModLibraryService.ActivateModFromVault(mod, _state.Settings);
                _state.Log(Loc.F("Profiles.VaultMove.ActivatedLog", mod.DisplayName));
            }
            else if (mod.Location is ModLocation.Active or ModLocation.Partial)
            {
                ModLibraryService.MoveModToVault(mod, _state.Settings);
                _state.Log(Loc.F("Profiles.VaultMove.MovedLog", mod.DisplayName));
            }

            _state.SaveLibraryAndCache();
            _state.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Profiles.VaultMove.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
