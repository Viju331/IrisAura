using IrisAura.Island.Shell;

namespace IrisAura.Island.Motion;

/// <summary>
/// Drives the pill's size, corner radius, split bubble and content cross-fade (BLUEPRINT §8.4):
/// <list type="bullet">
/// <item>Springs on width, height and radius; expand uses the under-damped spring, collapse the near-critical one.</item>
/// <item>Width leads height by <see cref="HeightLag"/> for the "gooey" morph.</item>
/// <item>Outgoing content fades out within <see cref="OutgoingFade"/>; incoming content fades in once
///   the size animation is <see cref="IncomingStartProgress"/> complete, scaling 0.92 → 1 and sliding up.</item>
/// <item>Reduced motion: size snaps, content cross-fades over <see cref="ReducedMotionFade"/>.</item>
/// </list>
/// Pure and deterministic: the caller passes the frame delta to <see cref="Tick"/>.
/// </summary>
public sealed class MorphController
{
    public static readonly TimeSpan HeightLag = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan OutgoingFade = TimeSpan.FromMilliseconds(80);
    public static readonly TimeSpan IncomingFade = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan ReducedMotionFade = TimeSpan.FromMilliseconds(150);
    public const double IncomingStartProgress = 0.6;
    public const double IncomingStartScale = 0.92;
    public const double IncomingStartOffsetY = 6;

    private readonly Spring _width;
    private readonly Spring _height;
    private readonly Spring _radius;
    private readonly Spring _bubbleSize;
    private readonly Spring _bubbleGap;

    private double _heightLagRemaining;
    private double _pendingHeight;
    private double _initialDistance;
    private double _transitionElapsed = double.PositiveInfinity;
    private double _incomingElapsed = double.PositiveInfinity;
    private bool _contentTransition;

    public MorphController(PillGeometry initial)
    {
        _width = new Spring(initial.Width, SpringParameters.Collapse);
        _height = new Spring(initial.Height, SpringParameters.Collapse);
        _radius = new Spring(initial.CornerRadius, SpringParameters.Collapse);
        _bubbleSize = new Spring(initial.BubbleSize, SpringParameters.Collapse);
        _bubbleGap = new Spring(initial.BubbleGap, SpringParameters.Collapse);
        Target = initial;
        _pendingHeight = initial.Height;
    }

    public PillGeometry Target { get; private set; }

    public PillGeometry Current => new(
        Math.Max(0, _width.Value),
        Math.Max(0, _height.Value),
        Math.Max(0, _radius.Value),
        Math.Max(0, _bubbleSize.Value),
        Math.Max(0, _bubbleGap.Value));

    public bool ReducedMotion { get; set; }

    /// <summary>Opacity of the content being replaced (1 → 0).</summary>
    public double OutgoingOpacity { get; private set; }

    /// <summary>Opacity of the new content (0 → 1).</summary>
    public double IncomingOpacity { get; private set; } = 1;

    /// <summary>Scale of the new content (0.92 → 1).</summary>
    public double IncomingScale { get; private set; } = 1;

    /// <summary>Vertical offset of the new content in DIPs (slides up to 0).</summary>
    public double IncomingOffsetY { get; private set; }

    /// <summary>True while geometry or content is still animating.</summary>
    public bool IsAnimating =>
        !_width.IsAtRest || !_height.IsAtRest || !_radius.IsAtRest || !_bubbleSize.IsAtRest || !_bubbleGap.IsAtRest
        || _heightLagRemaining > 0 || _contentTransition;

    /// <summary>Fraction (0–1) of the size change completed since the last <see cref="MorphTo"/>.</summary>
    public double Progress
    {
        get
        {
            if (_initialDistance <= 0.5)
            {
                return 1;
            }

            var remaining = Math.Abs(Target.Width - _width.Value) + Math.Abs(Target.Height - _height.Value);
            return Math.Clamp(1 - (remaining / _initialDistance), 0, 1);
        }
    }

