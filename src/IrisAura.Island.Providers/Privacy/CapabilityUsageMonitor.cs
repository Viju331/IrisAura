using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Interop;
using Microsoft.Win32;
using Serilog;

namespace IrisAura.Island.Providers.Privacy;

/// <summary>Source of microphone/camera usage (a fake in tests).</summary>
public interface ICapabilityUsageSource
{
    /// <summary>Raised on a background thread when the set of apps using either device changes.</summary>
    event EventHandler<CapabilitySnapshot>? Changed;

    CapabilitySnapshot Current { get; }

    /// <summary>Starts watching for one subscriber; watching stops when every subscriber has released.</summary>
    void Acquire();

    void Release();
}

/// <summary>
/// Who is using the microphone and camera (BLUEPRINT §8.6.7), on one background thread shared by the
/// privacy-indicator and call-detection providers:
/// <list type="bullet">
/// <item>Consent store (<c>HKCU\…\CapabilityAccessManager\ConsentStore</c>, microphone and webcam), watched with
///   <c>RegNotifyChangeKeyValue</c>.</item>
/// <item>Microphone also from CoreAudio capture sessions (session-created and state-change notifications; the
///   device list is refreshed every 30 s): Windows 11 26H2 (build 26300) was found not to update the consent store at
///   all (KNOWN_ISSUES), and the session API is documented.</item>
/// </list>
/// Read-only; app names are never logged. Best-effort: exclusive-mode capture and apps that access the camera
/// directly may be missed.
/// </summary>
public sealed class CapabilityUsageMonitor : ICapabilityUsageSource, IDisposable
{
    public const string ConsentStorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private int _subscribers;
    private Thread? _thread;
    private ManualResetEvent? _stop;
    private CapabilitySnapshot _current = CapabilitySnapshot.Empty;

    public event EventHandler<CapabilitySnapshot>? Changed;

    public CapabilitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Acquire()
    {
        lock (_gate)
        {
            if (_subscribers++ > 0)
            {
                return;
            }

            _current = Merge(ReadSnapshot(), null);
            _stop = new ManualResetEvent(false);
            _thread = new Thread(Watch) { IsBackground = true, Name = "IrisAura capability monitor", Priority = ThreadPriority.BelowNormal };
            _thread.Start(_stop);
        }
    }

    public void Release()
    {
        Thread? thread;
        ManualResetEvent? stop;
        lock (_gate)
        {
            if (_subscribers == 0 || --_subscribers > 0)
            {
                return;
            }

            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
            _current = CapabilitySnapshot.Empty;
        }

        stop?.Set();
        thread?.Join(TimeSpan.FromSeconds(2));
        stop?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _subscribers = Math.Min(_subscribers, 1);
        }

        Release();
    }

    /// <summary>Reads who is using the microphone and camera now.</summary>
    public static CapabilitySnapshot ReadSnapshot()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
        if (root is null)
        {
            return CapabilitySnapshot.Empty;
        }

        return new CapabilitySnapshot(ReadUsers(root, "microphone"), ReadUsers(root, "webcam"));
    }

    private static List<CapabilityUser> ReadUsers(RegistryKey root, string capability)
    {
        var users = new List<CapabilityUser>();
        using var key = root.OpenSubKey(capability);
        if (key is null)
        {
            return users;
        }

        Collect(key, nonPackaged: false, users);
        using (var nonPackaged = key.OpenSubKey("NonPackaged"))
        {
            if (nonPackaged is not null)
            {
                Collect(nonPackaged, nonPackaged: true, users);
            }
        }

        users.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return users;
    }

    private static void Collect(RegistryKey parent, bool nonPackaged, List<CapabilityUser> users)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (!nonPackaged && name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var app = parent.OpenSubKey(name);
                if (app is null)
                {
                    continue;
                }

                var start = app.GetValue("LastUsedTimeStart") is long s ? s : 0;
                var stop = app.GetValue("LastUsedTimeStop") is long t ? t : 0;
                if (CapabilityUsage.IsInUse(start, stop))
                {
                    users.Add(CapabilityUsage.UserFromKey(name, nonPackaged));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Key removed while reading.
            }
        }
    }

    private static CapabilitySnapshot Merge(CapabilitySnapshot consentStore, CaptureSessionReader? capture) =>
        capture is null
            ? consentStore
            : consentStore with { Microphone = CapabilityUsage.MergeCapture(consentStore.Microphone, capture.ActiveCaptureProcesses()) };

    private void Publish(CapabilitySnapshot snapshot)
    {
        bool raise;
        lock (_gate)
        {
            raise = !snapshot.SameAs(_current);
            if (raise)
            {
                _current = snapshot;
            }
        }

        if (raise)
        {
            Changed?.Invoke(this, snapshot);
        }
    }

    private void Watch(object? state)
    {
        var stop = (ManualResetEvent)state!;
        using var captureChanged = new AutoResetEvent(false);
        using var capture = new CaptureSessionReader(() => captureChanged.Set());
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
            if (root is null)
            {
                Log.Information("Capability monitor: consent store not found; microphone from audio sessions only");
            }

            using var changed = new AutoResetEvent(false);
            var consentStore = ReadSnapshot();
            var armed = false;
            Publish(Merge(consentStore, capture)); // subscribes to capture sessions and reports current use
            while (true)
            {
                if (root is not null && !armed)
                {
                    var result = ProviderNativeMethods.RegNotifyChangeKeyValue(
                        root.Handle,
                        bWatchSubtree: true,
                        ProviderNativeMethods.REG_NOTIFY_CHANGE_NAME | ProviderNativeMethods.REG_NOTIFY_CHANGE_LAST_SET,
                        changed.SafeWaitHandle,
                        fAsynchronous: true);
                    armed = result == 0;
                    if (!armed)
                    {
                        Log.Warning("Capability monitor: RegNotifyChangeKeyValue failed ({Code})", result);
                    }
                }

                var signal = WaitHandle.WaitAny([stop, changed, captureChanged], DeviceRefreshInterval);
                if (signal == 0)
                {
                    return;
                }

                if (signal == WaitHandle.WaitTimeout)
                {
                    capture.RequestRebuild(); // pick up newly plugged-in microphones
                }
                else if (stop.WaitOne(Debounce))
                {
                    return; // changes arrive in bursts: settle, then read once
                }

                if (signal == 1)
                {
                    armed = false; // a registry notification fires once; re-arm next loop
                    consentStore = ReadSnapshot();
                }

                Publish(Merge(consentStore, capture));
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Capability monitor stopped: {Error}", ex.GetType().Name);
        }
    }
}
