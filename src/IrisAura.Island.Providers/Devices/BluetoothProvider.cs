using IrisAura.Island.Activities;
using Serilog;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace IrisAura.Island.Providers.Devices;

/// <summary>
/// "Device connected" (BLUEPRINT §8.6.6): <c>DeviceWatcher</c>s on the connected classic and LE Bluetooth
/// association endpoints. Devices already connected when the watcher starts are not announced; a device that then
/// connects shows "&lt;name&gt; connected" for 3 s with a glyph for its class (headphones, mouse, keyboard, phone)
/// and, when Windows exposes it, the battery level. Devices with both classic and LE endpoints are announced once.
/// Device names are shown but never logged. No Bluetooth adapter → unavailable.
/// </summary>
public sealed class BluetoothProvider : IActivityProvider
{
    public const string ProviderId = "bluetooth";

    private static readonly string[] RequestedProperties = ["System.Devices.Aep.ContainerId", "System.Devices.Aep.IsConnected"];
    private static readonly TimeSpan BatteryLookupDelay = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private readonly ConnectionDeduplicator _dedupe = new(TimeSpan.FromSeconds(10));
    private readonly List<Watch> _watches = [];
    private IActivitySink? _sink;
    private int _generation;

    public string Id => ProviderId;

    public string DisplayName => "Bluetooth devices";

    public void Start(IActivitySink sink)
    {
        int generation;
        lock (_gate)
        {
            _sink = sink;
            generation = ++_generation;
        }

        _ = StartAsync(sink, generation);
    }

    public void Stop()
    {
        List<Watch> watches;
        lock (_gate)
        {
            _sink = null;
            _generation++;
            watches = [.. _watches];
            _watches.Clear();
        }

        foreach (var watch in watches)
        {
            watch.Dispose();
        }
    }

    public bool OnAction(string activityId, string actionId) => false;

    private async Task StartAsync(IActivitySink sink, int generation)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                sink.ReportUnavailable("no Bluetooth adapter");
                return;
            }

            var classic = new Watch(this, generation, DeviceWatcherFor(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)), isLowEnergy: false);
            var lowEnergy = new Watch(this, generation, DeviceWatcherFor(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)), isLowEnergy: true);
            lock (_gate)
            {
                if (generation != _generation)
                {
                    classic.Dispose();
                    lowEnergy.Dispose();
                    return;
                }

                _watches.Add(classic);
                _watches.Add(lowEnergy);
            }

            classic.Start();
            lowEnergy.Start();
        }
        catch (Exception ex)
        {
            sink.ReportFailure(ex);
        }
    }

    private static DeviceWatcher DeviceWatcherFor(string selector) =>
        DeviceInformation.CreateWatcher(selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint);

    private async Task AnnounceAsync(DeviceInformation info, bool isLowEnergy, int generation)
    {
        IActivitySink? sink;
        var containerId = info.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g ? g : (Guid?)null;
        lock (_gate)
        {
            if (generation != _generation || _sink is null)
            {
                return;
            }

            if (!_dedupe.ShouldAnnounce(containerId?.ToString() ?? info.Id, DateTimeOffset.UtcNow))
            {
                return;
            }

            sink = _sink;
        }

        try
        {
            var category = await CategoryAsync(info.Id, isLowEnergy);
            sink.Start(BluetoothActivities.Connected(info.Name, category, null));

            if (containerId is { } container)
            {
                // Windows publishes the level a moment after the connection; update the banner if it shows up.
                await Task.Delay(BatteryLookupDelay);
                if (await BatteryAsync(container) is { } battery)
                {
                    lock (_gate)
                    {
                        if (generation != _generation)
                        {
                            return;
                        }
                    }

                    sink.Start(BluetoothActivities.Connected(info.Name, category, battery));
                }
            }
        }
        catch (Exception ex)
        {
            // One device's details failing must not stop the provider.
            Log.Information("Bluetooth: device details unavailable ({Error})", ex.GetType().Name);
        }
    }

    private static async Task<DeviceCategory> CategoryAsync(string id, bool isLowEnergy)
    {
        if (isLowEnergy)
        {
            using var le = await BluetoothLEDevice.FromIdAsync(id);
            return le is null ? DeviceCategory.Other : BluetoothActivities.FromAppearance(le.Appearance.RawValue);
        }

        using var device = await BluetoothDevice.FromIdAsync(id);
        return device is null ? DeviceCategory.Other : BluetoothActivities.FromClassOfDevice(device.ClassOfDevice.RawValue);
    }

    /// <summary>Battery level from any device node in the same container, or <see langword="null"/>.</summary>
    public static async Task<int?> BatteryAsync(Guid containerId)
    {
        var nodes = await DeviceInformation.FindAllAsync(
            $"System.Devices.ContainerId:=\"{{{containerId}}}\"",
            [BluetoothActivities.BatteryPropertyKey],
            DeviceInformationKind.Device);
        foreach (var node in nodes)
        {
            if (node.Properties.TryGetValue(BluetoothActivities.BatteryPropertyKey, out var value) && value is byte level)
            {
                return level;
            }
        }

        return null;
    }

    /// <summary>One DeviceWatcher; ignores its initial enumeration (already-connected devices).</summary>
    private sealed class Watch : IDisposable
    {
        private readonly BluetoothProvider _owner;
        private readonly int _generation;
        private readonly DeviceWatcher _watcher;
        private readonly bool _isLowEnergy;
        private volatile bool _enumerated;

        public Watch(BluetoothProvider owner, int generation, DeviceWatcher watcher, bool isLowEnergy)
        {
            _owner = owner;
            _generation = generation;
            _watcher = watcher;
            _isLowEnergy = isLowEnergy;
            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
        }

        public void Start() => _watcher.Start();

        public void Dispose()
        {
            _watcher.Added -= OnAdded;
            _watcher.Updated -= OnUpdated;
            _watcher.Removed -= OnRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            try
            {
                if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    _watcher.Stop();
                }
            }
            catch (Exception)
            {
                // Already stopped/aborted.
            }
        }

        private void OnAdded(DeviceWatcher sender, DeviceInformation info)
        {
            if (_enumerated)
            {
                _ = _owner.AnnounceAsync(info, _isLowEnergy, _generation);
            }
        }

        private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            // Connection changes arrive as Added/Removed because the selector filters on the connection status.
            // Handler required so the watcher raises Added for later connections.
        }

        private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            // Disconnects are not announced (BLUEPRINT §8.6.6 shows connections only). A handler is still attached:
            // DeviceWatcher expects Added, Updated and Removed handlers.
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args) => _enumerated = true;
    }
}