    /// <summary>Starts a morph to <paramref name="target"/>.</summary>
    /// <param name="target">Final geometry.</param>
    /// <param name="contentChanged">Whether the content shown inside the pill changes (cross-fade).</param>
    public void MorphTo(PillGeometry target, bool contentChanged)
    {
        var current = Current;
        var growing = (target.Width * target.Height) > (current.Width * current.Height);
        var parameters = growing ? SpringParameters.Expand : SpringParameters.Collapse;

        Target = target;
        _initialDistance = Math.Abs(target.Width - current.Width) + Math.Abs(target.Height - current.Height);

        if (ReducedMotion)
        {
            _width.SnapTo(target.Width);
            _height.SnapTo(target.Height);
            _radius.SnapTo(target.CornerRadius);
            _bubbleSize.SnapTo(target.BubbleSize);
            _bubbleGap.SnapTo(target.BubbleGap);
            _heightLagRemaining = 0;
            _pendingHeight = target.Height;
        }
        else
        {
            foreach (var spring in new[] { _width, _height, _radius, _bubbleSize, _bubbleGap })
            {
                spring.Parameters = parameters;
            }

            _width.Target = target.Width;
            _radius.Target = target.CornerRadius;
            _bubbleSize.Target = target.BubbleSize;
            _bubbleGap.Target = target.BubbleGap;

            // Width leads; height follows after the lag.
            _pendingHeight = target.Height;
            _heightLagRemaining = HeightLag.TotalSeconds;
        }

        if (contentChanged)
        {
            _contentTransition = true;
            _transitionElapsed = 0;
            _incomingElapsed = double.PositiveInfinity;
            OutgoingOpacity = 1;
            IncomingOpacity = 0;
            IncomingScale = ReducedMotion ? 1 : IncomingStartScale;
            IncomingOffsetY = ReducedMotion ? 0 : IncomingStartOffsetY;
        }
    }

    /// <summary>Jumps straight to <paramref name="geometry"/> with content fully shown.</summary>
    public void SnapTo(PillGeometry geometry)
    {
        Target = geometry;
        _width.SnapTo(geometry.Width);
        _height.SnapTo(geometry.Height);
        _radius.SnapTo(geometry.CornerRadius);
        _bubbleSize.SnapTo(geometry.BubbleSize);
        _bubbleGap.SnapTo(geometry.BubbleGap);
        _heightLagRemaining = 0;
        _pendingHeight = geometry.Height;
        _initialDistance = 0;
        _contentTransition = false;
        OutgoingOpacity = 0;
        IncomingOpacity = 1;
        IncomingScale = 1;
        IncomingOffsetY = 0;
    }

    /// <summary>Advances all animations by <paramref name="deltaSeconds"/>.</summary>
    /// <returns><see langword="true"/> while anything is still animating.</returns>
    public bool Tick(double deltaSeconds)
    {
        var dt = Math.Clamp(deltaSeconds, 0, Spring.MaxFrameDelta);

        if (_heightLagRemaining > 0)
        {
            _heightLagRemaining -= dt;
            if (_heightLagRemaining <= 0)
            {
                _heightLagRemaining = 0;
                _height.Target = _pendingHeight;
            }
        }

        _width.Step(dt);
        _height.Step(dt);
        _radius.Step(dt);
        _bubbleSize.Step(dt);
        _bubbleGap.Step(dt);

        if (_contentTransition)
        {
            AdvanceContent(dt);
        }

        return IsAnimating;
    }

    private void AdvanceContent(double dt)
    {
        _transitionElapsed += dt;

        if (ReducedMotion)
        {
            var t = Math.Clamp(_transitionElapsed / ReducedMotionFade.TotalSeconds, 0, 1);
            OutgoingOpacity = 1 - t;
            IncomingOpacity = t;
            IncomingScale = 1;
            IncomingOffsetY = 0;
            _contentTransition = t < 1;
            return;
        }

        OutgoingOpacity = Math.Clamp(1 - (_transitionElapsed / OutgoingFade.TotalSeconds), 0, 1);

        // Incoming content waits for the outgoing fade and for most of the size change.
        if (double.IsPositiveInfinity(_incomingElapsed)
            && OutgoingOpacity <= 0
            && Progress >= IncomingStartProgress)
        {
            _incomingElapsed = 0;
        }

        if (!double.IsPositiveInfinity(_incomingElapsed))
        {
            _incomingElapsed += dt;
            var t = Math.Clamp(_incomingElapsed / IncomingFade.TotalSeconds, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
            IncomingOpacity = eased;
            IncomingScale = IncomingStartScale + ((1 - IncomingStartScale) * eased);
            IncomingOffsetY = IncomingStartOffsetY * (1 - eased);
            _contentTransition = t < 1;
        }
    }
}
