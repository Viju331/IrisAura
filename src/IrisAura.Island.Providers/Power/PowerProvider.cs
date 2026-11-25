using IrisAura.Island.Activities;
using Windows.System.Power;

namespace IrisAura.Island.Providers.Power;

/// <summary>
/// Charging and low battery (BLUEPRINT §8.6.5) from <c>Windows.System.Power.PowerManager</c>
/// (<c>PowerSupplyStatusChanged</c>, <c>RemainingChargePercentChanged</c>, <c>BatteryStatusChanged</c>).
/// Devices without a battery report the provider unavailable.
/// </summary>
public sealed class PowerProvider : IActivityProvider
{
    public const string ProviderId = "power";

    private readonly object _gate = new();
    private BatteryMonitor _monitor = new();
    private IActivitySink? _sink;
    private int _generation;
    private bool _subscribed;

    public string Id => ProviderId;

    public string DisplayName => "Charging and battery";

    public void Start(IActivitySink sink)
    {
        int generation;
        lock (_gate)
        {
            _sink = sink;
            generation = ++_generation;
        }

        // The first PowerManager call activates WinRT power APIs (~0.5 s measured): keep it off the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (PowerManager.BatteryStatus == BatteryStatus.NotPresent)
                {
                    sink.ReportUnavailable("no battery");
                    return;
                }

                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return; // stopped meanwhile
                    }

                    _monitor = new BatteryMonitor();
                    _monitor.Observe(Read());
                    PowerManager.PowerSupplyStatusChanged += OnChanged;
                    PowerManager.RemainingChargePercentChanged += OnChanged;
                    PowerManager.BatteryStatusChanged += OnChanged;
                    _subscribed = true;
                }
            }
            catch (Exception ex)
            {
                sink.ReportFailure(ex);
            }
        });
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _sink = null;
            if (_subscribed)
            {
                PowerManager.PowerSupplyStatusChanged -= OnChanged;
                PowerManager.RemainingChargePercentChanged -= OnChanged;
                PowerManager.BatteryStatusChanged -= OnChanged;
                _subscribed = false;
            }
        }
    }

    public bool OnAction(string activityId, string actionId) => false;

    /// <summary>Current power state.</summary>
    public static BatteryReading Read() => new(
        PowerManager.BatteryStatus != BatteryStatus.NotPresent,
        PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent,
        PowerManager.RemainingChargePercent);

    private void OnChanged(object? sender, object e)
    {
        ActivitySpec? spec;
        IActivitySink? sink;
        lock (_gate)
        {
            if (_sink is null)
            {
                return;
            }

            spec = _monitor.Observe(Read());
            sink = _sink;
        }

        if (spec is not null)
        {
            sink.Start(spec);
        }
    }
}
