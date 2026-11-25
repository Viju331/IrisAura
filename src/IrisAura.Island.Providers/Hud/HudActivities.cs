using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Hud;

/// <summary>Builds the volume and brightness HUD activities (BLUEPRINT §8.6.3–§8.6.4). Pure, for tests.</summary>
public static class HudActivities
{
    public const string VolumeId = "volume";
    public const string BrightnessId = "brightness";

    /// <summary>Volume HUD: speaker glyph (crossed when muted), level bar and percentage.</summary>
    /// <param name="level">Master volume 0–1.</param>
    /// <param name="muted">Muted.</param>
    public static ActivitySpec Volume(double level, bool muted)
    {
        var percent = Percent(level);
        var text = muted ? "Muted" : $"{percent}%";
        return new ActivitySpec(
            VolumeId,
            ActivityKind.Volume,
            new CompactContent(
                LeadingGlyph: muted ? "Aura.Glyph.VolumeMute" : "Aura.Glyph.Volume",
                LeadingText: "Volume",
                TrailingProgress: muted ? 0 : Math.Clamp(level, 0, 1)),
            new ExpandedContent("Volume", text),
            AccessibleText: muted ? "Volume muted" : $"Volume {percent} percent");
    }

    /// <summary>Brightness HUD: sun glyph, level bar and percentage.</summary>
    /// <param name="percent">Brightness 0–100.</param>
    public static ActivitySpec Brightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return new ActivitySpec(
            BrightnessId,
            ActivityKind.Brightness,
            new CompactContent(LeadingGlyph: "Aura.Glyph.Brightness", LeadingText: "Brightness", TrailingProgress: percent / 100.0),
            new ExpandedContent("Brightness", $"{percent}%"),
            AccessibleText: $"Brightness {percent} percent");
    }

    /// <summary>Volume 0–1 → whole percent, rounded like the Windows flyout.</summary>
    public static int Percent(double level) => (int)Math.Round(Math.Clamp(level, 0, 1) * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether a volume notification is a visible change. Drivers and apps re-send identical values; a device switch
    /// or the first reading is not a user change.
    /// </summary>
    public static bool IsVolumeChange((double Level, bool Muted)? previous, double level, bool muted) =>
        previous is { } p && (p.Muted != muted || Percent(p.Level) != Percent(level));
}
