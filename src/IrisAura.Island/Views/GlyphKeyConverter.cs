using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace IrisAura.Island.Views;

/// <summary>Turns a glyph resource key ("Aura.Glyph.Play") into its <see cref="Geometry"/>; unknown keys give nothing.</summary>
public sealed class GlyphKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && key.StartsWith("Aura.Glyph.", StringComparison.Ordinal)
            ? Application.Current?.TryFindResource(key) as Geometry
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
