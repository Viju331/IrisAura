namespace IrisAura.Island.Providers.Media;

/// <summary>Mirror of <c>GlobalSystemMediaTransportControlsSessionPlaybackStatus</c> (keeps tests free of WinRT).</summary>
public enum MediaPlaybackStatus
{
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

/// <summary>Timeline as last reported by the app (BLUEPRINT §8.6.1). Positions arrive irregularly; see <see cref="MediaTimeline"/>.</summary>
public sealed record MediaTimelineSnapshot(
    TimeSpan Start,
    TimeSpan End,
    TimeSpan Position,
    DateTimeOffset LastUpdated,
    TimeSpan MinSeek,
    TimeSpan MaxSeek)
{
    /// <summary>False when the app reports no duration (some browsers): hide the progress bar.</summary>
    public bool HasDuration => End > Start;
}

/// <summary>Which transport controls the app currently allows.</summary>
public sealed record MediaControls(bool CanPlay, bool CanPause, bool CanNext, bool CanPrevious, bool CanSeek, bool CanTogglePlayPause)
{
    public static MediaControls None { get; } = new(false, false, false, false, false, false);
}

/// <summary>One media session (one app) as seen through the Windows media controls.</summary>
/// <param name="Key">Identifier within one snapshot: source app id plus an index for apps with several sessions.</param>
/// <param name="SourceAppId">AppUserModelId reported by the app (e.g. "Spotify.exe", "MSEdge", a packaged AUMID).</param>
/// <param name="Title">Track title (never logged).</param>
/// <param name="Artist">Artist (never logged).</param>
/// <param name="Album">Album title (never logged).</param>
/// <param name="Status">Playback status.</param>
/// <param name="PlaybackRate">Playback rate (1 = normal); used to interpolate the position.</param>
/// <param name="Controls">Enabled controls.</param>
/// <param name="Timeline">Timeline, or <see langword="null"/> when unavailable.</param>
/// <param name="Thumbnail">Encoded album art bytes (PNG/JPEG), or <see langword="null"/>.</param>
public sealed record MediaSessionInfo(
    string Key,
    string SourceAppId,
    string? Title,
    string? Artist,
    string? Album,
    MediaPlaybackStatus Status,
    double PlaybackRate,
    MediaControls Controls,
    MediaTimelineSnapshot? Timeline,
    byte[]? Thumbnail)
{
    public bool IsPlaying => Status == MediaPlaybackStatus.Playing;

    /// <summary>Album-art cache key (BLUEPRINT §8.6.1: title|artist|app).</summary>
    public string ArtKey => $"{Title}|{Artist}|{SourceAppId}";
}

/// <summary>Payload of the media activity: the session shown plus the others (app switcher).</summary>
public sealed record MediaActivityState(MediaSessionInfo Session, IReadOnlyList<MediaSessionInfo> Others);

/// <summary>Timeline interpolation (BLUEPRINT §8.6.1): position = Position + (now − LastUpdatedTime) while playing, clamped.</summary>
public static class MediaTimeline
{
    /// <summary>Longest gap interpolated; beyond it the app probably stopped reporting and the last position is kept.</summary>
    public static readonly TimeSpan MaxExtrapolation = TimeSpan.FromHours(6);

    public static TimeSpan CurrentPosition(MediaTimelineSnapshot timeline, DateTimeOffset now, bool playing, double playbackRate)
    {
        var position = timeline.Position;
        if (playing && timeline.LastUpdated != default)
        {
            var elapsed = now - timeline.LastUpdated;
            if (elapsed > TimeSpan.Zero && elapsed < MaxExtrapolation)
            {
                var rate = playbackRate > 0 && double.IsFinite(playbackRate) ? playbackRate : 1.0;
                position += TimeSpan.FromTicks((long)(elapsed.Ticks * rate));
            }
        }

        if (position < timeline.Start)
        {
            return timeline.Start;
        }

        return timeline.HasDuration && position > timeline.End ? timeline.End : position;
    }

    /// <summary>Progress 0–1, or <see langword="null"/> when there is no duration.</summary>
    public static double? Progress(MediaTimelineSnapshot timeline, TimeSpan position) =>
        timeline.HasDuration
            ? Math.Clamp((position - timeline.Start).TotalSeconds / (timeline.End - timeline.Start).TotalSeconds, 0, 1)
            : null;

