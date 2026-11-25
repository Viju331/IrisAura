using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Devices;

/// <summary>What kind of device connected; chooses the glyph.</summary>
public enum DeviceCategory
{
    Other,
    Audio,
    Mouse,
    Keyboard,
    Phone,
}

/// <summary>View payload of the "connected" banner (Phase 4B device design): the Island draws the device glyph and a battery ring.</summary>
/// <param name="Name">Device name (shown, never logged).</param>
/// <param name="Category">Device kind.</param>
/// <param name="BatteryPercent">Battery level, or <see langword="null"/> when Windows does not report it.</param>
public sealed record BluetoothDeviceInfo(string Name, DeviceCategory Category, int? BatteryPercent);

/// <summary>Bluetooth "connected" activity and device classification (BLUEPRINT §8.6.6). Pure, for tests.</summary>
public static class BluetoothActivities
{
    public const string ActivityId = "bluetooth";

    /// <summary>
    /// Windows' Bluetooth battery level device property, read from a device node in the same container
    /// (shown in Settings for some headsets). ⚠ VERIFY per device: many devices, including some earbuds, do not set it.
    /// </summary>
    public const string BatteryPropertyKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    /// <summary>Classic Bluetooth Class of Device (raw 24-bit value) → category.</summary>
    public static DeviceCategory FromClassOfDevice(uint raw)
    {
        var major = (raw >> 8) & 0x1F;
        var minorPeripheral = (raw >> 6) & 0x3;
        return major switch
        {
            0x04 => DeviceCategory.Audio, // Audio/Video
            0x02 => DeviceCategory.Phone,
            0x05 => minorPeripheral switch
            {
                0x1 or 0x3 => DeviceCategory.Keyboard, // keyboard, combo
                0x2 => DeviceCategory.Mouse, // pointing device
                _ => DeviceCategory.Other,
            },
            _ => DeviceCategory.Other,
        };
    }

    /// <summary>Bluetooth LE GAP Appearance (raw 16-bit value) → category.</summary>
    public static DeviceCategory FromAppearance(ushort raw)
    {
        var category = raw >> 6;
        var sub = raw & 0x3F;
        return category switch
        {
            0x001 => DeviceCategory.Phone,
            0x00F => sub switch
            {
                0x01 => DeviceCategory.Keyboard,
                0x02 => DeviceCategory.Mouse,
                _ => DeviceCategory.Other,
            },
            0x021 or 0x025 => DeviceCategory.Audio, // audio sink; wearable audio device (earbuds, headset, headphones)
            _ => DeviceCategory.Other,
        };
    }

    public static string GlyphFor(DeviceCategory category) => category switch
    {
        DeviceCategory.Audio => "Aura.Glyph.Headphones",
        DeviceCategory.Mouse => "Aura.Glyph.Mouse",
        DeviceCategory.Keyboard => "Aura.Glyph.Keyboard",
        DeviceCategory.Phone => "Aura.Glyph.Phone",
        _ => "Aura.Glyph.Device",
    };

    /// <summary>"&lt;device name&gt; connected", with the battery level when Windows exposes it.</summary>
    /// <param name="name">Device name as set by the user in Windows (shown, never logged).</param>
    /// <param name="category">Device kind.</param>
    /// <param name="batteryPercent">Battery level, or <see langword="null"/> when not reported.</param>
    public static ActivitySpec Connected(string name, DeviceCategory category, int? batteryPercent)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Bluetooth device" : name.Trim();
        var battery = batteryPercent is { } b ? Math.Clamp(b, 0, 100) : (int?)null;
        var subtitle = battery is { } p ? $"Connected · {p}% battery" : "Connected";
        return new ActivitySpec(
            ActivityId,
            ActivityKind.Bluetooth,
            new CompactContent(
                LeadingGlyph: GlyphFor(category),
                LeadingText: name,
                TrailingText: battery is { } t ? $"{t}%" : "Connected",
                Tone: ActivityTone.Success),
            new ExpandedContent(name, subtitle),
            AccessibleText: battery is { } a ? $"{name} connected, battery {a} percent" : $"{name} connected",
            View: new BluetoothDeviceInfo(name, category, battery));
    }
}

/// <summary>
/// Suppresses duplicate "connected" reports: a device with both classic and LE endpoints (common for earbuds)
/// connects on both within seconds. Keyed by the device container id.
/// </summary>
public sealed class ConnectionDeduplicator(TimeSpan window)
{
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns <see langword="true"/> the first time a container connects within the window.</summary>
    public bool ShouldAnnounce(string containerId, DateTimeOffset now)
    {
        foreach (var stale in _recent.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList())
        {
            _recent.Remove(stale);
        }

        if (_recent.ContainsKey(containerId))
        {
            return false;
        }

        _recent[containerId] = now;
        return true;
    }
}
