using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Media;
using IrisAura.Island.Shell;
using MediaTimeline = IrisAura.Island.Providers.Media.MediaTimeline;

namespace IrisAura.Island.Views;

/// <summary>Another playing/paused app in the expanded view's switcher.</summary>
public sealed record MediaSwitcherItem(string Name, RelayCommand Select);

/// <summary>
/// View model for the media activity (BLUEPRINT §8.6.1–§8.6.2): title/artist, album art (decoded off the UI
/// thread, cached by the source) with a dominant-colour tint, source app name/icon, interpolated progress,
/// transport controls, app switcher, and a waveform driven by the app's own audio level.
/// Timers run only while the activity is visible: 30 Hz waveform while playing (and until the bars settle
/// after pausing), 4 Hz progress while playing.
/// </summary>
public sealed class MediaActivityView : ObservableObject, IActivityViewModel, IPanelAwareView
{
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(1000.0 / 30);
    private static readonly TimeSpan PanelLevelInterval = TimeSpan.FromMilliseconds(1000.0 / 15);
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromMilliseconds(250);

    private readonly IAudioLevelSource _levels;
    private readonly AppVisualsResolver _visuals;
    private readonly Action<string, string> _invokeAction;
    private readonly Action<string> _openApp;
    private readonly WaveformSmoother _waveform = new();
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _timelineTimer;
    private readonly Brush _defaultAccent;
    private MediaActivityState _state;
    private string _activityId;
    private string? _accessible;
    private string? _artKey;
    private byte[]? _artBytes;
    private string? _visualsFor;
    private string? _processName;
    private ImageSource? _albumArt;
    private ImageSource? _appIcon;
    private string _appName;
    private Brush _accent;
    private IReadOnlyList<double> _bars;
    private IReadOnlyList<MediaSwitcherItem> _others = [];
    private double _progress;
    private string _elapsed = string.Empty;
    private string _remaining = string.Empty;
    private bool _visible;
    private long _lastLevelTick;

