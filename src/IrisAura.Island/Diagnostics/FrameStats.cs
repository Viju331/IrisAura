namespace IrisAura.Island.Diagnostics;

/// <summary>Summary of frame intervals during one animation (BLUEPRINT §8.8: 60 fps, no dropped frames).</summary>
public readonly record struct FrameSummary(int Frames, double AverageMs, double P95Ms, double MaxMs, int Dropped)
{
    public double AverageFps => AverageMs > 0 ? 1000 / AverageMs : 0;

    public override string ToString() =>
        Frames == 0
            ? "no frames"
            : $"{Frames} frames, avg {AverageMs:0.0} ms ({AverageFps:0} fps), p95 {P95Ms:0.0} ms, max {MaxMs:0.0} ms, dropped {Dropped}";
}

/// <summary>
/// Collects frame-to-frame intervals. A frame counts as dropped when its interval exceeds 1.5× the
/// 60 fps budget (25 ms), i.e. at least one display refresh was missed at 60 Hz.
/// </summary>
public sealed class FrameStats
{
    public const double BudgetMs = 1000.0 / 60.0;
    public const double DroppedThresholdMs = BudgetMs * 1.5;

    private readonly List<double> _intervals = [];

    public int Count => _intervals.Count;

    public void Add(double intervalMs)
    {
        if (intervalMs > 0)
        {
            _intervals.Add(intervalMs);
        }
    }

    public void Reset() => _intervals.Clear();

    public FrameSummary Summarize()
    {
        if (_intervals.Count == 0)
        {
            return default;
        }

        var sorted = _intervals.Order().ToArray();
        var p95Index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
        return new FrameSummary(
            sorted.Length,
            sorted.Average(),
            sorted[Math.Clamp(p95Index, 0, sorted.Length - 1)],
            sorted[^1],
            sorted.Count(i => i > DroppedThresholdMs));
    }
}
