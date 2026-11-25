using IrisAura.Island.Activities;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Privacy;

namespace IrisAura.Island.Providers.Calls;

/// <summary>
/// Call detection (beta, off by default; BLUEPRINT §8.6.8). Uses the shared microphone monitor and, as
/// corroboration, whether the same app has an active audio output session. Shows a Call activity with an elapsed
/// timer; clicking opens the app. Accept/decline are not possible. Ticks once a second only while a call app holds
/// the microphone.
/// </summary>
public sealed class CallProvider : IActivityProvider
{
    public const string ProviderId = "calls";
    public const string ActivityId = "call";

    private readonly ICapabilityUsageSource _source;
    private readonly IAudioSessionActivity _audio;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CallDetector _detector;
    private IActivitySink? _sink;
    private ITimer? _ticker;

    public CallProvider(ICapabilityUsageSource source, IAudioSessionActivity audio, TimeProvider? time = null, IReadOnlyList<CallApp>? apps = null)
    {
        _source = source;
        _audio = audio;
        _time = time ?? TimeProvider.System;
        Apps = apps ?? CallApp.Defaults;
        _detector = new CallDetector(Apps);
    }

    public IReadOnlyList<CallApp> Apps { get; }

    public string Id => ProviderId;

    public string DisplayName => "Call detection (beta)";

    public void Start(IActivitySink sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _detector = new CallDetector(Apps);
        }

        _source.Changed += OnChanged;
        _source.Acquire();
        Evaluate();
    }

    public void Stop()
    {
        _source.Changed -= OnChanged;
        _source.Release();
        lock (_gate)
        {
            _sink = null;
            _ticker?.Dispose();
            _ticker = null;
        }
    }

    public bool OnAction(string activityId, string actionId) => false;

    /// <summary>Re-evaluates now (tests; also driven by the monitor and the 1 s ticker).</summary>
    internal void Evaluate()
    {
        IActivitySink? sink;
        CallChange change;
        CallApp? app;
        DateTimeOffset? started;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_sink is null)
            {
                return;
            }

            var snapshot = _source.Current;
            change = _detector.Observe(snapshot, a => _audio.IsPlaying(a.ProcessNames), now);
            app = _detector.ActiveApp;
            started = _detector.StartedAt;
            sink = _sink;

            var tick = _detector.NeedsTicks(snapshot);
            if (tick && _ticker is null)
            {
                _ticker = _time.CreateTimer(_ => Evaluate(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
            else if (!tick && _ticker is not null)
            {
                _ticker.Dispose();
                _ticker = null;
            }
        }

        switch (change)
        {
            case CallChange.Started when app is not null:
                sink.Start(CallDetector.Activity(app, TimeSpan.Zero));
                break;
            case CallChange.Tick when app is not null && started is { } s:
                var spec = CallDetector.Activity(app, now - s);
                sink.Update(new ActivityUpdate(ActivityId) { Compact = spec.Compact, Expanded = spec.Expanded, AccessibleText = spec.AccessibleText });
                break;
            case CallChange.Ended:
                sink.End(ActivityId, "Call ended", TimeSpan.FromSeconds(2));
                break;
        }
    }

    private void OnChanged(object? sender, CapabilitySnapshot snapshot) => Evaluate();
}
