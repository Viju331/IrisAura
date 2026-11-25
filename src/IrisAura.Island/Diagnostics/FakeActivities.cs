using System.Windows.Threading;
using IrisAura.Island.Shell;
using IrisAura.Island.Views;

namespace IrisAura.Island.Diagnostics;

// Fake activities for Phase 1 (BLUEPRINT §10 Phase 1: "fake activities"). They exercise every
// visual state without any real provider and read no system data. Replaced by the activity
// engine (Phase 2) and real providers (Phases 3–5).

/// <summary>Fake "now playing" media with an animated waveform.</summary>
public sealed class FakeMediaActivity : ObservableObject, IIslandActivityView, IPanelAwareView
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(223);
    private readonly DispatcherTimer _timer;
    private readonly double[] _phase = [0.0, 1.3, 2.1, 0.7, 1.8];
    private double[] _bars = [0.2, 0.2, 0.2, 0.2, 0.2];
    private TimeSpan _position = TimeSpan.FromSeconds(72);
    private bool _isPlaying = true;
    private bool _visible;
    private double _time;
    private double _sinceTimeline;

    public FakeMediaActivity(string title = "Midnight Signals", string artist = "The Aura Ensemble", string app = "Media player")
    {
        Title = title;
        Artist = artist;
        AppName = app;
        // Note: the (interval, priority, handler, dispatcher) constructor starts the timer at once; this one does not.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += (_, _) => Advance(_timer.Interval.TotalSeconds);
        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        RestartCommand = new RelayCommand(() =>
        {
            _position = TimeSpan.Zero;
            Advance(0);
        });
    }

    public RelayCommand PlayPauseCommand { get; }

    /// <summary>Previous/next both restart the fake track.</summary>
    public RelayCommand RestartCommand { get; }

    public string Title { get; }

    public string Artist { get; }

    public string AppName { get; }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                UpdateTimer();
                if (!value)
                {
                    Bars = [0.12, 0.12, 0.12, 0.12, 0.12];
                }
            }
        }
    }

    /// <summary>Five bar levels, 0–1.</summary>
    public double[] Bars
    {
        get => _bars;
        private set => SetProperty(ref _bars, value);
    }

    public double Progress => _position.TotalSeconds / Duration.TotalSeconds;

    public string ElapsedText => Format(_position);

    public string RemainingText => "-" + Format(Duration - _position);

    public string AccessibleDescription => $"Now playing {Title} by {Artist}{(IsPlaying ? string.Empty : ", paused")}";

    public ExpandedKind? ExpandedKind => Shell.ExpandedKind.Media;

    public double ExpandedHeight => 180;

    public void TogglePlayPause() => IsPlaying = !IsPlaying;

    /// <summary>Like the real media view: 15 Hz waveform inside the panel.</summary>
    public void SetInPanel(bool inPanel) => _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / (inPanel ? 15 : 30));

    public void SetVisible(bool visible)
    {
        _visible = visible;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (_visible && _isPlaying)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Advance(double dt)
    {
        _time += dt;
        _position += TimeSpan.FromSeconds(dt);
        if (_position >= Duration)
        {
            _position = TimeSpan.Zero;
        }

        var bars = new double[5];
        for (var i = 0; i < bars.Length; i++)
        {
            var v = 0.55 + (0.3 * Math.Sin((_time * (5.0 + i)) + _phase[i])) + (0.15 * Math.Sin((_time * 11.3) + (i * 0.9)));
            bars[i] = Math.Clamp(v, 0.12, 1);
        }

        Bars = bars;

        // Timeline text and bar at 4 Hz, like the real media view (re-laying out text at 30 Hz is not representative).
        _sinceTimeline += dt;
        if (_sinceTimeline >= 0.25 || dt == 0)
        {
            _sinceTimeline = 0;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(RemainingText));
        }
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}

/// <summary>Fake countdown timer.</summary>
public sealed class FakeTimerActivity : ObservableObject, IIslandActivityView
{
    private readonly DispatcherTimer _timer;
    private TimeSpan _remaining;
    private bool _running = true;

    public FakeTimerActivity(TimeSpan total)
    {
        Total = total;
        _remaining = total;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        ToggleCommand = new RelayCommand(ToggleRunning);
        AddMinuteCommand = new RelayCommand(AddMinute);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised when the user cancels the timer.</summary>
    public event EventHandler? Cancelled;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand AddMinuteCommand { get; }

    public RelayCommand CancelCommand { get; }

    public TimeSpan Total { get; }

    public string RemainingText => $"{(int)_remaining.TotalMinutes:00}:{_remaining.Seconds:00}";

    /// <summary>Fraction remaining, 0–1.</summary>
    public double Progress => Total.TotalSeconds > 0 ? _remaining.TotalSeconds / Total.TotalSeconds : 0;

    public bool IsRunning
    {
        get => _running;
        set
        {
            if (SetProperty(ref _running, value))
            {
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    public string ToggleText => _running ? "Pause" : "Resume";

    public string AccessibleDescription => $"Timer, {RemainingText} remaining";

    public ExpandedKind? ExpandedKind => Shell.ExpandedKind.Generic;

    public double ExpandedHeight => 140;

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void ToggleRunning() => IsRunning = !IsRunning;

    public void AddMinute()
    {
        _remaining += TimeSpan.FromMinutes(1);
        Notify();
    }

    private void Tick()
    {
        if (!_running || _remaining <= TimeSpan.Zero)
        {
            return;
        }

        _remaining -= TimeSpan.FromSeconds(1);
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
    }
}

/// <summary>Fake notification banner. Not expandable.</summary>
public sealed class FakeBannerActivity(string appName, string title, string text) : ObservableObject, IIslandActivityView
{
    public string AppName { get; } = appName;

    public string Title { get; } = title;

    public string Text { get; } = text;

    public string AccessibleDescription => $"{AppName}: {Title}. {Text}";

    public ExpandedKind? ExpandedKind => null;

    public double ExpandedHeight => 0;

    public void SetVisible(bool visible)
    {
    }
}
