using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Newrest.Pos.Client.Core.ViewModels;

namespace Newrest.Pos.Client.Infrastructure;

public sealed class MadConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? Format.Mad(d) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class OnlineBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x3E)) : new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string { Length: > 0 } hex ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) : Brushes.SteelBlue;
        }
        catch (FormatException)
        {
            return Brushes.SteelBlue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
