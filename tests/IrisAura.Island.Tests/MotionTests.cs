using FluentAssertions;
using IrisAura.Island.Motion;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Tests;

/// <summary>Springs, morph ordering, content cross-fade and shake (BLUEPRINT §8.4).</summary>
public sealed class MotionTests
{
    private const double Frame = 1.0 / 60;

    [Fact]
    public void Blueprint_spring_presets_match_section_8_4()
    {
        SpringParameters.Collapse.Should().Be(new SpringParameters(300, 30));
        SpringParameters.Expand.Should().Be(new SpringParameters(300, 22));
        SpringParameters.Expand.DampingRatio.Should().BeLessThan(SpringParameters.Collapse.DampingRatio);
    }

    [Theory]
    [InlineData(300, 22)]
    [InlineData(300, 30)]
    [InlineData(300, 34.64)]
    public void Spring_settles_exactly_on_target(double k, double c)
    {
        var spring = new Spring(120, new SpringParameters(k, c)) { Target = 400 };

        var frames = Run(spring, maxSeconds: 3);

        spring.Value.Should().Be(400);
        spring.IsAtRest.Should().BeTrue();
        (frames * Frame).Should().BeLessThan(1.5, "a UI spring must settle well within 1.5 s");
    }

    [Fact]
    public void Expand_spring_overshoots_slightly_and_collapse_spring_barely()
    {
        var expand = new Spring(0, SpringParameters.Expand) { Target = 100 };
        var collapse = new Spring(0, SpringParameters.Collapse) { Target = 100 };

        var expandPeak = Peak(expand);
        var collapsePeak = Peak(collapse);

        expandPeak.Should().BeInRange(102, 115, "expand is slightly under-damped for a small overshoot");
        collapsePeak.Should().BeLessThan(expandPeak);
        collapsePeak.Should().BeLessThan(103, "collapse is close to critically damped");
    }

    [Fact]
    public void Critically_damped_spring_never_overshoots()
    {
        var critical = new Spring(0, new SpringParameters(300, 2 * Math.Sqrt(300))) { Target = 100 };
        Peak(critical).Should().BeLessThanOrEqualTo(100.0001);
    }

    [Fact]
    public void Spring_is_stable_with_long_frame_gaps()
    {
        var spring = new Spring(0, SpringParameters.Expand) { Target = 400 };
        for (var i = 0; i < 40; i++)
        {
            spring.Step(0.5); // debugger pause or sleep; clamped to 0.1 s
        }

        spring.Value.Should().BeApproximately(400, 0.001);
        double.IsFinite(spring.Velocity).Should().BeTrue();
    }

    [Fact]
    public void Morph_reaches_target_geometry()
    {
        var morph = new MorphController(IslandLayout.Idle);
        morph.MorphTo(IslandLayout.ExpandedMedia, contentChanged: true);

        Run(morph);

        morph.Current.Should().Be(IslandLayout.ExpandedMedia);
        morph.IsAnimating.Should().BeFalse();
        morph.IncomingOpacity.Should().Be(1);
        morph.OutgoingOpacity.Should().Be(0);
        morph.IncomingScale.Should().Be(1);
        morph.IncomingOffsetY.Should().Be(0);
    }

    [Fact]
    public void Width_leads_height_by_40_ms()
    {
        var morph = new MorphController(IslandLayout.Compact);
        morph.MorphTo(IslandLayout.ExpandedMedia, contentChanged: false);

        // 30 ms in: width moving, height not yet.
        morph.Tick(0.010);
        morph.Tick(0.010);
        morph.Tick(0.010);
        morph.Current.Width.Should().BeGreaterThan(IslandLayout.Compact.Width);
        morph.Current.Height.Should().Be(IslandLayout.Compact.Height);

        // After 40 ms the height starts.
        morph.Tick(0.015);
        morph.Tick(0.010);
        morph.Current.Height.Should().BeGreaterThan(IslandLayout.Compact.Height);
    }

