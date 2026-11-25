using Windows.Media.Control;

namespace IrisAura.Island.Providers.Media;

/// <summary>Transport commands.</summary>
public enum MediaCommand
{
    TogglePlayPause,
    Next,
    Previous,
    Seek,
}

/// <summary>All sessions at one moment, plus the system's current session.</summary>
public sealed record MediaSnapshot(IReadOnlyList<MediaSessionInfo> Sessions, string? CurrentKey)
{
    public static MediaSnapshot Empty { get; } = new([], null);
}

/// <summary>Source of media sessions; the real one wraps the Windows media controls, tests use a fake.</summary>
public interface IMediaSessionSource : IDisposable
{
    /// <summary>Something changed (sessions, metadata, playback, timeline). May fire on any thread.</summary>
    event EventHandler? Changed;

    Task StartAsync(CancellationToken cancellationToken);

    Task<MediaSnapshot> SnapshotAsync(CancellationToken cancellationToken);

    Task<bool> ControlAsync(string key, MediaCommand command, TimeSpan? position = null);
}

/// <summary>
/// Windows <c>GlobalSystemMediaTransportControlsSessionManager</c> (GSMTC, BLUEPRINT §8.6.1): documented,
/// works for any app that publishes System Media Transport Controls. No undocumented NPSM APIs.
/// Album art is read once per title|artist|app and cached (16 entries, ≤ 4 MB each).
/// </summary>
public sealed class GsmtcMediaSessionSource : IMediaSessionSource
{
    private const long MaxThumbnailBytes = 4 * 1024 * 1024;
    private const int ThumbnailCacheSize = 16;

    private readonly object _gate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = [];
    private readonly Dictionary<string, byte[]?> _thumbnails = new(StringComparer.Ordinal);
    private readonly Queue<string> _thumbnailOrder = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _disposed;

    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Resubscribe();
    }

    public async Task<MediaSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var manager = _manager ?? throw new InvalidOperationException("Not started.");
        var sessions = manager.GetSessions();
        var current = manager.GetCurrentSession();
        var keys = Keys(sessions);
        string? currentKey = null;
        var result = new List<MediaSessionInfo>(sessions.Count);

        for (var i = 0; i < sessions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = sessions[i];
            if (current is not null && currentKey is null && session.SourceAppUserModelId == current.SourceAppUserModelId)
            {
                currentKey = keys[i];
            }

            result.Add(await DescribeAsync(session, keys[i]).ConfigureAwait(false));
        }

        return new MediaSnapshot(result, currentKey);
    }

    public async Task<bool> ControlAsync(string key, MediaCommand command, TimeSpan? position = null)
    {
        var manager = _manager ?? throw new InvalidOperationException("Not started.");
        var sessions = manager.GetSessions();
        var keys = Keys(sessions);
        var index = keys.IndexOf(key);
        if (index < 0)
        {
            return false;
        }

        var session = sessions[index];
        return command switch
        {
            MediaCommand.TogglePlayPause => await session.TryTogglePlayPauseAsync(),
            MediaCommand.Next => await session.TrySkipNextAsync(),
            MediaCommand.Previous => await session.TrySkipPreviousAsync(),
            MediaCommand.Seek when position is { } p => await session.TryChangePlaybackPositionAsync(p.Ticks),
            _ => false,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            Unsubscribe();
        }

        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
    }

    /// <summary>Key = app id, plus "#n" for an app's second and later sessions.</summary>
    private static List<string> Keys(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>(sessions.Count);
        foreach (var session in sessions)
        {
            var id = session.SourceAppUserModelId ?? string.Empty;
            var n = seen.GetValueOrDefault(id);
            seen[id] = n + 1;
            keys.Add(n == 0 ? id : $"{id}#{n}");
        }

        return keys;
    }

    private async Task<MediaSessionInfo> DescribeAsync(GlobalSystemMediaTransportControlsSession session, string key)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception)
        {
            // Some apps fail this call transiently while changing tracks; show what we have.
        }

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var c = playback?.Controls;
        var controls = c is null
            ? MediaControls.None
            : new MediaControls(c.IsPlayEnabled, c.IsPauseEnabled, c.IsNextEnabled, c.IsPreviousEnabled, c.IsPlaybackPositionEnabled, c.IsPlayPauseToggleEnabled);

        var info = new MediaSessionInfo(
            key,
            session.SourceAppUserModelId ?? string.Empty,
            NullIfEmpty(props?.Title),
            NullIfEmpty(props?.Artist) ?? NullIfEmpty(props?.AlbumArtist),
            NullIfEmpty(props?.AlbumTitle),
            MapStatus(playback?.PlaybackStatus),
            playback?.PlaybackRate ?? 1.0,
            controls,
            timeline is null
                ? null
                : new MediaTimelineSnapshot(timeline.StartTime, timeline.EndTime, timeline.Position, timeline.LastUpdatedTime, timeline.MinSeekTime, timeline.MaxSeekTime),
            null);

        return info with { Thumbnail = await ThumbnailAsync(info.ArtKey, props).ConfigureAwait(false) };
    }

    private async Task<byte[]?> ThumbnailAsync(string artKey, GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        lock (_gate)
        {
            if (_thumbnails.TryGetValue(artKey, out var cached))
            {
                return cached;
            }
        }

        byte[]? bytes = null;
        if (props?.Thumbnail is { } reference)
        {
            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream.Size is > 0 and <= MaxThumbnailBytes)
                {
                    using var read = stream.AsStreamForRead();
                    using var buffer = new MemoryStream();
                    await read.CopyToAsync(buffer).ConfigureAwait(false);
                    bytes = buffer.ToArray();
                }
            }
            catch (Exception)
            {
                bytes = null; // art unavailable: the view falls back to the app icon
            }
        }

        // Only cache real artwork: apps (e.g. Chrome) often publish the title first and the artwork a moment
        // later, so "no artwork yet" must be asked again on the next refresh.
        if (bytes is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_thumbnails.TryAdd(artKey, bytes))
            {
                _thumbnailOrder.Enqueue(artKey);
                while (_thumbnailOrder.Count > ThumbnailCacheSize)
                {
                    _thumbnails.Remove(_thumbnailOrder.Dequeue());
                }
            }
        }

        return bytes;
    }

    private static MediaPlaybackStatus MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackStatus.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
        _ => MediaPlaybackStatus.Closed,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private void Resubscribe()
    {
        lock (_gate)
        {
            Unsubscribe();
            if (_manager is null)
            {
                return;
            }

            foreach (var session in _manager.GetSessions())
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                _subscribed.Add(session);
            }
        }
    }

    private void Unsubscribe()
    {
        foreach (var session in _subscribed)
        {
            try
            {
                session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch (Exception)
            {
                // Session already gone.
            }
        }

        _subscribed.Clear();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Resubscribe();
        Raise();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Raise();

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Raise();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Raise();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Raise();

    private void Raise()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
