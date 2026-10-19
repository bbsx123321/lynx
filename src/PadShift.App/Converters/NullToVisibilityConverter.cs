using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadShift.App.Converters;

/// <summary>null/空串 → Collapsed，其余 → Visible（配置描述这类可空行用）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? text = value as string;
        return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
