using System.Windows.Threading;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Timers;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Views;

/// <summary>
/// View model for the Island's timer (BLUEPRINT §8.6.10): compact = timer ring + remaining <c>mm:ss</c>; expanded =
/// large countdown with pause/resume, +1 min and cancel. Counts down from the <see cref="TimerState"/> payload with a
/// 1 Hz timer that runs only while the activity is visible.
/// </summary>
public sealed class TimerActivityView : ObservableObject, IActivityViewModel
{
    private readonly DispatcherTimer _ticker;
    private readonly TimeProvider _time;
    private TimerState _state;
    private string _id;

    public TimerActivityView(IslandActivity activity, Action<string, string> invokeAction, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _id = activity.Id;
        _state = activity.View as TimerState ?? new TimerState(TimeSpan.Zero, null, TimeSpan.Zero);
        _ticker = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Refresh();
        ToggleCommand = new RelayCommand(() => invokeAction(_id, ActivityActions.Toggle));
        AddMinuteCommand = new RelayCommand(() => invokeAction(_id, ActivityActions.AddMinute));
        CancelCommand = new RelayCommand(() => invokeAction(_id, ActivityActions.Cancel));
    }

    public RelayCommand ToggleCommand { get; }

    public RelayCommand AddMinuteCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string RemainingText => TimerState.Format(_state.Remaining(_time.GetUtcNow()));

    /// <summary>Fraction remaining, 0–1 (the ring empties as time runs out).</summary>
    public double Progress => _state.FractionRemaining(_time.GetUtcNow());

    public bool IsPaused => _state.IsPaused;

    public string ToggleText => _state.IsPaused ? "Resume" : "Pause";

    public string Caption => _state.IsPaused ? "Timer · paused" : "Timer";

    public string AccessibleDescription => _state.IsPaused ? $"Timer paused, {RemainingText} left" : $"Timer, {RemainingText} left";

    public ExpandedKind? ExpandedKind => Shell.ExpandedKind.Generic;

    public double ExpandedHeight => 140;

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Refresh();
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
        }
    }

    public void Apply(IslandActivity activity)
    {
        _id = activity.Id;
        if (activity.View is TimerState state)
        {
            _state = state;
        }

        OnPropertyChanged(string.Empty);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
    }
}
