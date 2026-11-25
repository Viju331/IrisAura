using System.Diagnostics;
using System.IO;
using System.Text.Json;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Media;

namespace IrisAura.Island.Diagnostics;

/// <summary>
/// <c>IrisAura.Island.exe --media-probe report.json</c>: per-app evidence for BLUEPRINT §8.12 / Phase 3
/// ("document per-app gaps"). Lists every Windows media session with what it publishes (status, timeline,
/// controls, whether title/artist/art exist) and whether its audio level could be matched to its own
/// session. Records <b>no titles, artists or artwork</b> — only presence flags.
/// </summary>
internal static class MediaProbe
{
    /// <param name="outputPath">Report path.</param>
    /// <param name="saveArtworkDirectory">Only when explicitly given (test tones): also saves each session's artwork there.</param>
    public static async Task<int> RunAsync(string outputPath, string? saveArtworkDirectory = null)
    {
        using var source = new GsmtcMediaSessionSource();
        using var meter = new CoreAudioLevelMeter();
        await source.StartAsync(CancellationToken.None);
        var snapshot = await source.SnapshotAsync(CancellationToken.None);

        var sessions = new List<object>();
        foreach (var s in snapshot.Sessions)
        {
            if (saveArtworkDirectory is not null && s.Thumbnail is { Length: > 0 } art)
            {
                Directory.CreateDirectory(saveArtworkDirectory);
                File.WriteAllBytes(Path.Combine(saveArtworkDirectory, $"artwork-{sessions.Count}.bin"), art);
            }

            var processName = MediaAppIdentity.ProcessNameFor(s.SourceAppId);
            var (peak, matched) = await SampleLevelAsync(meter, processName);
            var timeline = s.Timeline;
            sessions.Add(new
            {
                appId = s.SourceAppId,
                isCurrent = s.Key == snapshot.CurrentKey,
                status = s.Status.ToString(),
                hasTitle = s.Title is not null,
                hasArtist = s.Artist is not null,
                hasAlbum = s.Album is not null,
                hasArtwork = s.Thumbnail is { Length: > 0 },
                artworkBytes = s.Thumbnail?.Length ?? 0,
                timeline = timeline is null ? null : new
                {
                    hasDuration = timeline.HasDuration,
                    durationSeconds = Math.Round((timeline.End - timeline.Start).TotalSeconds, 1),
                    positionSeconds = Math.Round(timeline.Position.TotalSeconds, 1),
                    lastUpdatedAgeSeconds = timeline.LastUpdated == default ? (double?)null : Math.Round((DateTimeOffset.Now - timeline.LastUpdated).TotalSeconds, 1),
                },
                playbackRate = s.PlaybackRate,
                controls = s.Controls,
                processName,
                fallbackDisplayName = MediaAppIdentity.DisplayNameFor(s.SourceAppId),
                peakLevelMax = Math.Round(peak, 4),
                levelFromOwnSession = matched,
                wouldBeShown = MediaSessionSelector.IsShowable(s),
            });
        }

        // How chatty are the apps? (Change events in 5 s: metadata, playback and timeline notifications.)
        var events = 0;
        source.Changed += (_, _) => Interlocked.Increment(ref events);
        await Task.Delay(TimeSpan.FromSeconds(5));

        var selected = MediaSessionSelector.Select(snapshot.Sessions, snapshot.CurrentKey, null);
        var report = new
        {
            tool = "IrisAura.Island --media-probe",
            timestampUtc = DateTime.UtcNow,
            os = Environment.OSVersion.VersionString,
            sessionCount = snapshot.Sessions.Count,
            islandWouldShow = selected?.SourceAppId,
            changeEventsIn5Seconds = events,
            sessions,
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>Samples the level for 1.5 s at 30 Hz; returns the max and whether it came from the app's own session.</summary>
    private static async Task<(double Peak, bool Matched)> SampleLevelAsync(CoreAudioLevelMeter meter, string? processName)
    {
        var max = 0.0;
        var matched = false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            max = Math.Max(max, meter.GetPeak(processName));
            matched |= meter.LastMatchedSession;
            await Task.Delay(33);
        }

        return (max, matched);
    }
}
