using System.ComponentModel;
using System.Windows.Threading;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Privacy;

namespace IrisAura.Island.Diagnostics;

/// <summary>Fake scenarios used by the self-test.</summary>
public enum FakeScenario
{
    Idle,
    Media,
    Timer,

    /// <summary>Timer (priority 60) and media (50): the arbiter puts the timer in the pill and media in the bubble.</summary>
    MediaAndTimer,
}

/// <summary>
/// A real <see cref="IActivityProvider"/> that produces fake activities for the debug panel and the
/// self-test (BLUEPRINT §10 Phase 1–2). Everything goes through the engine, so priorities, transients,
/// expiry, swap, quiet mode and provider-failure isolation are exercised for real. Reads no system data.
/// </summary>
public sealed class DebugActivityProvider : IActivityProvider
{
    public const string ProviderId = "debug";
    public const string MediaId = "debug:media";
    public const string TimerId = "debug:timer";
    public const string NotificationId = "debug:notification";
    public const string VolumeId = "debug:volume";
    public const string ChargingId = "debug:charging";
    public const string CallId = "debug:call";
    public const string DownloadId = "debug:download";
    public const string CustomId = "debug:custom";
    public const string MicrophoneId = "debug:microphone";
    public const string CameraId = "debug:camera";
    public const string BluetoothId = "debug:bluetooth";

    private readonly DispatcherTimer _ticker;
    private IActivitySink? _sink;
    private FakeTimerActivity? _timer;
    private int _timerPriority;
    private DateTimeOffset? _callStarted;
    private double? _download;
    private double _volume = 0.4;

    public DebugActivityProvider()
    {
        Media.PropertyChanged += OnMediaChanged;
        _ticker = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Tick();
    }

    public string Id => ProviderId;

    public string DisplayName => "Debug (fake activities)";

    public FakeMediaActivity Media { get; } = new();

    public bool IsRunning => _sink is not null;

    public void Start(IActivitySink sink)
    {
        _sink = sink;
        _ticker.Start();
    }

    public void Stop()
    {
        _sink = null;
        _ticker.Stop();
        _timer = null;
        _callStarted = null;
        _download = null;
    }

    public bool OnAction(string activityId, string actionId)
    {
        switch (activityId, actionId)
        {
            case (MediaId, ActivityActions.Primary):
                Media.TogglePlayPause();
                return true;
            case (TimerId, ActivityActions.Primary):
                _timer?.ToggleRunning();
                return true;
            case (CallId, "end"):
                EndCall();
                return true;
            case (DownloadId, "cancel"):
                _download = null;
                _sink?.End(DownloadId, "Cancelled", TimeSpan.FromSeconds(2));
                return true;
            case (CustomId, "done"):
                _sink?.End(CustomId, "Deployed ✓", TimeSpan.FromSeconds(3));
                return true;
            case (CustomId, "fail"):
                // Simulates a provider bug: the host must disable this provider only.
                throw new InvalidOperationException("Simulated provider failure (debug action)");
            default:
                return false; // e.g. "open": fake activities have no app to open
        }
    }

    // ---------------------------------------------------------------- scenarios

    public void Show(FakeScenario scenario)
    {
        EndPersistent();
        if (scenario is FakeScenario.Media or FakeScenario.MediaAndTimer)
        {
            StartMedia();
        }

        if (scenario is FakeScenario.Timer or FakeScenario.MediaAndTimer)
        {
            StartTimer(TimeSpan.FromMinutes(5));
        }
    }

    public void StartMedia() => _sink?.Start(new ActivitySpec(
        MediaId, ActivityKind.Media, new CompactContent(), SourceAppId: "debug",
        AccessibleText: Media.AccessibleDescription, View: Media, IsPaused: !Media.IsPlaying));

    public void StopMedia() => _sink?.End(MediaId);

    public void StartTimer(TimeSpan duration)
    {
        if (_timer is not null)
        {
            _timer.Cancelled -= OnTimerCancelled;
        }

        _timer = new FakeTimerActivity(duration);
        _timer.Cancelled += OnTimerCancelled;
        _timerPriority = ActivityDefaults.Priority(ActivityKind.Timer);
        _sink?.Start(new ActivitySpec(
            TimerId, ActivityKind.Timer, new CompactContent(), SourceAppId: "debug",
            AccessibleText: _timer.AccessibleDescription, View: _timer));
    }

    public void StopTimer()
    {
        _timer = null;
        _sink?.End(TimerId);
    }

    public void Notify() => _sink?.Start(new ActivitySpec(
        NotificationId, ActivityKind.Notification,
        new CompactContent(LeadingText: "Messages"),
        new ExpandedContent("Alex", "Are we still on for the 4 pm review?"),
        AccessibleText: "Messages: Alex. Are we still on for the 4 pm review?"));

    /// <summary>Volume HUD; repeated presses update the same transient and extend its 1.5 s lifetime.</summary>
    public void VolumeStep(double delta)
    {
        _volume = Math.Clamp(_volume + delta, 0, 1);
        _sink?.Start(new ActivitySpec(
            VolumeId, ActivityKind.Volume,
            new CompactContent(LeadingText: "Volume", TrailingProgress: _volume),
            new ExpandedContent("Volume", $"{_volume * 100:0}%"),
            AccessibleText: $"Volume {_volume * 100:0} percent"));
    }

