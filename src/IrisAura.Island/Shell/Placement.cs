using System.Windows;

namespace IrisAura.Island.Shell;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>True when this rectangle covers all of <paramref name="other"/>.</summary>
    public bool Covers(PixelRect other) =>
        Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;

    public static PixelRect FromLtrb(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public override string ToString() => $"({Left},{Top} {Width}x{Height})";
}

/// <summary>A display monitor: full bounds, work area (minus taskbar/app bars), DPI scale.</summary>
public readonly record struct MonitorDescriptor(nint Handle, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary);

/// <summary>Which monitor hosts the Island (BLUEPRINT §8.3).</summary>
public enum MonitorMode
{
    Primary,
    FollowCursor,
}

/// <summary>Pure placement maths (BLUEPRINT §8.3).</summary>
public static class PlacementCalculator
{
    /// <summary>
    /// Window rectangle in physical pixels: top-centre of the monitor's work area, so the Island sits
    /// below a top-docked taskbar or app bar.
    /// </summary>
    public static PixelRect Compute(MonitorDescriptor monitor, double windowWidthDip, double windowHeightDip)
    {
        var width = (int)Math.Round(windowWidthDip * monitor.Scale);
        var height = (int)Math.Round(windowHeightDip * monitor.Scale);
        var work = monitor.WorkArea;
        var left = work.Left + ((work.Width - width) / 2);
        return new PixelRect(left, work.Top, width, height);
    }

    /// <summary>Converts a point in screen pixels to window DIPs.</summary>
    public static Point ScreenToWindowDip(PixelRect window, double scale, int screenX, int screenY) =>
        new((screenX - window.Left) / scale, (screenY - window.Top) / scale);

    /// <summary>Converts a rectangle in window DIPs to screen pixels.</summary>
    public static PixelRect WindowDipToScreen(PixelRect window, double scale, Rect dip) =>
        PixelRect.FromLtrb(
            window.Left + (int)Math.Floor(dip.Left * scale),
            window.Top + (int)Math.Floor(dip.Top * scale),
            window.Left + (int)Math.Ceiling(dip.Right * scale),
            window.Top + (int)Math.Ceiling(dip.Bottom * scale));
}