    /// <summary>"1:12" / "1:02:03".</summary>
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}

/// <summary>
/// Decides whether a new media state would change what the Island shows. Apps report timeline positions
/// frequently; when the new position matches the interpolated one (within <see cref="PositionTolerance"/>) and
/// nothing else changed, republishing would only cost CPU (measured in Phase 3: ADR 0006).
/// </summary>
public static class MediaChange
{
    public static readonly TimeSpan PositionTolerance = TimeSpan.FromSeconds(1.5);

    public static bool IsRedundant(MediaActivityState? previous, MediaActivityState next, DateTimeOffset now)
    {
        if (previous is null)
        {
            return false;
        }

        var a = previous.Session;
        var b = next.Session;
        if (a.Key != b.Key || a.SourceAppId != b.SourceAppId || a.Title != b.Title || a.Artist != b.Artist
            || a.Album != b.Album || a.Status != b.Status || !a.PlaybackRate.Equals(b.PlaybackRate)
            || a.Controls != b.Controls || !ReferenceEquals(a.Thumbnail, b.Thumbnail))
        {
            return false;
        }

        if (!previous.Others.Select(o => (o.Key, o.Status)).SequenceEqual(next.Others.Select(o => (o.Key, o.Status))))
        {
            return false;
        }

        return (a.Timeline, b.Timeline) switch
        {
            (null, null) => true,
            ({ } t1, { } t2) => t1.Start == t2.Start && t1.End == t2.End && t1.MinSeek == t2.MinSeek && t1.MaxSeek == t2.MaxSeek
                && (MediaTimeline.CurrentPosition(t1, now, a.IsPlaying, a.PlaybackRate)
                    - MediaTimeline.CurrentPosition(t2, now, b.IsPlaying, b.PlaybackRate)).Duration() <= PositionTolerance,
            _ => false,
        };
    }
}

/// <summary>
/// Chooses the session the Island shows (BLUEPRINT §8.6.1): the user's choice from the app switcher if it
/// still exists; otherwise the playing session (the system's current one if several play); otherwise the
/// current paused session. Closed, opened-but-idle and stopped sessions are not shown.
/// </summary>
public static class MediaSessionSelector
{
    public static bool IsShowable(MediaSessionInfo s) =>
        s.Status is MediaPlaybackStatus.Playing or MediaPlaybackStatus.Paused or MediaPlaybackStatus.Changing;

    public static MediaSessionInfo? Select(IReadOnlyList<MediaSessionInfo> sessions, string? currentKey, string? userKey)
    {
        var visible = sessions.Where(IsShowable).ToList();
        if (visible.Count == 0)
        {
            return null;
        }

        if (userKey is not null && visible.FirstOrDefault(s => s.Key == userKey) is { } chosen)
        {
            return chosen;
        }

        var playing = visible.Where(s => s.IsPlaying).ToList();
        if (playing.Count > 0)
        {
            return playing.FirstOrDefault(s => s.Key == currentKey) ?? playing[0];
        }

        return visible.FirstOrDefault(s => s.Key == currentKey) ?? visible[0];
    }
}

/// <summary>
/// Dominant album-art colour for tinting (BLUEPRINT §8.6.1): average of a small downscaled image,
/// excluding near-black and near-white pixels, brightened to stay readable on the black pill.
/// </summary>
public static class AlbumTint
{
    public const byte NearBlackMax = 40;
    public const byte NearWhiteMin = 220;
    public const double MinBrightness = 0.55;

    /// <summary>Computes the tint from BGRA32 pixels; <see langword="null"/> when no usable pixels.</summary>
    public static (byte R, byte G, byte B)? FromBgra(ReadOnlySpan<byte> bgra)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte pb = bgra[i], pg = bgra[i + 1], pr = bgra[i + 2], pa = bgra[i + 3];
            if (pa < 128)
            {
                continue;
            }

            var max = Math.Max(pr, Math.Max(pg, pb));
            var min = Math.Min(pr, Math.Min(pg, pb));
            if (max <= NearBlackMax || min >= NearWhiteMin)
            {
                continue;
            }

