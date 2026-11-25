using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IrisAura.Island.Views.Panel;

/// <summary>Picks "&lt;Prefix&gt;.&lt;ViewModelType&gt;" (e.g. "Expanded.MediaActivityView") for the panel's now card.</summary>
public sealed class ActivityTemplateSelector : DataTemplateSelector
{
    public string Prefix { get; set; } = "Expanded";

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is null || container is not FrameworkElement element
            ? null
            : element.TryFindResource($"{Prefix}.{item.GetType().Name}") as DataTemplate;
}

/// <summary>
/// Tray item behaviour: a click opens the item; dragging it out (past the system drag distance) starts a file drag
/// so it can be dropped into any app. Attached to the tile in the Tray tab template.
/// </summary>
public static class ShelfDrag
{
    public static readonly DependencyProperty IsSourceProperty = DependencyProperty.RegisterAttached(
        "IsSource", typeof(bool), typeof(ShelfDrag), new PropertyMetadata(false, OnIsSourceChanged));

    private static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(Point?), typeof(ShelfDrag));

    public static bool GetIsSource(DependencyObject element) => (bool)element.GetValue(IsSourceProperty);

    public static void SetIsSource(DependencyObject element, bool value) => element.SetValue(IsSourceProperty, value);

    private static void OnIsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.NewValue is not true)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown += (_, args) =>
        {
            element.SetValue(StartProperty, args.GetPosition(element));
            element.CaptureMouse();
            args.Handled = true;
        };
        element.MouseMove += (_, args) =>
        {
            if (element.GetValue(StartProperty) is not Point start || args.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var delta = args.GetPosition(element) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            element.ClearValue(StartProperty);
            element.ReleaseMouseCapture();
            if ((element as FrameworkElement)?.DataContext is ShelfItemView item)
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move);
            }
        };
        element.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var wasPressed = element.GetValue(StartProperty) is Point;
            element.ClearValue(StartProperty);
            element.ReleaseMouseCapture();
            args.Handled = true; // do not reach the pill's click (pin/unpin)
            if (wasPressed && (element as FrameworkElement)?.DataContext is ShelfItemView item)
            {
                item.Open();
            }
        };
    }
}
