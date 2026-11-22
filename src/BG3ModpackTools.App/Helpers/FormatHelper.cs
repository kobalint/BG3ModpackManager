using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BG3ModpackTools.App.Services;
using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.App.Helpers;

public static class FormatHelper
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double val = bytes;
        int unit = 0;
        while (val >= 1024 && unit < units.Length - 1)
        {
            val /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{val:0.##} {units[unit]}";
    }

    public static string FormatModKind(ModKind kind) => kind switch
    {
        ModKind.Pak => Loc.T("Common.Kind.Pak"),
        ModKind.Hybrid => Loc.T("Common.Kind.Hybrid"),
        ModKind.LooseData => Loc.T("Common.Kind.LooseData"),
        ModKind.LooseGame => Loc.T("Common.Kind.LooseGame"),
        _ => kind.ToString()
    };

    public static string FormatModLocation(ModLocation location) => location switch
    {
        ModLocation.Active => Loc.T("Common.Location.Active"),
        ModLocation.Vault => Loc.T("Common.Location.Vault"),
        ModLocation.Partial => Loc.T("Common.Location.Partial"),
        ModLocation.Missing => Loc.T("Common.Location.Missing"),
        _ => location.ToString()
    };

    public static Brush GetLocationBg(ModLocation location) => location switch
    {
        ModLocation.Active => ThemeManager.GetBrush("BrushBadgeGreenBg"),
        ModLocation.Vault => ThemeManager.GetBrush("BrushBadgeBlueBg"),
        ModLocation.Partial => ThemeManager.GetBrush("BrushBadgeAmberBg"),
        _ => ThemeManager.GetBrush("BrushBadgeCrimsonBg")
    };

    public static Brush GetLocationFg(ModLocation location) => location switch
    {
        ModLocation.Active => ThemeManager.GetBrush("BrushGreen"),
        ModLocation.Vault => ThemeManager.GetBrush("BrushBlue"),
        ModLocation.Partial => ThemeManager.GetBrush("BrushAmber"),
        _ => ThemeManager.GetBrush("BrushCrimson")
    };

    public static string FormatOverrideLabel(OverrideKind kind, ManualOverrideMode manualMode = ManualOverrideMode.Auto) =>
        (manualMode, kind) switch
        {
            (ManualOverrideMode.ForceOverride, OverrideKind.StandaloneOverride) => Loc.T("Common.Override.StandaloneManual"),
            (ManualOverrideMode.ForceOverride, _) => Loc.T("Common.Override.MergedManual"),
            (ManualOverrideMode.ForceNonOverride, _) => Loc.T("Common.Override.StandardManual"),
            (_, OverrideKind.StandaloneOverride) => Loc.T("Common.Override.Standalone"),
            (_, OverrideKind.MergedOverride) => Loc.T("Common.Override.Merged"),
            _ => Loc.T("Common.Override.Standard")
        };

    public static Brush GetOverrideBg(OverrideKind kind) => kind switch
    {
        OverrideKind.StandaloneOverride => ThemeManager.GetBrush("BrushBadgeStandaloneBg"),
        OverrideKind.MergedOverride => ThemeManager.GetBrush("BrushBadgeAmberBg"),
        _ => ThemeManager.GetBrush("BrushBadgeGreenBg")
    };

    public static Brush GetOverrideFg(OverrideKind kind) => kind switch
    {
        OverrideKind.StandaloneOverride => ThemeManager.GetBrush("BrushStandaloneOvr"),
        OverrideKind.MergedOverride => ThemeManager.GetBrush("BrushAmber"),
        _ => ThemeManager.GetBrush("BrushGreen")
    };
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
