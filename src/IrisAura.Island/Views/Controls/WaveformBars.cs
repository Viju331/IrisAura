using System.Windows;
using System.Windows.Media;

namespace IrisAura.Island.Views.Controls;

/// <summary>
/// Rounded level bars (BLUEPRINT §8.6.2). Drawn directly in <see cref="OnRender"/>: no child
/// elements, so a 30 Hz level update costs one cheap redraw.
/// </summary>
public sealed class WaveformBars : FrameworkElement
{
    public static readonly DependencyProperty LevelsProperty = DependencyProperty.Register(
        nameof(Levels), typeof(IReadOnlyList<double>), typeof(WaveformBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(WaveformBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(WaveformBars),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(WaveformBars),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public IReadOnlyList<double>? Levels
    {
        get => (IReadOnlyList<double>?)GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Levels?.Count ?? 5;
        var width = (count * BarWidth) + (Math.Max(0, count - 1) * Gap);
        var height = double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var levels = Levels;
        if (levels is null || levels.Count == 0)
        {
            return;
        }

        var height = ActualHeight;
        var radius = BarWidth / 2;
        for (var i = 0; i < levels.Count; i++)
        {
            var h = Math.Max(BarWidth, Math.Clamp(levels[i], 0, 1) * height);
            var x = i * (BarWidth + Gap);
            var y = (height - h) / 2;
            drawingContext.DrawRoundedRectangle(BarBrush, null, new Rect(x, y, BarWidth, h), radius, radius);
        }
    }
}
