namespace IrisAura.Island.Motion;

/// <summary>Spring constants (BLUEPRINT §8.4).</summary>
/// <param name="Stiffness">Spring constant k.</param>
/// <param name="Damping">Damping coefficient c. Critical damping is 2·√(k·m).</param>
/// <param name="Mass">Mass m.</param>
public readonly record struct SpringParameters(double Stiffness, double Damping, double Mass = 1.0)
{
    /// <summary>Collapse: stiffness 300, damping 30 (close to critical, no visible overshoot).</summary>
    public static SpringParameters Collapse { get; } = new(300, 30);

    /// <summary>Expand: stiffness 300, damping 22 (slightly under-damped: a small overshoot).</summary>
    public static SpringParameters Expand { get; } = new(300, 22);

    /// <summary>Damping ratio ζ; 1 is critically damped, below 1 overshoots.</summary>
    public double DampingRatio => Damping / (2 * Math.Sqrt(Stiffness * Mass));
}

/// <summary>
/// One-dimensional damped spring, integrated with semi-implicit Euler in fixed sub-steps so it
/// stays stable for any frame time. Driven by the real frame delta (BLUEPRINT §8.4).
/// </summary>
public sealed class Spring
{
    /// <summary>Largest integration sub-step (1/240 s).</summary>
    public const double MaxSubStep = 1.0 / 240.0;

    /// <summary>Longest frame delta honoured; longer gaps (debugger, sleep) are clamped.</summary>
    public const double MaxFrameDelta = 0.1;

    /// <summary>Distance from target, in DIPs, considered "at rest".</summary>
    public const double RestDistance = 0.05;

    /// <summary>Speed, in DIPs per second, considered "at rest".</summary>
    public const double RestVelocity = 0.5;

    public Spring(double initialValue, SpringParameters parameters)
    {
        Value = initialValue;
        Target = initialValue;
        Parameters = parameters;
    }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public double Target { get; set; }

    public SpringParameters Parameters { get; set; }

    public bool IsAtRest => Value == Target && Velocity == 0;

    /// <summary>Jumps to <paramref name="value"/> with no motion.</summary>
    public void SnapTo(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    /// <summary>Advances the spring by <paramref name="deltaSeconds"/>.</summary>
    /// <returns><see langword="true"/> while the spring is still moving.</returns>
    public bool Step(double deltaSeconds)
    {
        if (IsAtRest)
        {
            return false;
        }

        var remaining = Math.Clamp(deltaSeconds, 0, MaxFrameDelta);
        var (k, c, m) = (Parameters.Stiffness, Parameters.Damping, Parameters.Mass);

        while (remaining > 0)
        {
            var h = Math.Min(MaxSubStep, remaining);
            var acceleration = ((-k * (Value - Target)) - (c * Velocity)) / m;
            Velocity += acceleration * h;
            Value += Velocity * h;
            remaining -= h;
        }

        if (Math.Abs(Value - Target) < RestDistance && Math.Abs(Velocity) < RestVelocity)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }

        return true;
    }
}
