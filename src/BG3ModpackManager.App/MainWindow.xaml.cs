using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.App.Views;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.App;

public partial class MainWindow : Window
{
    private enum LayoutTier
    {
        Wide,
        Medium,
        Narrow
    }

    private readonly AppState _state;
    private readonly ProfilesView _profilesView;
    private readonly LoadOrderReviewView _reviewView;
    private readonly LibraryView _libraryView;
    private readonly SettingsView _settingsView;
    private readonly DispatcherTimer _gameProcessTimer;
    private readonly CancellationTokenSource _shutdownCts = new();
    private LayoutTier? _lastLayoutTier;
    private bool? _lastShowSubtitle;
    private bool? _lastCompactHeight;

    public MainWindow(AppState state)
    {
        InitializeComponent();
        _state = state;

        Rect workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 24));
        }

        _profilesView = new ProfilesView(state);
        _reviewView = new LoadOrderReviewView(state);
        _libraryView = new LibraryView(state);
        _settingsView = new SettingsView(state);

        _libraryView.ArchiveImportBatchRequested += paths => _ = HandleDroppedPathsAsync(paths);

        ActivityLogList.ItemsSource = _state.ActivityLog;
        _state.StateChanged += RefreshChrome;
        _state.StatusMessagePublished += msg =>
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(() => StatusBarText.Text = msg);
            }
        };
        _state.BusyStateChanged += (isBusy, msg) =>
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    BusyOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
                    BusyMessageText.Text = string.IsNullOrWhiteSpace(msg) ? Loc.T("App.PleaseWait") : msg;
                });
            }
        };

        ThemeManager.ThemeChanged += OnThemeChanged;
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;

        _gameProcessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _gameProcessTimer.Tick += (_, _) => UpdateGameRunningIndicator();

        ViewHost.Content = _profilesView;
        RefreshChrome();
        UpdateGameRunningIndicator();
        UpdateThemeToggleButton();

        foreach (string warn in LocalizationManager.Instance.LoadWarnings)
        {
            _state.Log(warn);
        }

        Loaded += OnWindowLoaded;
        Closed += (_, _) =>
        {
            _gameProcessTimer.Stop();
            ThemeManager.ThemeChanged -= OnThemeChanged;
            LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
        };
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(ActualWidth, ActualHeight);
        _gameProcessTimer.Start();
        await RunInitialScanAsync();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        LayoutTier tier = width >= 1160
            ? LayoutTier.Wide
            : (width >= 900 ? LayoutTier.Medium : LayoutTier.Narrow);
        bool showDropHint = width >= 1380;
        bool showSubtitle = width >= 1060;
        bool compactHeight = height < 640;

        ActiveProfilePillText.MaxWidth = tier == LayoutTier.Narrow
            ? 240
            : Math.Clamp(width - (showDropHint ? 1040 : 760), 140, 320);

        if (_lastLayoutTier == tier &&
            _lastShowSubtitle == showSubtitle &&
            _lastCompactHeight == compactHeight)
        {
            DropHintBorder.Visibility = showDropHint ? Visibility.Visible : Visibility.Collapsed;
            UpdateQuickActionButtons();
            return;
        }

        _lastLayoutTier = tier;
        _lastShowSubtitle = showSubtitle;
        _lastCompactHeight = compactHeight;

        ActivityDrawer.Height = compactHeight ? 115 : 160;
        AppSubtitleText.Visibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed;

        bool isNarrow = tier == LayoutTier.Narrow;
        if (isNarrow)
        {
            Grid.SetRow(HeaderControlsPanel, 1);
            Grid.SetColumn(HeaderControlsPanel, 0);
            Grid.SetColumnSpan(HeaderControlsPanel, 2);
            HeaderControlsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            HeaderControlsPanel.Margin = new Thickness(0, 8, 0, 0);

            DropHintBorder.Visibility = Visibility.Collapsed;
            QuickSwitchLabel.Visibility = Visibility.Visible;
            QuickProfileCombo.Width = 180;

            NavColumn.Width = new GridLength(0);
            Grid.SetRow(NavBorder, 0);
            Grid.SetRowSpan(NavBorder, 1);
            Grid.SetColumn(NavBorder, 0);
            Grid.SetColumnSpan(NavBorder, 2);
            NavBorder.BorderThickness = new Thickness(0, 0, 0, 1);

            NavItemsPanel.Orientation = Orientation.Horizontal;
            NavItemsPanel.Margin = new Thickness(8, 4, 8, 4);
            SetNavButtonStyle("NavButtonHorizontal");

            NavSpacerRow.Height = new GridLength(0);
            QuickActionsHeaderText.Visibility = Visibility.Collapsed;
            QuickActionsBorder.Margin = new Thickness(0);
            QuickActionsBorder.Padding = new Thickness(10, 6, 10, 6);
            QuickActionCol0.Width = GridLength.Auto;
            QuickActionCol1.Width = GridLength.Auto;
            QuickActionCol2.Width = GridLength.Auto;

            Grid.SetRow(StartBg3Button, 0);
            Grid.SetColumn(StartBg3Button, 0);
            StartBg3Button.Margin = new Thickness(0, 0, 6, 0);
            StartBg3Button.Padding = new Thickness(10, 5, 10, 5);

            Grid.SetRow(OpenModsFolderButton, 0);
            Grid.SetColumn(OpenModsFolderButton, 1);
            OpenModsFolderButton.Margin = new Thickness(0, 0, 6, 0);

            Grid.SetRow(OpenGameFolderButton, 0);
            Grid.SetColumn(OpenGameFolderButton, 2);
            OpenGameFolderButton.Margin = new Thickness(0);

            SidebarFooterBorder.Visibility = Visibility.Collapsed;
            StatusBarStatsText.Visibility = Visibility.Visible;

            Grid.SetRow(ViewHost, 1);
            Grid.SetRowSpan(ViewHost, 1);
            Grid.SetColumn(ViewHost, 0);
            Grid.SetColumnSpan(ViewHost, 2);
        }
        else
        {
            Grid.SetRow(HeaderControlsPanel, 0);
            Grid.SetColumn(HeaderControlsPanel, 1);
            Grid.SetColumnSpan(HeaderControlsPanel, 1);
            HeaderControlsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            HeaderControlsPanel.Margin = new Thickness(0);

            bool isWide = tier == LayoutTier.Wide;
            DropHintBorder.Visibility = showDropHint ? Visibility.Visible : Visibility.Collapsed;
            QuickSwitchLabel.Visibility = isWide ? Visibility.Visible : Visibility.Collapsed;
            QuickProfileCombo.Width = isWide ? 190 : 165;

            NavColumn.Width = new GridLength(isWide ? 220 : 180);
            Grid.SetRow(NavBorder, 0);
            Grid.SetRowSpan(NavBorder, 2);
            Grid.SetColumn(NavBorder, 0);
            Grid.SetColumnSpan(NavBorder, 1);
            NavBorder.BorderThickness = new Thickness(0, 0, 1, 0);

            NavItemsPanel.Orientation = Orientation.Vertical;
            NavItemsPanel.Margin = new Thickness(0, 10, 0, 0);
            SetNavButtonStyle("NavButton");

            NavSpacerRow.Height = new GridLength(1, GridUnitType.Star);
            QuickActionsHeaderText.Visibility = compactHeight ? Visibility.Collapsed : Visibility.Visible;
            QuickActionsBorder.Margin = compactHeight ? new Thickness(10, 4, 10, 0) : new Thickness(10, 8, 10, 0);
            QuickActionsBorder.Padding = compactHeight ? new Thickness(0, 6, 0, 0) : new Thickness(0, 10, 0, 0);
            QuickActionCol0.Width = new GridLength(1, GridUnitType.Star);
            QuickActionCol1.Width = new GridLength(0);
            QuickActionCol2.Width = new GridLength(0);

            Grid.SetRow(StartBg3Button, 0);
            Grid.SetColumn(StartBg3Button, 0);
            StartBg3Button.Margin = new Thickness(0, 0, 0, 6);
            StartBg3Button.Padding = compactHeight ? new Thickness(10, 4, 10, 4) : new Thickness(10, 6, 10, 6);

            Grid.SetRow(OpenModsFolderButton, 1);
            Grid.SetColumn(OpenModsFolderButton, 0);
            OpenModsFolderButton.Margin = new Thickness(0, 0, 0, 6);

            Grid.SetRow(OpenGameFolderButton, 2);
            Grid.SetColumn(OpenGameFolderButton, 0);
            OpenGameFolderButton.Margin = new Thickness(0);

            SidebarFooterBorder.Visibility = Visibility.Visible;
            StatusBarStatsText.Visibility = Visibility.Collapsed;

            Grid.SetRow(ViewHost, 0);
            Grid.SetRowSpan(ViewHost, 2);
            Grid.SetColumn(ViewHost, 1);
            Grid.SetColumnSpan(ViewHost, 1);
        }

        UpdateQuickActionButtons();
    }

    private void SetNavButtonStyle(string styleKey)
    {
        if (TryFindResource(styleKey) is Style navStyle)
        {
            NavProfiles.Style = navStyle;
            NavReview.Style = navStyle;
            NavLibrary.Style = navStyle;
            NavSettings.Style = navStyle;
        }
    }

    private void OnThemeChanged(AppTheme _) => UpdateThemeToggleButton();

    private void OnLanguageChanged()
    {
        RefreshChrome();
        UpdateGameRunningIndicator();
        UpdateThemeToggleButton();
    }

    private void UpdateThemeToggleButton()
    {
        ThemeToggleButton.Content = ThemeManager.CurrentTheme == AppTheme.Dark
            ? Loc.T("App.ThemeToLight")
            : Loc.T("App.ThemeToDark");
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        AppTheme next = ThemeManager.CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _state.Settings.Theme = next;
        _state.Store.SaveSettings(_state.Settings);
        ThemeManager.ApplyTheme(next);
        string themeLabel = next == AppTheme.Dark ? Loc.T("Settings.Theme.Dark") : Loc.T("Settings.Theme.Light");
        _state.Log(Loc.F("Settings.ThemeLog", themeLabel));
    }

    private void UpdateGameRunningIndicator()
    {
        IReadOnlyList<RunningGameProcess> running = GameProcessGuard.GetRunningGameProcesses();
        if (running.Count == 0)
        {
            Bg3RunningPill.Visibility = Visibility.Collapsed;
            return;
        }

        string labels = string.Join(" + ", running.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));
        Bg3RunningPillText.Text = Loc.F("App.Bg3RunningFormat", labels);
        Bg3RunningPill.Visibility = Visibility.Visible;
    }

    private async Task RunInitialScanAsync()
    {
        try
        {
            _state.SetBusy(true, Loc.T("App.ScanningMods"));
            var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
            var (adopted, updated) = await Task.Run(() => ModLibraryService.ScanAndAdoptActiveModsAsync(
                _state.Settings,
                _state.Db,
                _state.Cache,
                progress,
                _shutdownCts.Token), _shutdownCts.Token);

            _state.SaveLibraryAndCache();
            _state.Log(Loc.F("App.StartupScanLog", _state.Db.Mods.Count, adopted, updated));
            _state.NotifyStateChanged();

            await _reviewView.LoadOrRefreshInitialAuditAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _state.Log(Loc.F("App.StartupScanWarning", ex.Message));
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void RefreshChrome()
    {
        ModProfile? activeProfile = _state.Db.Profiles.FirstOrDefault(p => p.Id == _state.Db.ActiveProfileId);
        ActiveProfilePillText.Text = Loc.F(
            "App.ActiveProfileFormat",
            activeProfile is not null ? activeProfile.Name : Loc.T("App.ActiveProfileNone"));

        var orderedProfiles = _state.Db.Profiles
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        QuickProfileCombo.ItemsSource = orderedProfiles;
        if (QuickProfileCombo.SelectedItem is not ModProfile cur || !orderedProfiles.Any(p => p.Id == cur.Id))
        {
            QuickProfileCombo.SelectedItem = activeProfile ?? orderedProfiles.FirstOrDefault();
        }

        QuickSwitchButton.IsEnabled = orderedProfiles.Count > 0;
        NavProfilesCount.Text = _state.Db.Profiles.Count.ToString();
        NavLibraryCount.Text = _state.Db.Mods.Count.ToString();
        SidebarStatsText.Text = Loc.F("App.SidebarStatsFormat", _state.Db.Mods.Count, _state.Db.Profiles.Count);
        StatusBarStatsText.Text = SidebarStatsText.Text;

        int unlistedOverrides = _state.LatestAudit?.UnlistedOverrideCount ?? 0;
        NavReviewAlertBadge.Visibility = unlistedOverrides > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavReviewAlertCount.Text = unlistedOverrides.ToString();
        UpdateQuickActionButtons();
    }

    private void UpdateQuickActionButtons()
    {
        LayoutTier tier = _lastLayoutTier ?? LayoutTier.Wide;
        Bg3LaunchRenderer renderer = _state.Settings.LaunchRenderer;
        string rendererLong = Loc.T(renderer == Bg3LaunchRenderer.DirectX11 ? "App.Renderer.DirectX11" : "App.Renderer.Vulkan");
        string rendererShort = Loc.T(renderer == Bg3LaunchRenderer.DirectX11 ? "App.Renderer.DirectX11Short" : "App.Renderer.VulkanShort");

        if (tier == LayoutTier.Narrow)
        {
            StartBg3TitleText.Text = Loc.F("Nav.StartBg3InlineFormat", rendererShort);
            StartBg3RendererText.Visibility = Visibility.Collapsed;
            OpenModsFolderText.Text = Loc.T("Nav.OpenModsFolder");
            OpenGameFolderText.Text = Loc.T("Nav.OpenGameFolder");
        }
        else if (tier == LayoutTier.Medium)
        {
            StartBg3TitleText.Text = Loc.T("Nav.StartBg3Compact");
            StartBg3RendererText.Text = rendererLong;
            StartBg3RendererText.Visibility = Visibility.Visible;
            OpenModsFolderText.Text = Loc.T("Nav.OpenModsFolderCompact");
            OpenGameFolderText.Text = Loc.T("Nav.OpenGameFolderCompact");
        }
        else
        {
            StartBg3TitleText.Text = Loc.T("Nav.StartBg3Full");
            StartBg3RendererText.Text = rendererLong;
            StartBg3RendererText.Visibility = Visibility.Visible;
            OpenModsFolderText.Text = Loc.T("Nav.OpenModsFolder");
            OpenGameFolderText.Text = Loc.T("Nav.OpenGameFolder");
        }

        Bg3LaunchResolution resolution = GameLauncherService.ResolveExecutable(_state.Settings);
        StartBg3Button.ToolTip = resolution.Success
            ? Loc.F("App.StartBg3.TooltipFormat", rendererLong, resolution.ExecutablePath)
            : (resolution.ErrorCode == "GameFolderNotSet"
                ? Loc.T("App.StartBg3.TooltipNotConfigured")
                : Loc.F("App.StartBg3.TooltipMissingFormat", rendererLong, resolution.ExecutablePath));

        string modsFolder = _state.Settings.ModsFolder.Trim();
        OpenModsFolderButton.ToolTip = string.IsNullOrWhiteSpace(modsFolder)
            ? Loc.T("App.OpenModsFolder.TooltipNotSet")
            : Loc.F("App.OpenModsFolder.TooltipFormat", modsFolder);

        string gameFolder = GameLauncherService.ResolveGameFolder(_state.Settings);
        OpenGameFolderButton.ToolTip = string.IsNullOrWhiteSpace(gameFolder)
            ? Loc.T("App.OpenGameFolder.TooltipNotSet")
            : Loc.F("App.OpenGameFolder.TooltipFormat", gameFolder);
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (NavProfiles.IsChecked == true)
        {
            ViewHost.Content = _profilesView;
            _profilesView.RefreshView();
        }
        else if (NavReview.IsChecked == true)
        {
            ViewHost.Content = _reviewView;
        }
        else if (NavLibrary.IsChecked == true)
        {
            ViewHost.Content = _libraryView;
            _libraryView.RefreshView();
        }
        else if (NavSettings.IsChecked == true)
        {
            ViewHost.Content = _settingsView;
        }
    }

    private void OnQuickSwitchClick(object sender, RoutedEventArgs e)
    {
        if (QuickProfileCombo.SelectedItem is ModProfile target)
        {
            NavProfiles.IsChecked = true;
            _profilesView.SwitchToProfile(target);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RunInitialScanAsync();
    }

    private void OnStartBg3Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<RunningGameProcess> running = GameProcessGuard.GetRunningGameProcesses();
        if (running.Count > 0)
        {
            string labels = string.Join(", ", running.Select(p => p.VariantLabel).Distinct(StringComparer.OrdinalIgnoreCase));
            MessageBox.Show(
                this,
                Loc.F("App.StartBg3.AlreadyRunningMsg", labels),
                Loc.T("App.StartBg3.AlreadyRunningTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Bg3LaunchResolution resolution = GameLauncherService.ResolveExecutable(_state.Settings);
        if (!resolution.Success)
        {
            string msg = resolution.ErrorCode is "GameFolderNotSet" or "GameFolderMissing"
                ? Loc.T("App.StartBg3.MissingFolderMsg")
                : Loc.F("App.StartBg3.MissingExeMsg", resolution.ExecutableFileName, resolution.ExecutablePath);
            MessageBox.Show(
                this,
                msg,
                Loc.T("App.StartBg3.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            GameLauncherService.LaunchGame(resolution);
            string rendererLong = Loc.T(resolution.Renderer == Bg3LaunchRenderer.DirectX11 ? "App.Renderer.DirectX11" : "App.Renderer.Vulkan");
            _state.Log(Loc.F("App.StartBg3.Log", rendererLong, resolution.ExecutablePath));
            UpdateGameRunningIndicator();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.F("App.StartBg3.LaunchErrorMsg", resolution.ExecutablePath, ex.Message),
                Loc.T("App.StartBg3.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnOpenModsFolderClick(object sender, RoutedEventArgs e)
        => OpenConfiguredFolder(_state.Settings.ModsFolder);

    private void OnOpenGameFolderClick(object sender, RoutedEventArgs e)
        => OpenConfiguredFolder(GameLauncherService.ResolveGameFolder(_state.Settings));

    private void OpenConfiguredFolder(string? path)
    {
        string trimmed = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !Directory.Exists(trimmed))
        {
            MessageBox.Show(
                this,
                Loc.F("App.OpenFolder.MissingMsg", string.IsNullOrWhiteSpace(trimmed) ? Loc.T("Common.None") : trimmed),
                Loc.T("Settings.OpenFolderTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", trimmed) { UseShellExecute = true });
    }

    private void OnToggleActivityLogClick(object sender, RoutedEventArgs e)
    {
        ActivityDrawer.Visibility = ActivityDrawer.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e) => UpdateDragVisual(e);
    private void OnWindowDragOver(object sender, DragEventArgs e) => UpdateDragVisual(e);

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateDragVisual(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await HandleDroppedPathsAsync(paths);
        }
    }

    private async Task HandleDroppedPathsAsync(string[] paths)
    {
        var importedArchivePaths = new List<string>();

        foreach (string path in paths)
        {
            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                NavReview.IsChecked = true;
                ViewHost.Content = _reviewView;
                await _reviewView.AuditFileAsync(path);
                continue;
            }

            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".7z", StringComparison.OrdinalIgnoreCase))
            {
                bool isModpack = await Task.Run(() => ProfileImportService.IsExportedModpackArchiveAsync(path));
                if (isModpack)
                {
                    NavProfiles.IsChecked = true;
                    ViewHost.Content = _profilesView;
                    if (await _profilesView.ImportModpackArchiveAsync(path))
                    {
                        importedArchivePaths.Add(path);
                    }

                    continue;
                }
            }

            ArchiveImportPlan? plan = null;
            try
            {
                string fileName = Path.GetFileName(path);
                _state.SetBusy(true, Loc.F("App.InspectingDrop", fileName));
                plan = await Task.Run(() => ArchiveImportService.InspectDropAsync(path, _state.Settings, _state.Db));
                _state.SetBusy(false);

                if (plan.Items.Count == 0)
                {
                    MessageBox.Show(this, Loc.F("App.EmptyPackageMsg", fileName), Loc.T("App.EmptyPackageTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    continue;
                }

                var dlg = new Views.Dialogs.ArchiveImportDialog(plan, _state.Db) { Owner = this };
                if (dlg.ShowDialog() != true)
                {
                    continue;
                }

                string? selectedProfileId = dlg.SelectedProfileId;
                bool installActiveNow = dlg.InstallActiveNow;

                if (installActiveNow && !_state.EnsureGameClosedBeforeFileOps(this))
                {
                    _state.Log(Loc.F("App.ImportCancelledBg3Running", plan.SourceName));
                    continue;
                }

                _state.SetBusy(true, Loc.F("App.ImportingDrop", plan.SourceName));
                var progress = new Progress<string>(msg => _state.SetBusy(true, msg));
                ArchiveImportResult result = await Task.Run(() => ArchiveImportService.CommitImportAsync(
                    plan,
                    selectedProfileId,
                    installActiveNow,
                    _state.Settings,
                    _state.Db,
                    progress));

                _state.SaveLibraryAndCache();
                _state.Log(Loc.F("App.ImportSummaryLog", plan.SourceName, result.ImportedModIds.Count, result.ReusedDuplicateModIds.Count));
                foreach (string renameInfo in result.RenamedOriginalFiles)
                {
                    _state.Log(Loc.F("App.ImportBackupLog", renameInfo));
                }

                _state.NotifyStateChanged();

                if (result.Errors.Count > 0)
                {
                    MessageBox.Show(this, string.Join("\n", result.Errors), Loc.T("App.ImportErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    if ((result.ImportedModIds.Count > 0 || result.ReusedDuplicateModIds.Count > 0) &&
                        ArchiveImportService.IsDeletableArchiveFile(path))
                    {
                        importedArchivePaths.Add(path);
                    }

                    if (result.Warnings.Count > 0)
                    {
                        MessageBox.Show(this, string.Join("\n", result.Warnings), Loc.T("App.ImportWarningsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    Loc.F("App.DropImportErrorMsg", Path.GetFileName(path), ex.Message),
                    Loc.T("App.DropImportErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _state.SetBusy(false);
                if (plan is not null)
                {
                    ArchiveImportService.CleanupStaging(plan);
                }
            }
        }

        if (importedArchivePaths.Count > 0)
        {
            PromptDeleteImportedArchives(importedArchivePaths);
        }
    }

    private void PromptDeleteImportedArchives(List<string> archivePaths)
    {
        List<string> unique = archivePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        if (unique.Count == 0)
        {
            return;
        }

        const int maxShown = 15;
        string listPreview = string.Join("\n", unique.Take(maxShown).Select(p => $"• {Path.GetFileName(p)}"));
        if (unique.Count > maxShown)
        {
            listPreview += Loc.F("App.MoreArchivesFormat", unique.Count - maxShown);
        }

        string msg = unique.Count == 1
            ? Loc.F("App.DeleteArchiveSingleMsg", listPreview)
            : Loc.F("App.DeleteArchiveMultiMsg", unique.Count, listPreview);

        MessageBoxResult answer = MessageBox.Show(
            this,
            msg,
            Loc.T("App.DeleteArchiveTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        int deleted = 0;
        var failed = new List<string>();
        foreach (string p in unique)
        {
            try
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(p)}: {ex.Message}");
            }
        }

        _state.Log(Loc.F("App.DeleteArchiveLog", deleted));
        if (failed.Count > 0)
        {
            MessageBox.Show(
                this,
                Loc.F("App.DeleteArchiveWarnMsg", deleted, string.Join("\n", failed)),
                Loc.T("App.DeleteArchiveWarnTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
