using System.Windows;
using System.Windows.Media;

namespace IrisAura.Island.Views.Controls;

/// <summary>
/// Cheap soft shadow for the pill (BLUEPRINT §8.3 "or a pre-rendered … for performance").
/// Draws a few concentric rounded rectangles with falling opacity instead of a
/// <see cref="System.Windows.Media.Effects.DropShadowEffect"/>. Measured in Phase 1: the blur effect
/// made every waveform update and animation frame re-rasterise the shadow, costing ~4× the CPU in a
/// layered window (docs/PROGRESS.md, Phase 1).
/// </summary>
public sealed class PillShadow : FrameworkElement
{
    /// <summary>How far the outermost ring extends beyond the shape, in DIPs.</summary>
    public const double Spread = 8;

    /// <summary>Downward offset of the shadow, in DIPs.</summary>
    public const double OffsetY = 2;

    private const int Rings = 4;
    private const double InnerAlpha = 0.20;

    private static readonly Brush[] RingBrushes = CreateRingBrushes();

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(PillShadow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public PillShadow()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Corner radius of the shape the shadow belongs to.</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // This element is sized to the shape; rings are drawn outside its bounds (no clipping).
        var shape = new Rect(0, OffsetY, ActualWidth, ActualHeight);
        if (shape.Width <= 0 || shape.Height <= 0)
        {
            return;
        }

        // Outermost (faintest) first; each ring is a filled rounded rectangle, so the overlaps
        // accumulate into a soft gradient towards the shape. The opaque pill covers the centre.
        for (var i = Rings; i >= 1; i--)
        {
            var grow = Spread * i / Rings;
            var rect = Rect.Inflate(shape, grow, grow);
            var radius = Math.Min(CornerRadius + grow, Math.Min(rect.Width, rect.Height) / 2);
            drawingContext.DrawRoundedRectangle(RingBrushes[i - 1], null, rect, radius, radius);
        }
    }

    private static Brush[] CreateRingBrushes()
    {
        var brushes = new Brush[Rings];
        for (var i = 0; i < Rings; i++)
        {
            // Alpha per ring; stacked rings give ~InnerAlpha near the edge, fading to ~0.04 outside.
            var alpha = InnerAlpha / Rings * (1 + ((Rings - 1 - i) * 0.35));
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0));
            brush.Freeze();
            brushes[i] = brush;
        }

        return brushes;
    }
}