    [Fact]
    public void Outgoing_content_fades_within_80_ms_and_incoming_waits_for_60_percent_progress()
    {
        var morph = new MorphController(IslandLayout.Compact);
        morph.MorphTo(IslandLayout.ExpandedMedia, contentChanged: true);

        var elapsed = 0.0;
        var incomingStartedAtProgress = -1.0;
        while (morph.IsAnimating && elapsed < 3)
        {
            morph.Tick(Frame);
            elapsed += Frame;

            if (elapsed >= MorphController.OutgoingFade.TotalSeconds + Frame)
            {
                morph.OutgoingOpacity.Should().Be(0, "outgoing content is gone within 80 ms");
            }

            if (incomingStartedAtProgress < 0 && morph.IncomingOpacity > 0)
            {
                incomingStartedAtProgress = morph.Progress;
            }
        }

        incomingStartedAtProgress.Should().BeGreaterThanOrEqualTo(MorphController.IncomingStartProgress);
    }

    [Fact]
    public void Incoming_content_scales_up_from_0_92()
    {
        var morph = new MorphController(IslandLayout.Compact);
        morph.MorphTo(IslandLayout.ExpandedMedia, contentChanged: true);

        morph.IncomingScale.Should().Be(MorphController.IncomingStartScale);
        morph.IncomingOpacity.Should().Be(0);

        var scales = new List<double>();
        while (morph.IsAnimating)
        {
            morph.Tick(Frame);
            scales.Add(morph.IncomingScale);
        }

        scales.Should().BeInAscendingOrder();
        scales[^1].Should().Be(1);
    }

    [Fact]
    public void Reduced_motion_snaps_size_and_only_cross_fades()
    {
        var morph = new MorphController(IslandLayout.Compact) { ReducedMotion = true };
        morph.MorphTo(IslandLayout.ExpandedMedia, contentChanged: true);

        morph.Current.Should().Be(IslandLayout.ExpandedMedia, "size snaps immediately");
        morph.IncomingScale.Should().Be(1);
        morph.IncomingOffsetY.Should().Be(0);

        var frames = Run(morph);
        (frames * Frame).Should().BeApproximately(MorphController.ReducedMotionFade.TotalSeconds, 2 * Frame);
        morph.IncomingOpacity.Should().Be(1);
    }

    [Fact]
    public void Morph_without_content_change_keeps_content_visible()
    {
        var morph = new MorphController(IslandLayout.Compact);
        morph.MorphTo(IslandLayout.Split, contentChanged: false);

        morph.IncomingOpacity.Should().Be(1);
        Run(morph);
        morph.Current.Should().Be(IslandLayout.Split);
    }

    [Fact]
    public void Shake_is_8_dip_3_cycles_300_ms_and_decays()
    {
        var samples = Enumerable.Range(0, 301)
            .Select(ms => ShakeAnimation.OffsetAt(TimeSpan.FromMilliseconds(ms)))
            .ToArray();

        samples.Max(Math.Abs).Should().BeLessThanOrEqualTo(ShakeAnimation.Amplitude);
        samples.Max(Math.Abs).Should().BeGreaterThan(ShakeAnimation.Amplitude * 0.6);
        ShakeAnimation.OffsetAt(TimeSpan.Zero).Should().Be(0);
        ShakeAnimation.OffsetAt(ShakeAnimation.Duration).Should().Be(0);

        // 3 cycles → 5 interior zero crossings.
        var crossings = 0;
        for (var i = 2; i < samples.Length - 1; i++)
        {
            if (Math.Sign(samples[i]) != Math.Sign(samples[i - 1]) && samples[i] != 0 && samples[i - 1] != 0)
            {
                crossings++;
            }
        }

        crossings.Should().Be(5);

        // Decays: second half peaks below first half peaks.
        samples[150..].Max(Math.Abs).Should().BeLessThan(samples[..150].Max(Math.Abs));
    }

    private static int Run(Spring spring, double maxSeconds = 5)
    {
        var frames = 0;
        while (spring.Step(Frame) && frames < maxSeconds / Frame)
        {
            frames++;
        }

        return frames;
    }

    private static int Run(MorphController morph, double maxSeconds = 5)
    {
        var frames = 0;
        while (morph.Tick(Frame) && frames < maxSeconds / Frame)
        {
            frames++;
        }

        return frames + 1;
    }

    private static double Peak(Spring spring)
    {
        var peak = spring.Value;
        for (var i = 0; i < 600 && spring.Step(Frame); i++)
        {
            peak = Math.Max(peak, spring.Value);
        }

        return peak;
    }
}
