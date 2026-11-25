using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace IrisAura.Island.Views.Controls;

/// <summary>
/// Thin playback-position bar (BLUEPRINT §8.6.1 seek). Drawn in <see cref="OnRender"/>; clicking executes
/// <see cref="SeekCommand"/> with the clicked fraction (0–1). The hit area is taller than the drawn track.
/// </summary>
public sealed class SeekBar : FrameworkElement
{
    public const double TrackHeight = 4;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(SeekBar), new PropertyMetadata(null));

    public static readonly DependencyProperty CanSeekProperty = DependencyProperty.Register(
        nameof(CanSeek), typeof(bool), typeof(SeekBar), new PropertyMetadata(false, (d, _) => ((SeekBar)d).UpdateCursor()));

    public SeekBar()
    {
        Height = 14;
        UpdateCursor();
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public ICommand? SeekCommand
    {
        get => (ICommand?)GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    public bool CanSeek
    {
        get => (bool)GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var top = (ActualHeight - TrackHeight) / 2;
        var radius = TrackHeight / 2;

        // Transparent full-height rectangle: makes the whole control hit-testable.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));
        drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(0, top, width, TrackHeight), radius, radius);
        var fill = Math.Clamp(Progress, 0, 1) * width;
        if (fill > 0)
        {
            drawingContext.DrawRoundedRectangle(FillBrush, null, new Rect(0, top, Math.Max(fill, TrackHeight), TrackHeight), radius, radius);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!CanSeek || ActualWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        var fraction = Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);
        if (SeekCommand?.CanExecute(fraction) == true)
        {
            SeekCommand.Execute(fraction);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void UpdateCursor() => Cursor = CanSeek ? Cursors.Hand : null;
}
