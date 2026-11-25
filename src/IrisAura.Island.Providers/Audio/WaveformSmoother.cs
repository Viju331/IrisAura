namespace IrisAura.Island.Providers.Audio;

/// <summary>
/// Turns one audio peak level into animated waveform bars (BLUEPRINT §8.6.2): 4–5 bars, each smoothed with
/// a 40 ms attack and 180 ms release; when paused the bars settle into a flat, dim line. Each bar is modulated
/// differently so a single level still looks like a live waveform. Pure: the caller supplies the frame time.
/// </summary>
public sealed class WaveformSmoother
{
    public const double Attack = 0.040;
    public const double Release = 0.180;

    /// <summary>Height of the flat line when paused or silent.</summary>
    public const double Floor = 0.12;

    private static readonly double[] Speeds = [5.1, 7.3, 6.2, 8.9, 5.7];
    private static readonly double[] Offsets = [0.0, 1.7, 3.1, 0.9, 2.4];

    private readonly double[] _levels;
    private double _time;

    public WaveformSmoother(int bars = 5)
    {
        if (bars is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(bars), "1–5 bars");
        }

        _levels = Enumerable.Repeat(Floor, bars).ToArray();
    }

    public IReadOnlyList<double> Levels => _levels;

    /// <summary>True when every bar is at the flat line (nothing left to animate).</summary>
    public bool IsSettled => _levels.All(l => Math.Abs(l - Floor) < 0.002);

    /// <param name="peak">Linear peak 0–1 from the audio meter.</param>
    /// <param name="deltaSeconds">Time since the previous update.</param>
    /// <param name="playing">False settles the bars to the flat line.</param>
    public void Update(double peak, double deltaSeconds, bool playing)
    {
        var dt = Math.Clamp(deltaSeconds, 0, 0.25);
        _time += dt;

        // Perceptual shaping: quiet audio still moves the bars visibly.
        var shaped = playing && double.IsFinite(peak) ? Math.Sqrt(Math.Clamp(peak, 0, 1)) : 0;

        for (var i = 0; i < _levels.Length; i++)
        {
            var modulation = 0.55 + (0.45 * Math.Abs(Math.Sin((_time * Speeds[i]) + Offsets[i])));
            var target = playing ? Math.Clamp(shaped * modulation, Floor, 1) : Floor;
            var tau = target > _levels[i] ? Attack : Release;
            var k = 1 - Math.Exp(-dt / tau);
            _levels[i] += (target - _levels[i]) * k;
            if (Math.Abs(_levels[i] - target) < 0.001)
            {
                _levels[i] = target;
            }
        }
    }
}
