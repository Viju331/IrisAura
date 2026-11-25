using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IrisAura.Island.Views;

/// <summary><see langword="true"/> → Collapsed, <see langword="false"/> → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