            r += pr;
            g += pg;
            b += pb;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        double ar = r / (double)count, ag = g / (double)count, ab = b / (double)count;
        var brightness = Math.Max(ar, Math.Max(ag, ab)) / 255.0;
        if (brightness < MinBrightness && brightness > 0)
        {
            var scale = MinBrightness / brightness;
            ar = Math.Min(255, ar * scale);
            ag = Math.Min(255, ag * scale);
            ab = Math.Min(255, ab * scale);
        }

        return ((byte)Math.Round(ar), (byte)Math.Round(ag), (byte)Math.Round(ab));
    }
}

/// <summary>
/// Maps a media session's AppUserModelId to the process name that plays its audio and to a fallback display
/// name. Unpackaged apps usually report their exe name; browsers use fixed ids. Unknown ids return the id itself
/// (process match is then best-effort and the waveform falls back to the whole device level).
/// </summary>
public static class MediaAppIdentity
{
    private static readonly Dictionary<string, (string Process, string Name)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chrome"] = ("chrome", "Google Chrome"),
        ["MSEdge"] = ("msedge", "Microsoft Edge"),
        ["308046B0AF4A39CB"] = ("firefox", "Firefox"),
        ["Firefox"] = ("firefox", "Firefox"),
        ["Brave"] = ("brave", "Brave"),
        ["Opera"] = ("opera", "Opera"),
        ["Spotify.exe"] = ("Spotify", "Spotify"),
        ["Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"] = ("Microsoft.Media.Player", "Media Player"),
        ["SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"] = ("Spotify", "Spotify"),
        ["vlc.exe"] = ("vlc", "VLC"),
        ["AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"] = ("AppleMusic", "Apple Music"),
    };

    /// <summary>
    /// Chromium browsers may report a per-install id such as "Chrome.VXSLNYZIFHXJXHXRSTL6TR5EKU" instead of
    /// "Chrome" (observed on Windows 11 build 26300 with a separate Chrome profile).
    /// </summary>
    private static readonly (string Prefix, string Process, string Name)[] ChromiumPrefixes =
    [
        ("Chrome.", "chrome", "Google Chrome"),
        ("MSEdge.", "msedge", "Microsoft Edge"),
        ("Brave.", "brave", "Brave"),
        ("Opera.", "opera", "Opera"),
        ("Vivaldi.", "vivaldi", "Vivaldi"),
    ];

    public static bool IsPackaged(string appId) => appId.Contains('!', StringComparison.Ordinal);

    private static (string Process, string Name)? Lookup(string appId)
    {
        if (Known.TryGetValue(appId, out var known))
        {
            return known;
        }

        foreach (var (prefix, process, name) in ChromiumPrefixes)
        {
            if (appId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !IsPackaged(appId))
            {
                return (process, name);
            }
        }

        return null;
    }

    /// <summary>Process name (without ".exe") expected to own the session's audio, or <see langword="null"/> when unknown.</summary>
    public static string? ProcessNameFor(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        if (Lookup(appId) is { } known)
        {
            return known.Process;
        }

        if (IsPackaged(appId))
        {
            return null; // packaged app: process name not derivable from the AUMID
        }

        var name = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId[..^4] : appId;
        name = Path.GetFileName(name);
        return name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : null;
    }

    /// <summary>Readable app name when nothing better can be resolved.</summary>
    public static string DisplayNameFor(string appId)
    {
        if (Lookup(appId) is { } known)
        {
            return known.Name;
        }

        if (IsPackaged(appId))
        {
            // "Publisher.AppName_hash!Entry": prefer the entry point, else the last part of the package name.
            var bang = appId.IndexOf('!', StringComparison.Ordinal);
            var entry = appId[(bang + 1)..];
            if (entry.Length > 0 && !entry.Equals("App", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            var package = appId[..bang];
            var underscore = package.IndexOf('_', StringComparison.Ordinal);
            var family = underscore > 0 ? package[..underscore] : package;
            var dot = family.LastIndexOf('.');
            return dot >= 0 ? family[(dot + 1)..] : family;
        }

        return ProcessNameFor(appId) ?? "Media";
    }
}