    public MediaActivityView(
        IslandActivity activity,
        IAudioLevelSource levels,
        AppVisualsResolver visuals,
        Action<string, string> invokeAction,
        Action<string> openApp)
    {
        _levels = levels;
        _visuals = visuals;
        _invokeAction = invokeAction;
        _openApp = openApp;
        _defaultAccent = (Brush?)Application.Current?.TryFindResource("Aura.Blue") ?? Brushes.SteelBlue;
        _accent = _defaultAccent;
        _bars = _waveform.Levels.ToArray();
        _state = (MediaActivityState)activity.View!;
        _activityId = activity.Id;
        _appName = MediaAppIdentity.DisplayNameFor(_state.Session.SourceAppId);

        _levelTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = LevelInterval };
        _levelTimer.Tick += (_, _) => OnLevelTick();
        _timelineTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimelineInterval };
        _timelineTimer.Tick += (_, _) => UpdateTimeline();

        PlayPauseCommand = new RelayCommand(() => _invokeAction(_activityId, "playpause"));
        NextCommand = new RelayCommand(() => _invokeAction(_activityId, "next"));
        PreviousCommand = new RelayCommand(() => _invokeAction(_activityId, "previous"));
        OpenAppCommand = new RelayCommand(() => _openApp(_activityId));
        SeekCommand = new RelayCommand<double>(Seek);

        Apply(activity);
    }

    // ---- bound properties

    public string Title => _state.Session.Title ?? _appName;

    public string? Artist => _state.Session.Artist;

    public bool HasArtist => !string.IsNullOrEmpty(Artist);

    public string AppName => _appName;

    public ImageSource? AppIcon => _appIcon;

    public ImageSource? AlbumArt => _albumArt;

    public bool HasAlbumArt => _albumArt is not null;

    /// <summary>Album-art tint, or <c>Aura.Blue</c> (BLUEPRINT §8.3 theme).</summary>
    public Brush AccentBrush => _accent;

    public bool IsPlaying => _state.Session.IsPlaying;

    public bool HasTimeline => _state.Session.Timeline?.HasDuration == true;

    public double Progress => _progress;

    public string ElapsedText => _elapsed;

    public string RemainingText => _remaining;

    public IReadOnlyList<double> Bars => _bars;

    public bool CanPlayPause => _state.Session.Controls is { } c && (c.CanTogglePlayPause || c.CanPlay || c.CanPause);

    public bool CanNext => _state.Session.Controls.CanNext;

    public bool CanPrevious => _state.Session.Controls.CanPrevious;

    public bool CanSeek => HasTimeline && _state.Session.Controls.CanSeek;

    public IReadOnlyList<MediaSwitcherItem> Others => _others;

    public bool HasOthers => _others.Count > 0;

    public RelayCommand PlayPauseCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand OpenAppCommand { get; }

    public RelayCommand<double> SeekCommand { get; }

    // ---- IIslandActivityView

    public string AccessibleDescription => _accessible ?? $"Now playing {Title}";

    public ExpandedKind? ExpandedKind => Shell.ExpandedKind.Media;

    public double ExpandedHeight => 180;

    /// <summary>Inside the panel the waveform updates at 15 Hz (smoothing is time-based, so it looks the same, only coarser).</summary>
    public void SetInPanel(bool inPanel) => _levelTimer.Interval = inPanel ? PanelLevelInterval : LevelInterval;

    public void SetVisible(bool visible)
    {
        _visible = visible;
        UpdateTimers();
    }

    public void Apply(IslandActivity activity)
    {
        _activityId = activity.Id;
        _accessible = activity.AccessibleText;
        _state = (MediaActivityState)activity.View!;
        var session = _state.Session;

        _processName = MediaAppIdentity.ProcessNameFor(session.SourceAppId);
        if (_visualsFor != session.SourceAppId)
        {
            _visualsFor = session.SourceAppId;
            _appName = MediaAppIdentity.DisplayNameFor(session.SourceAppId);
            _appIcon = null;
            _ = LoadVisualsAsync(session.SourceAppId);
        }

        // Reload when the track changes or when its artwork arrives after the title (same key, new bytes).
        if (_artKey != session.ArtKey || !ReferenceEquals(_artBytes, session.Thumbnail))
        {
            _artKey = session.ArtKey;
            _artBytes = session.Thumbnail;
            _ = LoadArtAsync(session.ArtKey, session.Thumbnail);
        }

        _others = _state.Others
            .Select(o => new MediaSwitcherItem(MediaAppIdentity.DisplayNameFor(o.SourceAppId), new RelayCommand(() => _invokeAction(_activityId, "select:" + o.Key))))
            .ToList();

        UpdateTimeline();
        UpdateTimers();
        OnPropertyChanged(string.Empty);
    }

    // ---- timers

    private void UpdateTimers()
    {
        var waveformNeeded = _visible && (IsPlaying || !_waveform.IsSettled);
        if (waveformNeeded && !_levelTimer.IsEnabled)
        {
            _lastLevelTick = Stopwatch.GetTimestamp();
            _levelTimer.Start();
        }
        else if (!waveformNeeded)
        {
            _levelTimer.Stop();
        }

        if (_visible && IsPlaying && HasTimeline)
        {
            _timelineTimer.Start();
        }
        else
        {
            _timelineTimer.Stop();
        }
    }

    private void OnLevelTick()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_lastLevelTick, now).TotalSeconds;
        _lastLevelTick = now;

        var peak = IsPlaying ? _levels.GetPeak(_processName) : 0;
        _waveform.Update(peak, dt, IsPlaying);
        _bars = _waveform.Levels.ToArray();
        OnPropertyChanged(nameof(Bars));

        if (!IsPlaying && _waveform.IsSettled)
        {
            _levelTimer.Stop();
        }
    }

    private void UpdateTimeline()
    {
        var session = _state.Session;
        if (session.Timeline is not { HasDuration: true } timeline)
        {
            return;
        }

        var position = MediaTimeline.CurrentPosition(timeline, DateTimeOffset.Now, session.IsPlaying, session.PlaybackRate);
        _progress = MediaTimeline.Progress(timeline, position) ?? 0;
        _elapsed = MediaTimeline.Format(position - timeline.Start);
        _remaining = "-" + MediaTimeline.Format(timeline.End - position);
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    private void Seek(double fraction)
    {
        if (_state.Session.Timeline is not { HasDuration: true } t || !CanSeek)
        {
            return;
        }

        var target = t.Start + TimeSpan.FromTicks((long)((t.End - t.Start).Ticks * Math.Clamp(fraction, 0, 1)));
        _invokeAction(_activityId, "seek:" + target.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- async visuals

    private async Task LoadVisualsAsync(string appId)
    {
        var visuals = await _visuals.ResolveAsync(appId).ConfigureAwait(true);
        if (_visualsFor != appId)
        {
            return;
        }

        _appName = visuals.Name;
        _appIcon = visuals.Icon;
        OnPropertyChanged(nameof(AppName));
        OnPropertyChanged(nameof(AppIcon));
        OnPropertyChanged(nameof(Title));
    }

    private async Task LoadArtAsync(string artKey, byte[]? bytes)
    {
        (BitmapSource? Art, Brush? Tint) result = (null, null);
        if (bytes is { Length: > 0 })
        {
            result = await Task.Run(() => (AppVisualsResolver.Decode(bytes, 112), TintFrom(bytes))).ConfigureAwait(true);
        }

        if (_artKey != artKey || !ReferenceEquals(_artBytes, bytes))
        {
            return; // a newer track or newer artwork arrived meanwhile
        }

        _albumArt = result.Art;
        _accent = result.Tint ?? _defaultAccent;
        OnPropertyChanged(nameof(AlbumArt));
        OnPropertyChanged(nameof(HasAlbumArt));
        OnPropertyChanged(nameof(AccentBrush));
    }

    /// <summary>Dominant colour from a 16 × 16 downscale (any thread).</summary>
    internal static Brush? TintFrom(byte[] bytes)
    {
        if (AppVisualsResolver.Decode(bytes, 16) is not { } small)
        {
            return null;
        }

        var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        if (AlbumTint.FromBgra(pixels) is not { } c)
        {
            return null;
        }

        var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
