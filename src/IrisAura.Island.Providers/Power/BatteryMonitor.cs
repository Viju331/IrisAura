using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Power;

/// <summary>One reading of the power state.</summary>
/// <param name="HasBattery">A battery is present.</param>
/// <param name="PluggedIn">Mains power connected.</param>
/// <param name="Percent">Remaining charge 0–100.</param>
public readonly record struct BatteryReading(bool HasBattery, bool PluggedIn, int Percent);

/// <summary>
/// Decides which power activities to show (BLUEPRINT §8.6.5). Pure; the provider feeds it readings.
/// <list type="bullet">
/// <item>Plugging in shows the charging HUD (bolt, percentage, filling bar).</item>
/// <item>While on battery, a low-battery banner at 20 % and at 10 %, each once per discharge cycle; plugging in
///   starts a new cycle. Dropping past both at once shows only the 10 % banner.</item>
/// <item>The first reading never shows anything (no banner at startup); thresholds already passed count as shown.</item>
/// </list>
/// </summary>
public sealed class BatteryMonitor
{
    public const string ChargingId = "charging";
    public const string LowBatteryId = "battery-low";
    public static readonly int[] Thresholds = [20, 10];
    public static readonly TimeSpan LowBatteryTtl = TimeSpan.FromSeconds(5);

    private readonly HashSet<int> _shown = [];
    private BatteryReading? _previous;

    /// <summary>Returns the activity to show for this reading, if any.</summary>
    public ActivitySpec? Observe(BatteryReading reading)
    {
        var previous = _previous;
        _previous = reading;
        if (!reading.HasBattery)
        {
            return null;
        }

        if (previous is null)
        {
            if (!reading.PluggedIn)
            {
                MarkPassed(reading.Percent);
            }

            return null;
        }

        if (reading.PluggedIn)
        {
            _shown.Clear(); // a new discharge cycle starts when unplugged again
            return previous.Value.PluggedIn ? null : Charging(reading.Percent);
        }

        var crossed = Thresholds.Where(t => reading.Percent <= t && !_shown.Contains(t)).ToList();
        if (crossed.Count == 0)
        {
            return null;
        }

        foreach (var t in crossed)
        {
            _shown.Add(t);
        }

        return LowBattery(reading.Percent);
    }

    public static ActivitySpec Charging(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return new ActivitySpec(
            ChargingId,
            ActivityKind.Charging,
            new CompactContent(LeadingGlyph: "Aura.Glyph.Bolt", LeadingText: "Charging", TrailingProgress: percent / 100.0, Tone: ActivityTone.Success),
            new ExpandedContent("Charging", $"{percent}%"),
            AccessibleText: $"Charging, {percent} percent");
    }

    public static ActivitySpec LowBattery(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return new ActivitySpec(
            LowBatteryId,
            ActivityKind.Charging,
            new CompactContent(LeadingGlyph: "Aura.Glyph.BatteryLow", LeadingText: "Low battery", TrailingText: $"{percent}%", Tone: ActivityTone.Alert),
            new ExpandedContent("Low battery", $"{percent}% remaining"),
            Ttl: LowBatteryTtl,
            AccessibleText: $"Low battery, {percent} percent remaining");
    }

    private void MarkPassed(int percent)
    {
        foreach (var t in Thresholds.Where(t => percent <= t))
        {
            _shown.Add(t);
        }
    }
}
