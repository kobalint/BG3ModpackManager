using System.Windows;
using BG3ModpackManager.App.Services;

namespace BG3ModpackManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var state = new AppState();
        LocalizationManager.Instance.Initialize(state.Settings.LanguageCode);
        ThemeManager.Initialize(state.Settings.Theme);
        var mainWindow = new MainWindow(state);
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
