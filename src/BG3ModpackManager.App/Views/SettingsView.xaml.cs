using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using BG3ModpackManager.App.Services;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;
using Microsoft.Win32;

namespace BG3ModpackManager.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppState _state;
    private string _lastGameFolderValue = string.Empty;
    private bool _loading;

    public SettingsView(AppState state)
    {
        InitializeComponent();
        _state = state;
        LoadFromSettings(_state.Settings);
        PopulateLanguages();

        ThemeManager.ThemeChanged += OnExternalThemeChanged;
        LocalizationManager.Instance.LanguageChanged += OnExternalLanguageChanged;
    }

    private void OnExternalThemeChanged(AppTheme theme)
    {
        _loading = true;
        DarkThemeRadio.IsChecked = theme == AppTheme.Dark;
        LightThemeRadio.IsChecked = theme == AppTheme.Light;
        _loading = false;
        UpdateStatusIndicators();
    }

    private void OnExternalLanguageChanged()
    {
        _loading = true;
        LanguageCombo.SelectedValue = LocalizationManager.Instance.CurrentLanguageCode;
        _loading = false;
        UpdateStatusIndicators();
    }

    private void PopulateLanguages()
    {
        _loading = true;
        LanguageCombo.ItemsSource = LocalizationManager.Instance.AvailableLanguages;
        LanguageCombo.SelectedValue = LocalizationManager.Instance.CurrentLanguageCode;
        _loading = false;
    }

    private void LoadFromSettings(AppSettings s)
    {
        _loading = true;
        DarkThemeRadio.IsChecked = s.Theme == AppTheme.Dark;
        LightThemeRadio.IsChecked = s.Theme == AppTheme.Light;

        ModsFolderBox.Text = s.ModsFolder;
        GameFolderBox.Text = s.GameFolder;
        _lastGameFolderValue = s.GameFolder;
        DataFolderBox.Text = s.DataFolder;
        OrdersFolderBox.Text = s.OrdersFolder;
        VaultFolderBox.Text = s.VaultFolder;
        LsxPathBox.Text = s.ModSettingsLsxPath;

        FullProfileModeRadio.IsChecked = s.SwitchMode == SwitchMode.FullProfile;
        OverridesOnlyModeRadio.IsChecked = s.SwitchMode == SwitchMode.OverridesAndDataOnly;
        SyncLsxCheckBox.IsChecked = s.SyncModSettingsLsxOnSwitch;
        _loading = false;

        UpdateStatusIndicators();
    }

    private void OnThemeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        AppTheme selected = LightThemeRadio.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
        if (_state.Settings.Theme == selected && ThemeManager.CurrentTheme == selected)
        {
            return;
        }

        _state.Settings.Theme = selected;
        _state.Store.SaveSettings(_state.Settings);
        ThemeManager.ApplyTheme(selected);
        string themeLabel = selected == AppTheme.Dark ? Loc.T("Settings.Theme.Dark") : Loc.T("Settings.Theme.Light");
        _state.Log(Loc.F("Settings.ThemeLog", themeLabel));
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        if (LanguageCombo.SelectedItem is LanguageInfo lang &&
            !string.Equals(_state.Settings.LanguageCode, lang.Code, StringComparison.OrdinalIgnoreCase))
        {
            _state.Settings.LanguageCode = lang.Code;
            _state.Store.SaveSettings(_state.Settings);
            LocalizationManager.Instance.SetLanguage(lang.Code);
            _state.Log(Loc.F("Settings.LanguageLog", lang.DisplayName));
        }
    }

    private void OnReloadLanguagesClick(object sender, RoutedEventArgs e)
    {
        LocalizationManager.Instance.ReloadLanguages();
        PopulateLanguages();
        foreach (string warn in LocalizationManager.Instance.LoadWarnings)
        {
            _state.Log(warn);
        }

        _state.Log(Loc.F("Settings.LanguagesReloadedLog", LocalizationManager.Instance.AvailableLanguages.Count));
    }

    private void OnExportLanguageTemplateClick(object sender, RoutedEventArgs e)
    {
        string path = LocalizationManager.Instance.ExportEnglishTemplate();
        MessageBox.Show(
            Window.GetWindow(this),
            Loc.F("Settings.TemplateExportMsg", path),
            Loc.T("Settings.TemplateExportTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnOpenLanguagesFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LocalizationManager.Instance.LanguagesFolderPath);
        OpenFolder(LocalizationManager.Instance.LanguagesFolderPath);
    }

    private void OnPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateStatusIndicators();
        }
    }

    private void OnGameFolderTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string newGame = GameFolderBox.Text.Trim();
        string oldDerivedData = string.IsNullOrWhiteSpace(_lastGameFolderValue)
            ? string.Empty
            : Path.Combine(_lastGameFolderValue.Trim(), "Data");

        if (!string.IsNullOrWhiteSpace(newGame) &&
            (string.IsNullOrWhiteSpace(DataFolderBox.Text) ||
             string.Equals(DataFolderBox.Text.Trim(), oldDerivedData, StringComparison.OrdinalIgnoreCase)))
        {
            DataFolderBox.Text = Path.Combine(newGame, "Data");
        }

        _lastGameFolderValue = newGame;
        UpdateStatusIndicators();
    }

    private void UpdateStatusIndicators()
    {
        SetDirIndicator(ModsStatusText, ModsFolderBox.Text);
        SetDirIndicator(GameStatusText, GameFolderBox.Text);
        SetDirIndicator(DataStatusText, DataFolderBox.Text);
        SetDirIndicator(OrdersStatusText, OrdersFolderBox.Text);
        SetDirIndicator(VaultStatusText, VaultFolderBox.Text, createHint: true);
        SetFileIndicator(LsxStatusText, LsxPathBox.Text);

        if (!string.IsNullOrWhiteSpace(ModsFolderBox.Text) && !string.IsNullOrWhiteSpace(VaultFolderBox.Text))
        {
            bool same = FileTransactionHelper.IsSameVolume(ModsFolderBox.Text, VaultFolderBox.Text);
            VaultVolumeHint.Text = same ? Loc.T("Settings.Volume.Same") : Loc.T("Settings.Volume.Different");
            VaultVolumeHint.Foreground = ThemeManager.GetBrush(same ? "BrushGreen" : "BrushAmber");
        }
        else
        {
            VaultVolumeHint.Text = string.Empty;
        }
    }

    private static void SetDirIndicator(TextBlock label, string path, bool createHint = false)
    {
        bool exists = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path.Trim());
        label.Text = exists
            ? Loc.T("Settings.Status.Found")
            : (createHint ? Loc.T("Settings.Status.WillCreate") : Loc.T("Settings.Status.Missing"));
        label.Foreground = ThemeManager.GetBrush(exists ? "BrushGreen" : (createHint ? "BrushBlue" : "BrushAmber"));
    }

    private static void SetFileIndicator(TextBlock label, string path)
    {
        bool exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());
        label.Text = exists ? Loc.T("Settings.Status.Found") : Loc.T("Settings.Status.Missing");
        label.Foreground = ThemeManager.GetBrush(exists ? "BrushGreen" : "BrushAmber");
    }

    private void BrowseFolder(TextBox targetBox, string title)
    {
        var dlg = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(targetBox.Text) ? targetBox.Text : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            targetBox.Text = dlg.FolderName;
        }
    }

    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.F("Settings.DirMissingMsg", path),
                Loc.T("Settings.OpenFolderTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void OnBrowseModsClick(object sender, RoutedEventArgs e) => BrowseFolder(ModsFolderBox, Loc.T("Settings.Browse.Mods"));
    private void OnBrowseGameClick(object sender, RoutedEventArgs e) => BrowseFolder(GameFolderBox, Loc.T("Settings.Browse.Game"));
    private void OnBrowseDataClick(object sender, RoutedEventArgs e) => BrowseFolder(DataFolderBox, Loc.T("Settings.Browse.Data"));
    private void OnBrowseOrdersClick(object sender, RoutedEventArgs e) => BrowseFolder(OrdersFolderBox, Loc.T("Settings.Browse.Orders"));
    private void OnBrowseVaultClick(object sender, RoutedEventArgs e) => BrowseFolder(VaultFolderBox, Loc.T("Settings.Browse.Vault"));

    private void OnOpenModsClick(object sender, RoutedEventArgs e) => OpenFolder(ModsFolderBox.Text.Trim());
    private void OnOpenGameClick(object sender, RoutedEventArgs e) => OpenFolder(GameFolderBox.Text.Trim());
    private void OnOpenDataClick(object sender, RoutedEventArgs e) => OpenFolder(DataFolderBox.Text.Trim());
    private void OnOpenOrdersClick(object sender, RoutedEventArgs e) => OpenFolder(OrdersFolderBox.Text.Trim());
    private void OnOpenVaultClick(object sender, RoutedEventArgs e) => OpenFolder(VaultFolderBox.Text.Trim());

    private void OnBrowseLsxClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Settings.Browse.Lsx"),
            Filter = Loc.T("Settings.Browse.LsxFilter"),
            InitialDirectory = File.Exists(LsxPathBox.Text) ? Path.GetDirectoryName(LsxPathBox.Text) : null
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            LsxPathBox.Text = dlg.FileName;
        }
    }

    private void OnAutoDetectClick(object sender, RoutedEventArgs e)
    {
        AppSettings detected = PathAutoDetector.DetectDefaults(new AppSettings
        {
            SwitchMode = OverridesOnlyModeRadio.IsChecked == true ? SwitchMode.OverridesAndDataOnly : SwitchMode.FullProfile,
            SyncModSettingsLsxOnSwitch = SyncLsxCheckBox.IsChecked == true,
            LastSelectedOrderJsonPath = _state.Settings.LastSelectedOrderJsonPath,
            LastExportFolder = _state.Settings.LastExportFolder,
            Theme = LightThemeRadio.IsChecked == true ? AppTheme.Light : AppTheme.Dark,
            LanguageCode = _state.Settings.LanguageCode
        });
        LoadFromSettings(detected);
        _state.Log(Loc.T("Settings.AutoDetectedLog"));
    }

    private void OnOpenAppDataClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_state.Store.DataDirectory);
        OpenFolder(_state.Store.DataDirectory);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _state.Settings.Theme = LightThemeRadio.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
        if (LanguageCombo.SelectedValue is string langCode && !string.IsNullOrWhiteSpace(langCode))
        {
            _state.Settings.LanguageCode = langCode;
        }

        _state.Settings.ModsFolder = ModsFolderBox.Text.Trim();
        _state.Settings.GameFolder = GameFolderBox.Text.Trim();
        _state.Settings.DataFolder = DataFolderBox.Text.Trim();
        _state.Settings.OrdersFolder = OrdersFolderBox.Text.Trim();
        _state.Settings.VaultFolder = VaultFolderBox.Text.Trim();
        _state.Settings.ModSettingsLsxPath = LsxPathBox.Text.Trim();
        _state.Settings.SwitchMode = OverridesOnlyModeRadio.IsChecked == true
            ? SwitchMode.OverridesAndDataOnly
            : SwitchMode.FullProfile;
        _state.Settings.SyncModSettingsLsxOnSwitch = SyncLsxCheckBox.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(_state.Settings.VaultFolder))
        {
            Directory.CreateDirectory(_state.Settings.VaultFolder);
        }

        _state.Store.SaveSettings(_state.Settings);
        ModLibraryService.ReconcileDiskState(_state.Settings, _state.Db);
        _state.SaveLibraryAndCache();
        _state.Log(Loc.T("Settings.SavedLog"));
        _state.NotifyStateChanged();

        MessageBox.Show(Window.GetWindow(this), Loc.T("Settings.SavedMsg"), Loc.T("Settings.SavedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
