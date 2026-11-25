using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Devices;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Views;

/// <summary>
/// "Device connected" banner in the Phase 4B design (BLUEPRINT §8.13A): the device's glyph inside a ring that fills to
/// the battery level when Windows reports it (our own icon and animation; no third-party artwork).
/// </summary>
public sealed class DeviceActivityView : ObservableObject, IActivityViewModel
{
    private BluetoothDeviceInfo _device;
    private string? _accessible;

    public DeviceActivityView(IslandActivity activity)
    {
        _device = activity.View as BluetoothDeviceInfo ?? new BluetoothDeviceInfo("Bluetooth device", DeviceCategory.Other, null);
        _accessible = activity.AccessibleText;
    }

    public string Name => _device.Name;

    public string Glyph => BluetoothActivities.GlyphFor(_device.Category);

    public bool HasBattery => _device.BatteryPercent is not null;

    /// <summary>Battery 0–1 for the ring; a full faint ring when unknown.</summary>
    public double BatteryFraction => (_device.BatteryPercent ?? 100) / 100.0;

    public string Status => _device.BatteryPercent is { } b ? $"Connected · {b}%" : "Connected";

    public string AccessibleDescription => _accessible ?? $"{Name} connected";

    public ExpandedKind? ExpandedKind => null;

    public double ExpandedHeight => 0;

    public void SetVisible(bool visible)
    {
    }

    public void Apply(IslandActivity activity)
    {
        if (activity.View is BluetoothDeviceInfo device)
        {
            _device = device;
        }

        _accessible = activity.AccessibleText;
        OnPropertyChanged(string.Empty);
    }
}
