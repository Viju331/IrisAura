using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Timers;

/// <summary>
/// The Island's own countdown timer (BLUEPRINT §8.6.10). Started from the menu, the <c>Ctrl+Alt+Shift+T</c>
/// shortcut or (Phase 5) the SDK. One timer at a time; starting another replaces it.
/// <list type="bullet">
/// <item>Priority 60, raised to 85 in the last 10 s (§8.5).</item>
/// <item>Pause/resume, +1 min and cancel from the expanded view.</item>
/// <item>When it ends: a "Timer done" banner and <see cref="Finished"/> (the Island plays a sound and shakes).</item>
/// <item>Survives restarts (<see cref="TimerStore"/>). A timer that ended while the Island was closed shows the
///   banner once on start, without sound.</item>
/// </list>
/// No polling: one timer callback at the 10 s mark and one at the end. Thread-safe.
/// </summary>
public sealed class TimerProvider(TimerStore store, TimeProvider? time = null) : IActivityProvider
{
    public const string ProviderId = "timers";
    public const string ActivityId = "timer";
    public const string DoneId = "timer:done";
    public const int FinishingPriority = 85;
    public static readonly TimeSpan FinishingWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private IActivitySink? _sink;
    private TimerState? _state;
    private ITimer? _wake;

    /// <summary>The timer reached zero while the Island was running (play a sound, shake).</summary>
    public event EventHandler? Finished;

    public string Id => ProviderId;

    public string DisplayName => "Timers";

    public TimerState? Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _sink is not null;
            }
        }
    }

    public void Start(IActivitySink sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _state = store.Load();
            if (_state is { IsPaused: false } running && running.Remaining(_time.GetUtcNow()) <= TimeSpan.Zero)
            {
                // Ended while the Island was not running.
                _state = null;
                store.Save(null);
                sink.Start(DoneBanner(running.Duration, missed: true));
                return;
            }

            PublishLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _sink = null;
            _wake?.Dispose();
            _wake = null;

            // _state stays persisted, so the timer continues after a restart.
        }
    }

    public bool OnAction(string activityId, string actionId)
    {
        if (activityId != ActivityId)
        {
            return false;
        }

        switch (actionId)
        {
            case ActivityActions.Toggle:
            case ActivityActions.Primary:
                TogglePause();
                return true;
            case ActivityActions.AddMinute:
                AddMinute();
                return true;
            case ActivityActions.Cancel:
                Cancel();
                return true;
            default:
                return false; // "open": the Island's own timer has no app
        }
    }

    public void StartTimer(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        Change(_ => TimerState.StartNew(duration, _time.GetUtcNow()));
    }

    /// <summary>Shortcut behaviour: starts a 5-minute timer, or pauses/resumes the current one.</summary>
    public void StartOrToggle()
    {
        if (Current is null)
        {
            StartTimer(DefaultDuration);
        }
        else
        {
            TogglePause();
        }
    }

    public void TogglePause()
    {
        var now = _time.GetUtcNow();
        Change(s => s is null ? null : s.IsPaused ? s.Resume(now) : s.Pause(now));
    }

    public void AddMinute() => Change(s => s?.AddTime(TimeSpan.FromMinutes(1)));

    public void Cancel()
    {
        lock (_gate)
        {
            _state = null;
            store.Save(null);
            _wake?.Dispose();
            _wake = null;
            _sink?.End(ActivityId);
        }
    }

    /// <summary>Re-checks the timer now (timer callback; tests call it after advancing the clock).</summary>
    internal void Wake()
    {
        var finished = false;
        lock (_gate)
        {
            if (_sink is null || _state is not { IsPaused: false } state)
            {
                return;
            }

            if (state.Remaining(_time.GetUtcNow()) <= TimeSpan.Zero)
            {
                _state = null;
                store.Save(null);
                _wake?.Dispose();
                _wake = null;
                _sink.End(ActivityId);
                _sink.Start(DoneBanner(state.Duration, missed: false));
                finished = true;
            }
            else
            {
                PublishLocked();
            }
        }

        if (finished)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    public static ActivitySpec Activity(TimerState state, DateTimeOffset now)
    {
        var remaining = state.Remaining(now);
        var priority = !state.IsPaused && remaining <= FinishingWindow ? FinishingPriority : ActivityDefaults.Priority(ActivityKind.Timer);
        var text = TimerState.Format(remaining);
        return new ActivitySpec(
            ActivityId,
            ActivityKind.Timer,
            new CompactContent(LeadingGlyph: "Aura.Glyph.Timer", TrailingText: text, TrailingProgress: state.FractionRemaining(now)),
            new ExpandedContent("Timer", text),
            Priority: priority,
            AccessibleText: state.IsPaused ? $"Timer paused, {text} left" : $"Timer, {text} left",
            View: state);
    }

    public static ActivitySpec DoneBanner(TimeSpan duration, bool missed) => new(
        DoneId,
        ActivityKind.Timer,
        new CompactContent(LeadingGlyph: "Aura.Glyph.Timer", LeadingText: "Timer done", Tone: ActivityTone.Alert),
        new ExpandedContent("Timer done", missed ? $"{TimerState.Describe(duration)} ended while the Island was closed" : TimerState.Describe(duration)),
        Priority: FinishingPriority,
        IsTransient: true,
        Ttl: TimeSpan.FromSeconds(5),
        AccessibleText: "Timer done");

    private void Change(Func<TimerState?, TimerState?> change)
    {
        lock (_gate)
        {
            if (_sink is null)
            {
                return;
            }

            _state = change(_state);
            store.Save(_state);
            PublishLocked();
        }
    }

    /// <summary>Publishes the timer and schedules the next wake-up (10 s mark or end). Caller holds the lock.</summary>
    private void PublishLocked()
    {
        _wake?.Dispose();
        _wake = null;
        if (_sink is null || _state is not { } state)
        {
            return;
        }

        var now = _time.GetUtcNow();
        _sink.Start(Activity(state, now));
        if (state.IsPaused)
        {
            return;
        }

        var remaining = state.Remaining(now);
        var next = remaining > FinishingWindow ? remaining - FinishingWindow : remaining;
        _wake = _time.CreateTimer(_ => Wake(), null, next + TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
    }
}
