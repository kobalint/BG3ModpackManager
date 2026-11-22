using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

    private readonly AppState _state;
    private string? _selectedProfileId;
    private bool _suppressSelectionChanged;
    private bool? _lastStackedMode;
    private bool? _lastCompactHeader;
    private double _savedLeftPaneWidth = 280;

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

        bool stacked = width < 780;
        bool compactHeader = (!stacked && width < 960) || width < 640;

        if (_lastStackedMode != stacked)
        {
            _lastStackedMode = stacked;
            if (stacked)
            {
                if (LeftPaneColumn.Width.IsAbsolute && LeftPaneColumn.Width.Value >= 240)
                {
                    _savedLeftPaneWidth = Math.Clamp(LeftPaneColumn.Width.Value, 240, 400);
                }

                ProfilesListBox.MaxHeight = 130;
                ProfileActionsGrid.Columns = 4;
                ProfilesSplitter.Visibility = Visibility.Collapsed;
                LeftPaneColumn.MinWidth = 0;
                LeftPaneColumn.Width = new GridLength(0);
                SplitterColumn.Width = new GridLength(0);

                Grid.SetRow(ProfilesListCard, 0);
                Grid.SetRowSpan(ProfilesListCard, 1);
                Grid.SetColumn(ProfilesListCard, 0);
                Grid.SetColumnSpan(ProfilesListCard, 3);
                ProfilesListCard.Margin = new Thickness(0, 0, 0, 10);

                Grid.SetRow(ProfileDetailsGrid, 2);
                Grid.SetRowSpan(ProfileDetailsGrid, 1);
                Grid.SetColumn(ProfileDetailsGrid, 0);
                Grid.SetColumnSpan(ProfileDetailsGrid, 3);
            }
            else
            {
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
            profile.LinkedOrderJsonPath = dlg.FileName;
            profile.UpdatedAtUtc = DateTime.UtcNow;
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
        _state.Log(Loc.F("Profiles.ExportJson.Log", profile.Name, entries.Count, dlg.FileName));
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

        profile.ModIds.AddRange(dlg.SelectedModIds);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.AddMods.Log", dlg.SelectedModIds.Count, profile.Name));
        _state.NotifyStateChanged();
    }

    private void OnRemoveModClick(object sender, RoutedEventArgs e)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null || ProfileModsGrid.SelectedItem is not ProfileModRow row)
        {
            return;
        }

        profile.ModIds.RemoveAll(id => string.Equals(id, row.ModId, StringComparison.OrdinalIgnoreCase));
        profile.UpdatedAtUtc = DateTime.UtcNow;
        _state.SaveLibraryAndCache();
        _state.Log(Loc.F("Profiles.RemoveMod.Log", row.DisplayName, profile.Name));
        _state.NotifyStateChanged();
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelectedMod(-1);
    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelectedMod(1);

    private void MoveSelectedMod(int delta)
    {
        ModProfile? profile = GetSelectedProfile();
        if (profile is null || ProfileModsGrid.SelectedItem is not ProfileModRow row)
        {
            return;
        }

        int idx = profile.ModIds.FindIndex(id => string.Equals(id, row.ModId, StringComparison.OrdinalIgnoreCase));
        int newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= profile.ModIds.Count)
        {
            return;
        }

        (profile.ModIds[idx], profile.ModIds[newIdx]) = (profile.ModIds[newIdx], profile.ModIds[idx]);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        _state.SaveLibraryAndCache();
        RefreshSelectedProfileDetails();

        if (ProfileModsGrid.ItemsSource is List<ProfileModRow> rows)
        {
            ProfileModsGrid.SelectedItem = rows.FirstOrDefault(r => r.ModId == row.ModId);
        }
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
