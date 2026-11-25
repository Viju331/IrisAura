using IrisAura.Island.Activities;
using NAudio.CoreAudioApi;
using Serilog;

namespace IrisAura.Island.Providers.Hud;

/// <summary>
/// Volume HUD (BLUEPRINT §8.6.3): listens to the default output device's endpoint volume
/// (<c>IAudioEndpointVolume</c> via NAudio), which fires for volume keys, the tray slider and apps, and shows a
/// 1.5 s transient. Re-subscribes when the default device changes (headphones plugged in); switching devices
/// does not show the HUD. Windows' own flyout is not hidden (no supported API; see KNOWN_ISSUES).
/// </summary>
public sealed class VolumeProvider : IActivityProvider
{
    public const string ProviderId = "volume";

    // _gate guards COM (subscribe/unsubscribe); the volume callback never takes it, because unregistering a
    // callback can wait for one in flight. _lastGate guards only the last value.
    private readonly object _gate = new();
    private readonly object _lastGate = new();
    private volatile IActivitySink? _sink;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private MMDeviceNotificationClient? _notifications;
    private (double Level, bool Muted)? _last;
    private int _generation;

    public string Id => ProviderId;

    public string DisplayName => "Volume";

    public void Start(IActivitySink sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _generation++;
            _enumerator = new MMDeviceEnumerator();
            // Raised on the audio worker thread (no SynchronizationContext): the handler only queues work.
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            if (!SubscribeDefaultDevice())
            {
                sink.ReportUnavailable("no audio output device");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _sink = null;
            _generation++;
            UnsubscribeDevice();
            if (_notifications is not null)
            {
                _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                try
                {
                    _notifications.Dispose();
                }
                catch (Exception)
                {
                    // Audio service restarted: the registration is already gone.
                }
            }

            _notifications = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    public bool OnAction(string activityId, string actionId) => false;

    /// <summary>Subscribes to the current default render device. Caller holds <see cref="_gate"/>.</summary>
    private bool SubscribeDefaultDevice()
    {
        UnsubscribeDevice();
        if (_enumerator is null || !_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            return false;
        }

        _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var volume = _device.AudioEndpointVolume;
        lock (_lastGate)
        {
            _last = (volume.MasterVolumeLevelScalar, volume.Mute); // the current value is not a change
        }

        volume.OnVolumeNotification += OnVolumeNotification;
        return true;
    }

    private void UnsubscribeDevice()
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
        }
        catch (Exception)
        {
            // Device already removed.
        }

        _device.Dispose();
        _device = null;
    }

    /// <summary>Called on a CoreAudio thread.</summary>
    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        bool changed;
        lock (_lastGate)
        {
            changed = HudActivities.IsVolumeChange(_last, data.MasterVolume, data.Muted);
            _last = (data.MasterVolume, data.Muted);
        }

        if (changed)
        {
            _sink?.Start(HudActivities.Volume(data.MasterVolume, data.Muted));
        }
    }

    /// <summary>The default device changed: re-subscribe off the notification thread (CoreAudio forbids blocking there).</summary>
    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia)
        {
            return;
        }

        int generation;
        lock (_gate)
        {
            generation = _generation;
        }

        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (generation != _generation || _sink is null)
                {
                    return;
                }

                try
                {
                    SubscribeDefaultDevice();
                    Log.Information("Volume HUD: default output device changed; re-subscribed");
                }
                catch (Exception ex)
                {
                    Log.Warning("Volume HUD: re-subscribe failed ({Error})", ex.GetType().Name);
                }
            }
        });
    }
}
