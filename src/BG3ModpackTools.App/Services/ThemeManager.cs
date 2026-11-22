using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.App.Services;

/// <summary>
/// Manages runtime switching between custom Dark and Light color palettes
/// (including Windows 10/11 native title bar dark/light integration).
/// </summary>
public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static bool _hookRegistered;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public static event Action<AppTheme>? ThemeChanged;

    public static void Initialize(AppTheme initialTheme)
    {
        if (!_hookRegistered)
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                Window.LoadedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is Window w)
                    {
                        ApplyWindowTitleBar(w);
                    }
                }));
            _hookRegistered = true;
        }

        ApplyTheme(initialTheme);
    }

    public static void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        Application? app = Application.Current;
        if (app is null)
        {
            ThemeChanged?.Invoke(theme);
            return;
        }

        string uriText = theme == AppTheme.Light
            ? "pack://application:,,,/Themes/ColorsLight.xaml"
            : "pack://application:,,,/Themes/ColorsDark.xaml";

        var newColors = new ResourceDictionary { Source = new Uri(uriText, UriKind.Absolute) };
        var merged = app.Resources.MergedDictionaries;

        ResourceDictionary? existingColors = merged.FirstOrDefault(d =>
            d.Source is not null &&
            d.Source.OriginalString.Contains("Colors", StringComparison.OrdinalIgnoreCase));

        if (existingColors is not null)
        {
            int idx = merged.IndexOf(existingColors);
            merged[idx] = newColors;
        }
        else
        {
            merged.Insert(0, newColors);
        }

        foreach (Window win in app.Windows)
        {
            ApplyWindowTitleBar(win);
        }

        ThemeChanged?.Invoke(theme);
    }

    public static void ApplyWindowTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int useDark = CurrentTheme == AppTheme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // Unsupported on older Windows builds; ignore gracefully.
        }
    }

    public static Brush GetBrush(string resourceKey)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;
    }
}
