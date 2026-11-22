using System.ComponentModel;
using System.Windows;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.App.Services;

/// <summary>
/// Observable WPF localization facade wrapping LocalizationCatalog with live UI-thread refresh.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private readonly LocalizationCatalog _catalog;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    private LocalizationManager()
    {
        _catalog = new LocalizationCatalog();
    }

    public string LanguagesFolderPath => _catalog.ExternalLanguagesDirectory;
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _catalog.AvailableLanguages;
    public string CurrentLanguageCode => _catalog.CurrentLanguageCode;
    public IReadOnlyList<string> LoadWarnings => _catalog.LoadWarnings;

    public string this[string key] => _catalog.Get(key);

    public string Format(string key, params object?[] args) => _catalog.Format(key, args);

    public void Initialize(string? languageCode)
    {
        _catalog.Reload();
        _catalog.SetLanguage(languageCode);
        RaiseChanged();
    }

    public bool SetLanguage(string? languageCode)
    {
        bool ok = _catalog.SetLanguage(languageCode);
        RaiseChanged();
        return ok;
    }

    public void ReloadLanguages()
    {
        string current = _catalog.CurrentLanguageCode;
        _catalog.Reload();
        _catalog.SetLanguage(current);
        RaiseChanged();
    }

    public string ExportEnglishTemplate(string? destinationPath = null)
    {
        return _catalog.ExportEnglishTemplate(destinationPath);
    }

    private void RaiseChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(NotifyAll);
        }
        else
        {
            NotifyAll();
        }
    }

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }
}

/// <summary>
/// Concise static localization helper for C# code-behind.
/// </summary>
public static class Loc
{
    public static string T(string key) => LocalizationManager.Instance[key];
    public static string F(string key, params object?[] args) => LocalizationManager.Instance.Format(key, args);
}
