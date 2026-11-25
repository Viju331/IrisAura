using FluentAssertions;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Media;

namespace IrisAura.Island.Tests;

/// <summary>Media logic (BLUEPRINT §8.6.1–§8.6.2): timeline, selection, tint, app identity, waveform, provider.</summary>
public sealed class MediaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- timeline interpolation

    private static MediaTimelineSnapshot Timeline(double positionSeconds, double endSeconds = 200, DateTimeOffset? updated = null) =>
        new(TimeSpan.Zero, TimeSpan.FromSeconds(endSeconds), TimeSpan.FromSeconds(positionSeconds), updated ?? T0, TimeSpan.Zero, TimeSpan.FromSeconds(endSeconds));

    [Fact]
    public void Position_advances_with_real_time_while_playing()
    {
        var t = Timeline(72);
        MediaTimeline.CurrentPosition(t, T0.AddSeconds(5.5), playing: true, 1.0).Should().Be(TimeSpan.FromSeconds(77.5));
    }

    [Fact]
    public void Position_respects_playback_rate_and_stays_still_while_paused()
    {
        var t = Timeline(10);
        MediaTimeline.CurrentPosition(t, T0.AddSeconds(4), true, 2.0).Should().Be(TimeSpan.FromSeconds(18));
        MediaTimeline.CurrentPosition(t, T0.AddSeconds(4), false, 1.0).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Position_is_clamped_to_the_end_and_ignores_clock_skew()
    {
        MediaTimeline.CurrentPosition(Timeline(195), T0.AddSeconds(30), true, 1).Should().Be(TimeSpan.FromSeconds(200));
        MediaTimeline.CurrentPosition(Timeline(50), T0.AddSeconds(-3), true, 1).Should().Be(TimeSpan.FromSeconds(50), "LastUpdated in the future");
    }

    [Fact]
    public void Missing_last_updated_time_is_not_extrapolated()
    {
        var t = Timeline(40, updated: default(DateTimeOffset));
        MediaTimeline.CurrentPosition(t, T0, true, 1).Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void No_duration_means_no_progress_bar()
    {
        var t = Timeline(0, endSeconds: 0);
        t.HasDuration.Should().BeFalse();
        MediaTimeline.Progress(t, TimeSpan.Zero).Should().BeNull();
        MediaTimeline.Progress(Timeline(50), TimeSpan.FromSeconds(50)).Should().Be(0.25);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(72, "1:12")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Times_are_formatted_like_a_player(int seconds, string expected) =>
        MediaTimeline.Format(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    // ---------------------------------------------------------------- session selection

    private static MediaSessionInfo Session(string key, MediaPlaybackStatus status, string? app = null) =>
        new(key, app ?? key, "title", "artist", null, status, 1.0, MediaControls.None, null, null);

    [Fact]
    public void Playing_session_is_preferred_over_paused_current_session()
    {
        var sessions = new[] { Session("a", MediaPlaybackStatus.Paused), Session("b", MediaPlaybackStatus.Playing) };
        MediaSessionSelector.Select(sessions, currentKey: "a", userKey: null)!.Key.Should().Be("b");
    }

    [Fact]
    public void Several_playing_sessions_use_the_systems_current_one()
    {
        var sessions = new[] { Session("a", MediaPlaybackStatus.Playing), Session("b", MediaPlaybackStatus.Playing) };
        MediaSessionSelector.Select(sessions, "b", null)!.Key.Should().Be("b");
        MediaSessionSelector.Select(sessions, "zzz", null)!.Key.Should().Be("a");
    }

    [Fact]
    public void Paused_only_shows_the_current_paused_session()
    {
        var sessions = new[] { Session("a", MediaPlaybackStatus.Paused), Session("b", MediaPlaybackStatus.Paused) };
        MediaSessionSelector.Select(sessions, "b", null)!.Key.Should().Be("b");
    }

    [Fact]
    public void User_choice_from_the_switcher_wins_while_it_exists()
    {
        var sessions = new[] { Session("a", MediaPlaybackStatus.Playing), Session("b", MediaPlaybackStatus.Paused) };
        MediaSessionSelector.Select(sessions, "a", userKey: "b")!.Key.Should().Be("b");
        MediaSessionSelector.Select(sessions, "a", userKey: "gone")!.Key.Should().Be("a");
    }

    [Theory]
    [InlineData(MediaPlaybackStatus.Closed)]
    [InlineData(MediaPlaybackStatus.Opened)]
    [InlineData(MediaPlaybackStatus.Stopped)]
    public void Idle_sessions_are_not_shown(MediaPlaybackStatus status) =>
        MediaSessionSelector.Select([Session("a", status)], "a", null).Should().BeNull();

    // ---------------------------------------------------------------- redundant updates (CPU)

    private static MediaActivityState State(double positionSeconds, DateTimeOffset updated, MediaPlaybackStatus status = MediaPlaybackStatus.Playing, byte[]? art = null) =>
        new(Session("Chrome", status) with { Timeline = Timeline(positionSeconds, updated: updated), Thumbnail = art }, []);

    [Fact]
    public void Position_sync_matching_the_interpolated_position_is_redundant()
    {
        var before = State(10, T0);
        var sync = State(15.2, T0.AddSeconds(5)); // reported 5 s later, ~where interpolation already is
        MediaChange.IsRedundant(before, sync, T0.AddSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void Seek_pause_artwork_and_first_publish_are_not_redundant()
    {
        var before = State(10, T0);
        MediaChange.IsRedundant(before, State(90, T0.AddSeconds(5)), T0.AddSeconds(5)).Should().BeFalse("the user seeked");
        MediaChange.IsRedundant(before, State(10, T0, MediaPlaybackStatus.Paused), T0).Should().BeFalse("paused");
        MediaChange.IsRedundant(before, State(10, T0, art: [1, 2, 3]), T0).Should().BeFalse("artwork arrived");
        MediaChange.IsRedundant(null, before, T0).Should().BeFalse("nothing published yet");
    }

    // ---------------------------------------------------------------- album tint

    private static byte[] Pixels(params (byte R, byte G, byte B, byte A)[] colors) =>
        colors.SelectMany(c => new[] { c.B, c.G, c.R, c.A }).ToArray();

    [Fact]
    public void Tint_averages_colourful_pixels_and_ignores_black_white_and_transparent()
    {
        var pixels = Pixels((200, 40, 40, 255), (0, 0, 0, 255), (255, 255, 255, 255), (10, 200, 10, 0), (180, 60, 60, 255));
        AlbumTint.FromBgra(pixels).Should().Be(((byte)190, (byte)50, (byte)50));
    }

    [Fact]
    public void Tint_is_null_for_black_and_white_art() =>
        AlbumTint.FromBgra(Pixels((0, 0, 0, 255), (250, 250, 250, 255))).Should().BeNull();

    [Fact]
    public void Dark_tint_is_brightened_to_stay_readable_on_black()
    {
        var tint = AlbumTint.FromBgra(Pixels((60, 20, 80, 255)))!.Value;
        (Math.Max(tint.R, Math.Max(tint.G, tint.B)) / 255.0).Should().BeApproximately(AlbumTint.MinBrightness, 0.01);
        tint.B.Should().BeGreaterThan(tint.R).And.BeGreaterThan(tint.G, "hue is kept");
    }

    // ---------------------------------------------------------------- app identity

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Chrome", "chrome")]
    [InlineData("MSEdge", "msedge")]
    [InlineData("308046B0AF4A39CB", "firefox")]
    [InlineData("vlc.exe", "vlc")]
    [InlineData("foobar2000.exe", "foobar2000")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.Media.Player")]
    [InlineData("Chrome.VXSLNYZIFHXJXHXRSTL6TR5EKU", "chrome")] // observed on DEV-01 (Phase 3 media test)
    [InlineData("MSEdge.ABCDEF", "msedge")]
    [InlineData("Brave.XYZ", "brave")]
    public void App_id_maps_to_the_audio_process(string appId, string process) =>
        MediaAppIdentity.ProcessNameFor(appId).Should().Be(process);

    [Theory]
    [InlineData("")]
    [InlineData("Contoso.Player_abc123!App")]
    [InlineData("bad|name")]
    public void Unknown_packaged_or_invalid_ids_have_no_process(string appId) =>
        MediaAppIdentity.ProcessNameFor(appId).Should().BeNull();

    [Theory]
    [InlineData("MSEdge", "Microsoft Edge")]
    [InlineData("Chrome.VXSLNYZIFHXJXHXRSTL6TR5EKU", "Google Chrome")]
    [InlineData("Contoso.Player_abc123!App", "Player")]
    [InlineData("Contoso.Player_abc123!PlayerApp", "PlayerApp")]
    [InlineData("someplayer.exe", "someplayer")]
    public void Fallback_display_names_are_readable(string appId, string name) =>
        MediaAppIdentity.DisplayNameFor(appId).Should().Be(name);

    // ---------------------------------------------------------------- waveform (§8.6.2)

    [Fact]
    public void Waveform_attack_is_faster_than_release()
    {
        var w = new WaveformSmoother(1);
        w.Update(1.0, 0.040, playing: true);
        var afterAttack = w.Levels[0];

        var r = new WaveformSmoother(1);
        for (var i = 0; i < 30; i++)
        {
            r.Update(1.0, 0.033, true);
        }

        var top = r.Levels[0];
        r.Update(0.0, 0.040, true);
        var dropped = top - r.Levels[0];

        (afterAttack - WaveformSmoother.Floor).Should().BeGreaterThan(dropped, "a 40 ms attack moves further in 40 ms than a 180 ms release");
    }

    [Fact]
    public void Paused_waveform_settles_to_a_flat_line()
    {
        var w = new WaveformSmoother();
        for (var i = 0; i < 30; i++)
        {
            w.Update(0.9, 0.033, true);
        }

        w.IsSettled.Should().BeFalse();
        for (var i = 0; i < 60; i++)
        {
            w.Update(0.9, 0.033, playing: false);
        }

        w.IsSettled.Should().BeTrue();
        w.Levels.Should().OnlyContain(l => l == WaveformSmoother.Floor);
    }

    [Fact]
    public void Waveform_levels_stay_in_range_for_any_input()
    {
        var w = new WaveformSmoother();
        foreach (var peak in new[] { -1.0, 0, 0.001, 0.5, 1, 5, double.NaN, double.PositiveInfinity })
        {
            w.Update(peak, 0.033, true);
            w.Levels.Should().OnlyContain(l => l >= WaveformSmoother.Floor - 1e-9 && l <= 1);
        }

        var act = () => new WaveformSmoother(6);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------------------------------------------------------------- provider

    private sealed class FakeSource : IMediaSessionSource
    {
        public event EventHandler? Changed;

        public MediaSnapshot Snapshot { get; set; } = MediaSnapshot.Empty;

        public bool FailStart { get; init; }

        public bool Disposed { get; private set; }

        public List<(string Key, MediaCommand Command, TimeSpan? Position)> Commands { get; } = [];

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public Task StartAsync(CancellationToken cancellationToken) =>
            FailStart ? Task.FromException(new InvalidOperationException("no media manager")) : Task.CompletedTask;

        public Task<MediaSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);

        public Task<bool> ControlAsync(string key, MediaCommand command, TimeSpan? position = null)
        {
            lock (Commands)
            {
                Commands.Add((key, command, position));
            }

            return Task.FromResult(true);
        }

        public void Dispose() => Disposed = true;
    }

    private static (ActivityManager Manager, ActivityProviderHost Host, MediaProvider Provider, FakeSource Source) Start(MediaSnapshot snapshot)
    {
        var manager = new ActivityManager(new ManualTimeProvider());
        var host = new ActivityProviderHost(manager, null);
        var source = new FakeSource { Snapshot = snapshot };
        var provider = new MediaProvider(() => source) { Debounce = TimeSpan.FromMilliseconds(1) };
        host.Register(provider);
        host.StartAll();
        provider.Ready.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the provider refreshes once after connecting");
        return (manager, host, provider, source);
    }

    [Fact]
    public void Provider_publishes_the_selected_session_with_the_others_for_the_switcher()
    {
        var (manager, _, _, _) = Start(new MediaSnapshot(
            [Session("Spotify.exe", MediaPlaybackStatus.Paused), Session("MSEdge", MediaPlaybackStatus.Playing), Session("x", MediaPlaybackStatus.Stopped)],
            "Spotify.exe"));

        var media = manager.Find(MediaProvider.ActivityId)!;
        media.Kind.Should().Be(ActivityKind.Media);
        media.Priority.Should().Be(50);
        media.SourceAppId.Should().Be("MSEdge");
        media.PausedSince.Should().BeNull();
        var state = media.View.Should().BeOfType<MediaActivityState>().Subject;
        state.Session.Key.Should().Be("MSEdge");
        state.Others.Select(o => o.Key).Should().Equal("Spotify.exe");
        media.AccessibleText.Should().Contain("Now playing").And.Contain("Microsoft Edge");
    }

    [Fact]
    public async Task Provider_reports_pause_and_ends_the_activity_when_nothing_plays()
    {
        var (manager, _, provider, source) = Start(new MediaSnapshot([Session("Spotify.exe", MediaPlaybackStatus.Playing)], "Spotify.exe"));

        source.Snapshot = new MediaSnapshot([Session("Spotify.exe", MediaPlaybackStatus.Paused)], "Spotify.exe");
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        manager.Find(MediaProvider.ActivityId)!.PausedSince.Should().NotBeNull("paused media drops to the queue after 30 s");

        source.Snapshot = new MediaSnapshot([Session("Spotify.exe", MediaPlaybackStatus.Closed)], null);
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        manager.Find(MediaProvider.ActivityId).Should().BeNull();
    }

    [Fact]
    public async Task Switcher_choice_is_kept_until_another_session_starts_playing()
    {
        var (manager, host, provider, source) = Start(new MediaSnapshot(
            [Session("a", MediaPlaybackStatus.Playing), Session("b", MediaPlaybackStatus.Paused), Session("c", MediaPlaybackStatus.Paused)], "a"));

        host.DispatchAction(manager.Find(MediaProvider.ActivityId)!, "select:b").Should().BeTrue();
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        manager.Find(MediaProvider.ActivityId)!.SourceAppId.Should().Be("b");

        source.Snapshot = new MediaSnapshot(
            [Session("a", MediaPlaybackStatus.Playing), Session("b", MediaPlaybackStatus.Paused), Session("c", MediaPlaybackStatus.Playing)], "a");
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        manager.Find(MediaProvider.ActivityId)!.SourceAppId.Should().NotBe("b", "c started playing, which overrides the earlier choice");
    }

    [Fact]
    public async Task Transport_actions_reach_the_shown_session()
    {
        var (manager, host, _, source) = Start(new MediaSnapshot([Session("Spotify.exe", MediaPlaybackStatus.Playing)], "Spotify.exe"));
        var media = manager.Find(MediaProvider.ActivityId)!;

        host.DispatchAction(media, ActivityActions.Primary).Should().BeTrue();
        host.DispatchAction(media, "next").Should().BeTrue();
        host.DispatchAction(media, "previous").Should().BeTrue();
        host.DispatchAction(media, "seek:" + TimeSpan.FromSeconds(30).Ticks).Should().BeTrue();
        host.DispatchAction(media, "seek:-5").Should().BeFalse("invalid seek");
        host.DispatchAction(media, ActivityActions.Open).Should().BeFalse("opening the app is the Island's job");

        for (var i = 0; i < 100 && source.Commands.Count < 4; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        lock (source.Commands)
        {
            source.Commands.Select(c => c.Command).Should().BeEquivalentTo(
                [MediaCommand.TogglePlayPause, MediaCommand.Next, MediaCommand.Previous, MediaCommand.Seek]);
            source.Commands.Should().OnlyContain(c => c.Key == "Spotify.exe");
            source.Commands.Single(c => c.Command == MediaCommand.Seek).Position.Should().Be(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Change_events_trigger_a_refresh()
    {
        var (manager, _, _, source) = Start(MediaSnapshot.Empty);
        manager.Find(MediaProvider.ActivityId).Should().BeNull();

        source.Snapshot = new MediaSnapshot([Session("Spotify.exe", MediaPlaybackStatus.Playing)], "Spotify.exe");
        source.Raise();

        for (var i = 0; i < 100 && manager.Find(MediaProvider.ActivityId) is null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        manager.Find(MediaProvider.ActivityId).Should().NotBeNull();
    }

    [Fact]
    public async Task Provider_retries_then_reports_a_fault_after_repeated_failures()
    {
        var manager = new ActivityManager(new ManualTimeProvider());
        var host = new ActivityProviderHost(manager, null);
        var created = 0;
        var provider = new MediaProvider(() =>
        {
            Interlocked.Increment(ref created);
            return new FakeSource { FailStart = true };
        })
        {
            RetryDelays = [TimeSpan.FromMilliseconds(1)],
        };
        host.Register(provider);
        host.StartAll();

        for (var i = 0; i < 250 && host.Status(MediaProvider.ProviderId)!.State != ProviderState.Faulted; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        host.Status(MediaProvider.ProviderId)!.State.Should().Be(ProviderState.Faulted);
        created.Should().Be(MediaProvider.MaxConsecutiveFailures);
    }

    [Fact]
    public void Stopping_the_provider_disposes_the_source()
    {
        var (_, host, _, source) = Start(new MediaSnapshot([Session("a", MediaPlaybackStatus.Playing)], "a"));
        host.Stop(MediaProvider.ProviderId);
        source.Disposed.Should().BeTrue();
    }
}
