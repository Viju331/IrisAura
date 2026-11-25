using System.Globalization;
using IrisAura.Island.Activities;
using Serilog;

namespace IrisAura.Island.Providers.Media;

/// <summary>
/// "Song playing in any app" (BLUEPRINT §8.6.1). Publishes one media activity for the chosen session
/// (playing first; the system's current one if several play; the user's switcher choice wins) with the
/// other sessions attached for the app switcher. Paused media is reported as paused, so the arbiter drops it
/// to the queue after 30 s.
/// <para>Resilience (§8.9): if the media manager fails (e.g. after Explorer restarts) the provider reconnects
/// with back-off (1, 2, 5, 10, 30 s); after <see cref="MaxConsecutiveFailures"/> failures it reports a fault.</para>
/// <para>Privacy: titles, artists and album art are only passed to the Island; never logged or stored.</para>
/// </summary>
public sealed class MediaProvider : IActivityProvider
{
    public const string ProviderId = "media";
    public const string ActivityId = "media";
    public const int MaxConsecutiveFailures = 8;

    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Func<IMediaSessionSource> _sourceFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private IActivitySink? _sink;
    private IMediaSessionSource? _source;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _sourceFailed;
    private Dictionary<string, MediaPlaybackStatus> _lastStatus = new(StringComparer.Ordinal);
    private string? _userKey;
    private string? _shownKey;
    private bool _published;
    private MediaActivityState? _lastPublished;
    private int _refreshQueued;

    public MediaProvider(Func<IMediaSessionSource> sourceFactory)
    {
        _sourceFactory = sourceFactory;
    }

    public string Id => ProviderId;

    public string DisplayName => "Media (Windows media sessions)";

    /// <summary>Reconnect delays (tests shorten them).</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; set; } = Backoff;

    /// <summary>Delay used to merge bursts of change events into one refresh.</summary>
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Completes after the first successful refresh (tests, diagnostics).</summary>
    internal TaskCompletionSource Ready { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Number of times the source was (re)created.</summary>
    public int Connections { get; private set; }

    /// <summary>Diagnostics: media states sent to the engine.</summary>
    public int Published { get; private set; }

    /// <summary>Diagnostics: refreshes that changed nothing visible and were not sent.</summary>
    public int SkippedRedundant { get; private set; }

    public void Start(IActivitySink sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _cts = new CancellationTokenSource();
            _published = false;
            _userKey = null;
            Ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _sink = null;
        }

        cts?.Cancel();
        DisposeSource();
    }

    public bool OnAction(string activityId, string actionId)
    {
        if (activityId != ActivityId)
        {
            return false;
        }

        switch (actionId)
        {
            case ActivityActions.Primary or "playpause":
                Control(MediaCommand.TogglePlayPause);
                return true;
            case "next":
                Control(MediaCommand.Next);
                return true;
            case "previous":
                Control(MediaCommand.Previous);
                return true;
        }

        if (actionId.StartsWith("seek:", StringComparison.Ordinal)
            && long.TryParse(actionId.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= 0)
        {
            Control(MediaCommand.Seek, TimeSpan.FromTicks(ticks));
            return true;
        }

        if (actionId.StartsWith("select:", StringComparison.Ordinal))
        {
            _userKey = actionId[7..];
            QueueRefresh();
            return true;
        }

        return false; // "open" is handled by the Island (window activation needs the UI thread)
    }

    /// <summary>Reads all sessions and publishes the chosen one. Safe to call concurrently.</summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _source;
            if (source is null)
            {
                return;
            }

            var snapshot = await source.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            Publish(snapshot);
            Ready.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning("Media provider refresh failed: {Error}", ex.GetType().Name);
            _sourceFailed?.TrySetResult();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var source = _sourceFactory();
                _sourceFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                source.Changed += OnSourceChanged;
                _source = source;
                Connections++;
                await source.StartAsync(cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (!_sourceFailed.Task.IsCompleted)
                {
                    failures = 0;
                    Log.Information("Media provider connected (connection {Count})", Connections);
                }

                await _sourceFailed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning("Media provider could not connect: {Error}", ex.GetType().Name);
            }

            DisposeSource();
            failures++;
            if (failures >= MaxConsecutiveFailures)
            {
                _sink?.ReportFailure(new InvalidOperationException($"Windows media sessions unavailable after {failures} attempts."));
                return;
            }

            try
            {
                await Task.Delay(RetryDelays[Math.Min(failures - 1, RetryDelays.Count - 1)], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        // A different session starting to play overrides the user's earlier switcher choice.
        if (_userKey is not null)
        {
            var chosen = snapshot.Sessions.FirstOrDefault(s => s.Key == _userKey);
            var otherStarted = snapshot.Sessions.Any(s => s.IsPlaying && s.Key != _userKey
                && (!_lastStatus.TryGetValue(s.Key, out var before) || before != MediaPlaybackStatus.Playing));
            if (chosen is null || (otherStarted && chosen.IsPlaying is false))
            {
                _userKey = null;
            }
        }

        _lastStatus = snapshot.Sessions.ToDictionary(s => s.Key, s => s.Status, StringComparer.Ordinal);

        var selected = MediaSessionSelector.Select(snapshot.Sessions, snapshot.CurrentKey, _userKey);
        if (selected is null)
        {
            _shownKey = null;
            _lastPublished = null;
            if (_published)
            {
                _published = false;
                sink.End(ActivityId);
            }

            return;
        }

        _shownKey = selected.Key;
        var others = snapshot.Sessions.Where(s => MediaSessionSelector.IsShowable(s) && s.Key != selected.Key).ToList();
        var state = new MediaActivityState(selected, others);
        if (_published && MediaChange.IsRedundant(_lastPublished, state, DateTimeOffset.Now))
        {
            SkippedRedundant++;
            return; // only a routine position sync that matches the interpolated position
        }

        _lastPublished = state;
        Published++;
        var app = MediaAppIdentity.DisplayNameFor(selected.SourceAppId);
        var title = selected.Title ?? app;
        var accessible = selected.Artist is null
            ? $"{(selected.IsPlaying ? "Now playing" : "Paused")}: {title}, in {app}"
            : $"{(selected.IsPlaying ? "Now playing" : "Paused")}: {title} by {selected.Artist}, in {app}";

        sink.Start(new ActivitySpec(
            ActivityId,
            ActivityKind.Media,
            new CompactContent(),
            new ExpandedContent(title, selected.Artist),
            SourceAppId: selected.SourceAppId,
            AccessibleText: accessible,
            View: state,
            IsPaused: !selected.IsPlaying));
        _published = true;
    }

    private void Control(MediaCommand command, TimeSpan? position = null)
    {
        var source = _source;
        var key = _shownKey;
        if (source is null || key is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await source.ControlAsync(key, command, position).ConfigureAwait(false);
                if (!ok)
                {
                    Log.Information("Media app declined {Command}", command);
                }
            }
            catch (Exception ex)
            {
                // A control failing (app closed, control not supported) is not a provider fault.
                Log.Information("Media {Command} failed: {Error}", command, ex.GetType().Name);
            }

            QueueRefresh();
        });
    }

    private void OnSourceChanged(object? sender, EventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, token).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
            }
        });
    }

    private void DisposeSource()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is null)
        {
            return;
        }

        source.Changed -= OnSourceChanged;
        try
        {
            source.Dispose();
        }
        catch (Exception)
        {
            // Releasing a broken WinRT session manager can throw; nothing to do.
        }
    }
}
