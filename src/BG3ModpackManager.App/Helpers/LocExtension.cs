using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using BG3ModpackManager.App.Services;

namespace BG3ModpackManager.App.Helpers;

/// <summary>
/// XAML markup extension binding directly to LocalizationManager.Instance[Key] for instant language switching.
/// Usage: Text="{helpers:Loc Profiles.Title}"
/// For DataGridColumn headers (which are DependencyObjects rather than FrameworkElements), use helpers:LocHelper.HeaderKey="...".
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// Attached dependency property allowing DataGridColumn.Header to bind cleanly to LocalizationManager.Instance[Key].
/// </summary>
public static class LocHelper
{
    public static readonly DependencyProperty HeaderKeyProperty = DependencyProperty.RegisterAttached(
        "HeaderKey",
        typeof(string),
        typeof(LocHelper),
        new PropertyMetadata(null, OnHeaderKeyChanged));

    public static string? GetHeaderKey(DependencyObject obj) => (string?)obj.GetValue(HeaderKeyProperty);
    public static void SetHeaderKey(DependencyObject obj, string? value) => obj.SetValue(HeaderKeyProperty, value);

    private static void OnHeaderKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DataGridColumn col && e.NewValue is string key && !string.IsNullOrWhiteSpace(key))
        {
            var binding = new Binding($"[{key}]")
            {
                Source = LocalizationManager.Instance,
                Mode = BindingMode.OneWay
            };
            BindingOperations.SetBinding(col, DataGridColumn.HeaderProperty, binding);
        }
    }
}