    public void Charging() => _sink?.Start(new ActivitySpec(
        ChargingId, ActivityKind.Charging,
        new CompactContent(LeadingText: "Charging", TrailingProgress: 0.64),
        new ExpandedContent("Charging", "64%"),
        AccessibleText: "Charging, 64 percent"));

    public void StartCall()
    {
        _callStarted = DateTimeOffset.UtcNow;
        PublishCall(true);
    }

    public void EndCall()
    {
        _callStarted = null;
        _sink?.End(CallId, "Call ended", TimeSpan.FromSeconds(2));
    }

    public void StartDownload()
    {
        _download = 0;
        PublishDownload(true);
    }

    public void StartCustom(int priority) => _sink?.Start(new ActivitySpec(
        CustomId, ActivityKind.Custom,
        new CompactContent(LeadingGlyph: "Aura.Glyph.Next", TrailingText: "37%"),
        new ExpandedContent("Deploying API", "Stage 3 of 5", 0.37,
            [new ActivityButton("done", "Finish"), new ActivityButton("fail", "Simulate crash")]),
        Priority: priority, SourceAppId: "debug"));

    /// <summary>Fake "device connected" banner (Phase 4B device design).</summary>
    public void Bluetooth(ActivitySpec spec) => _sink?.Start(spec with { Id = BluetoothId });

    public void EndBluetooth() => _sink?.End(BluetoothId);

    /// <summary>Fake microphone / camera indicators (privacy dots).</summary>
    public void PrivacyIndicator(bool microphone, bool camera)
    {
        var user = new CapabilityUser("debug", "Debug panel", null, null);
        Toggle(MicrophoneId, microphone ? PrivacyProvider.Indicator(Capability.Microphone, [user]) : null);
        Toggle(CameraId, camera ? PrivacyProvider.Indicator(Capability.Camera, [user]) : null);

        void Toggle(string id, ActivitySpec? spec)
        {
            if (spec is null)
            {
                _sink?.End(id);
            }
            else
            {
                _sink?.Start(spec with { Id = id });
            }
        }
    }

    /// <summary>Simulates an unrecoverable provider error.</summary>
    public void Fail() => _sink?.ReportFailure(new InvalidOperationException("Simulated provider failure (debug panel)"));

    public void EndPersistent()
    {
        StopMedia();
        StopTimer();
        if (_callStarted is not null)
        {
            _callStarted = null;
            _sink?.End(CallId);
        }

        if (_download is not null)
        {
            _download = null;
            _sink?.End(DownloadId);
        }

        _sink?.End(CustomId);
    }

    // ---------------------------------------------------------------- internals

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FakeMediaActivity.IsPlaying))
        {
            _sink?.Update(new ActivityUpdate(MediaId) { IsPaused = !Media.IsPlaying, AccessibleText = Media.AccessibleDescription });
        }
    }

    private void OnTimerCancelled(object? sender, EventArgs e) => StopTimer();

    private void Tick()
    {
        if (_timer is not null)
        {
            // BLUEPRINT §8.5: a timer finishing in < 10 s is promoted to priority 85.
            var priority = _timer.Progress * _timer.Total.TotalSeconds < 10 ? 85 : ActivityDefaults.Priority(ActivityKind.Timer);
            if (priority != _timerPriority)
            {
                _timerPriority = priority;
                _sink?.Update(new ActivityUpdate(TimerId) { Priority = priority });
            }
        }

        if (_callStarted is not null)
        {
            PublishCall(false);
        }

        if (_download is { } progress)
        {
            _download = Math.Min(1, progress + 0.07);
            if (_download >= 1)
            {
                _download = null;
                _sink?.End(DownloadId, "Done ✓", TimeSpan.FromSeconds(3));
            }
            else
            {
                PublishDownload(false);
            }
        }
    }

    private void PublishCall(bool start)
    {
        var elapsed = DateTimeOffset.UtcNow - (_callStarted ?? DateTimeOffset.UtcNow);
        var text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        var compact = new CompactContent(LeadingText: "Call", TrailingText: text);
        var expanded = new ExpandedContent("Call (demo)", text, Buttons: [new ActivityButton("end", "End demo call")]);
        if (start)
        {
            _sink?.Start(new ActivitySpec(CallId, ActivityKind.Call, compact, expanded, SourceAppId: "debug", AccessibleText: "Call in progress"));
        }
        else
        {
            _sink?.Update(new ActivityUpdate(CallId) { Compact = compact, Expanded = expanded });
        }
    }

    private void PublishDownload(bool start)
    {
        var p = _download ?? 0;
        var compact = new CompactContent(LeadingText: "Download", TrailingProgress: p);
        var expanded = new ExpandedContent("quarterly-report.pdf", $"{p * 100:0}% (demo)", p, [new ActivityButton("cancel", "Cancel")]);
        if (start)
        {
            _sink?.Start(new ActivitySpec(DownloadId, ActivityKind.Download, compact, expanded, SourceAppId: "debug", AccessibleText: "Downloading quarterly-report.pdf"));
        }
        else
        {
            _sink?.Update(new ActivityUpdate(DownloadId) { Compact = compact, Expanded = expanded });
        }
    }
}
