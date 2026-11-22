using System.Collections.ObjectModel;
using System.Windows;
using BG3ModpackManager.App.Views.Dialogs;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.App.Services;

/// <summary>
/// Shared runtime state coordinating settings, library persistence, PAK scan cache, and UI refresh across views.
/// </summary>
public sealed class AppState
{
    public JsonStore Store { get; }
    public AppSettings Settings { get; set; }
    public LibraryDatabase Db { get; set; }
    public PakMetadataCache Cache { get; set; }
    public LoadOrderAuditReport? LatestAudit { get; set; }
    public ObservableCollection<string> ActivityLog { get; } = new();

    public event Action? StateChanged;
    public event Action<string>? StatusMessagePublished;
    public event Action<bool, string>? BusyStateChanged;

    public AppState()
    {
        Store = new JsonStore();
        AppSettings loaded = Store.LoadSettings();
        bool isFirstSettings = !File.Exists(Store.SettingsPath);
        Settings = PathAutoDetector.DetectDefaults(loaded);
        if (string.IsNullOrWhiteSpace(Settings.LanguageCode))
        {
            Settings.LanguageCode = "en";
        }

        if (isFirstSettings)
        {
            Store.SaveSettings(Settings);
        }

        Db = Store.LoadLibrary();
        Cache = Store.LoadCache();
    }

    public void SaveAll()
    {
        Store.SaveSettings(Settings);
        Store.SaveLibrary(Db);
        Store.SaveCache(Cache);
    }

    public void SaveLibraryAndCache()
    {
        Store.SaveLibrary(Db);
        Store.SaveCache(Cache);
    }

    public void NotifyStateChanged()
    {
        RunOnUiThread(() => StateChanged?.Invoke());
    }

    public void Log(string message)
    {
        RunOnUiThread(() =>
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            ActivityLog.Insert(0, stamped);
            while (ActivityLog.Count > 200)
            {
                ActivityLog.RemoveAt(ActivityLog.Count - 1);
            }

            StatusMessagePublished?.Invoke(message);
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void SetBusy(bool isBusy, string message = "")
    {
        BusyStateChanged?.Invoke(isBusy, message);
    }

    /// <summary>
    /// Ensures Baldur's Gate 3 (Vulkan or DX11) is closed before performing file mutations,
    /// prompting the user with GameRunningDialog if a game process is detected.
    /// </summary>
    public bool EnsureGameClosedBeforeFileOps(Window? owner)
    {
        if (!GameProcessGuard.IsGameRunning())
        {
            return true;
        }

        var dlg = new GameRunningDialog
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        return dlg.ShowDialog() == true && !GameProcessGuard.IsGameRunning();
    }
}
