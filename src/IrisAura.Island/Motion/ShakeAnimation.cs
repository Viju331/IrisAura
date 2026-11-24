namespace IrisAura.Island.Motion;

/// <summary>
/// Failure shake (BLUEPRINT §8.4): horizontal offset of ±8 DIPs, 3 cycles, decaying to zero over 300 ms.
/// </summary>
public static class ShakeAnimation
{
    public const double Amplitude = 8;
    public const int Cycles = 3;
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

    /// <summary>Horizontal offset in DIPs at <paramref name="elapsed"/> since the shake started.</summary>
    public static double OffsetAt(TimeSpan elapsed)
    {
        var t = elapsed.TotalSeconds;
        var duration = Duration.TotalSeconds;
        if (t <= 0 || t >= duration)
        {
            return 0;
        }

        var decay = 1 - (t / duration);
        return Amplitude * decay * Math.Sin(2 * Math.PI * Cycles * (t / duration));
    }
}
