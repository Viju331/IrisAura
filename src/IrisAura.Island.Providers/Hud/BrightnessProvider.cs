using System.Management;
using IrisAura.Island.Activities;
using Serilog;

namespace IrisAura.Island.Providers.Hud;

/// <summary>
/// Brightness HUD (BLUEPRINT §8.6.4): WMI event query <c>WmiMonitorBrightnessEvent</c> in <c>root\WMI</c>
/// (System.Management). Laptop panels only; external monitors controlled over DDC/CI do not raise it, so on a
/// device without a WMI brightness method the provider reports itself unavailable.
/// </summary>
public sealed class BrightnessProvider : IActivityProvider
{
    public const string ProviderId = "brightness";

    private readonly object _gate = new();
    private volatile IActivitySink? _sink;
    private ManagementEventWatcher? _watcher;
    private int? _last;

    public string Id => ProviderId;

    public string DisplayName => "Brightness";

    public void Start(IActivitySink sink)
    {
        _sink = sink;
        _ = Task.Run(() => StartWatcher(sink)); // WMI connection takes 50–200 ms: keep it off the UI thread
    }

    public void Stop()
    {
        _sink = null;
        ManagementEventWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        watcher.EventArrived -= OnEventArrived;
        try
        {
            watcher.Stop();
        }
        catch (ManagementException)
        {
            // WMI service restarted.
        }

        watcher.Dispose();
    }

    public bool OnAction(string activityId, string actionId) => false;

    /// <summary>Current panel brightness, or <see langword="null"/> when the device has no WMI brightness.</summary>
    public static int? ReadCurrent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"], System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        catch (ManagementException)
        {
            // Not supported (desktop / external monitor).
        }

        return null;
    }

    private void StartWatcher(IActivitySink sink)
    {
        try
        {
            var current = ReadCurrent();
            if (current is null)
            {
                sink.ReportUnavailable("no WMI brightness control (desktop or external monitor)");
                return;
            }

            var watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\WMI"),
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnEventArrived;
            lock (_gate)
            {
                if (!ReferenceEquals(_sink, sink))
                {
                    watcher.Dispose(); // stopped while connecting
                    return;
                }

                _last = current;
                _watcher = watcher;
            }

            watcher.Start();
        }
        catch (ManagementException ex)
        {
            Log.Information("Brightness HUD: WMI events unavailable ({Error})", ex.ErrorCode);
            sink.ReportUnavailable("WMI brightness events not supported");
        }
        catch (Exception ex)
        {
            sink.ReportFailure(ex);
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        int percent;
        try
        {
            percent = Convert.ToInt32(e.NewEvent["Brightness"], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return;
        }

        lock (_gate)
        {
            if (_last == percent)
            {
                return; // some firmware re-sends the same level
            }

            _last = percent;
        }

        _sink?.Start(HudActivities.Brightness(percent));
    }
}
