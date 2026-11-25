using System.Windows;

namespace IrisAura.Island.Shell;

/// <summary>Visual states of the Island (BLUEPRINT §8.2).</summary>
public enum IslandVisualState
{
    Idle,
    Compact,
    Split,
    Banner,
    Expanded,
}

/// <summary>Which expanded layout an activity uses.</summary>
public enum ExpandedKind
{
    /// <summary>400 × 180, radius 36 (media). Since Phase 4B the hover view is the dashboard panel instead.</summary>
    Media,

    /// <summary>400 × 120–200, radius 32.</summary>
    Generic,
}

/// <summary>Size of the pill and the optional split bubble, in DIPs at 100 % scale.</summary>
public readonly record struct PillGeometry(
    double Width,
    double Height,
    double CornerRadius,
    double BubbleSize = 0,
    double BubbleGap = 0)
{
    /// <summary>Width of pill plus gap plus bubble.</summary>
    public double TotalWidth => Width + (BubbleSize > 0 ? BubbleGap + BubbleSize : 0);

    public bool HasBubble => BubbleSize > 0.5;
}

/// <summary>
/// Fixed Island layout (BLUEPRINT §8.2, §8.3). The window is created once at the largest size and
/// only the pill inside it animates, so the window never resizes per frame.
/// </summary>
public static class IslandLayout
{
    /// <summary>
    /// Largest pill width (400) plus spring overshoot (~8 %) plus the shadow margin on both sides.
    /// Everything outside the pill is transparent and click-through.
    /// </summary>
    public const double WindowWidth = 480;

    /// <summary>Top inset + largest pill height (generic expanded, 200) + overshoot + shadow margin.</summary>
    public const double WindowHeight = 250;

    /// <summary>
    /// Window size while the dashboard panel (Phase 4B, §8.13A) is open or animating: panel width 640 plus overshoot and
    /// shadow margin. The window grows only for the panel, because every redraw of a layered window copies the whole
    /// surface (ADR 0004/0006): the compact pill and its waveform keep the smaller window.
    /// </summary>
    public const double PanelWindowWidth = 740;

    /// <summary>Top inset + panel height (224) + overshoot + shadow margin.</summary>
    public const double PanelWindowHeight = 290;

    /// <summary>Gap between the top of the work area and the pill.</summary>
    public const double TopInset = 6;

    /// <summary>Band above the pill that also counts as "over the pill", so sliding the cursor to the top edge reaches it.</summary>
    public const double HotZone = 6;

    public const double SplitGap = 8;
    public const double BubbleSize = 36;
    public const double GenericExpandedMin = 120;
    public const double GenericExpandedMax = 200;

    public static PillGeometry Idle { get; } = new(120, 32, 16);
    public static PillGeometry Compact { get; } = new(300, 36, 18);
    public static PillGeometry Split { get; } = new(260, 36, 18, BubbleSize, SplitGap);
    public static PillGeometry Banner { get; } = new(380, 72, 28);
    public static PillGeometry ExpandedMedia { get; } = new(400, 180, 36);

    /// <summary>Dashboard panel (§8.13A): tabs, now card, calendar strip, camera mirror / file tray.</summary>
    public static PillGeometry Dashboard { get; } = new(640, 224, 36);

    public static PillGeometry ExpandedGeneric(double height) =>
        new(400, Math.Clamp(height, GenericExpandedMin, GenericExpandedMax), 32);

    public static PillGeometry For(IslandVisualState state, ExpandedKind kind = ExpandedKind.Media, double genericHeight = 140) =>
        state switch
        {
            IslandVisualState.Idle => Idle,
            IslandVisualState.Compact => Compact,
            IslandVisualState.Split => Split,
            IslandVisualState.Banner => Banner,
            IslandVisualState.Expanded => kind == ExpandedKind.Media ? ExpandedMedia : ExpandedGeneric(genericHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };

    /// <summary>Pill rectangle in window DIPs: horizontally centred (with the bubble) at the top inset.</summary>
    public static Rect PillRect(PillGeometry g, double windowWidth = WindowWidth)
    {
        var left = (windowWidth - g.TotalWidth) / 2;
        return new Rect(left, TopInset, Math.Max(0, g.Width), Math.Max(0, g.Height));
    }

    /// <summary>Bubble rectangle in window DIPs, or <see langword="null"/> without a bubble.</summary>
    public static Rect? BubbleRect(PillGeometry g, double windowWidth = WindowWidth)
    {
        if (!g.HasBubble)
        {
            return null;
        }

        var pill = PillRect(g, windowWidth);
        var top = TopInset + ((g.Height - g.BubbleSize) / 2);
        return new Rect(pill.Right + g.BubbleGap, top, g.BubbleSize, g.BubbleSize);
    }

    /// <summary>
    /// Whether a point (window DIPs) is over the interactive Island: the pill (extended upwards by
    /// the hot zone to the window's top edge) or the bubble.
    /// </summary>
    public static bool IsOverInteractiveArea(PillGeometry g, Point p, double windowWidth = WindowWidth)
    {
        var pill = PillRect(g, windowWidth);
        var pillWithHotZone = new Rect(pill.Left, 0, pill.Width, pill.Bottom);
        if (pillWithHotZone.Contains(p))
        {
            return true;
        }

        return BubbleRect(g, windowWidth) is { } bubble && bubble.Contains(p);
    }
}
