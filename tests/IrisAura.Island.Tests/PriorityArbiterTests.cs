using FluentAssertions;
using IrisAura.Island.Engine;

namespace IrisAura.Island.Tests;

/// <summary>PriorityArbiter rules 1–5 (BLUEPRINT §8.5).</summary>
public sealed class PriorityArbiterTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityManager _manager;

    public PriorityArbiterTests() => _manager = new ActivityManager(_time);

    private ActivityLayout Arrange(bool quiet = false, UserPreference? preference = null) =>
        PriorityArbiter.Arrange(new ArbiterInput(_manager.Activities, _time.GetUtcNow(), quiet, preference));

    [Fact]
    public void Empty_engine_is_idle() => Arrange().Should().BeEquivalentTo(ActivityLayout.Empty);

    [Fact]
    public void Highest_priority_is_main_second_is_bubble_rest_are_queued()
    {
        _manager.Start("p", Specs.Persistent("download", ActivityKind.Download));
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media));
        _manager.Start("p", Specs.Persistent("call", ActivityKind.Call));
        _manager.Start("p", Specs.Persistent("timer", ActivityKind.Timer));

        var layout = Arrange();

        layout.Main!.Id.Should().Be("call");
        layout.Bubble!.Id.Should().Be("timer");
        layout.Queue.Select(a => a.Id).Should().Equal("media", "download");
        layout.PersistentInOrder.Select(a => a.Id).Should().Equal("call", "timer", "media", "download");
    }

    [Fact]
    public void Priority_ties_go_to_the_activity_that_started_first_then_id()
    {
        _manager.Start("p", Specs.Persistent("b", priority: 40));
        _time.Advance(TimeSpan.FromSeconds(1));
        _manager.Start("p", Specs.Persistent("a", priority: 40));
        _manager.Start("p", Specs.Persistent("c", priority: 40));

        var layout = Arrange();
        layout.Main!.Id.Should().Be("b", "started first; ties never flap");
        layout.Bubble!.Id.Should().Be("a", "same start time: ordinal id");
        layout.Queue.Single().Id.Should().Be("c");
    }

    [Fact]
    public void Updating_an_activity_does_not_reorder_ties()
    {
        _manager.Start("p", Specs.Persistent("first", priority: 40));
        _time.Advance(TimeSpan.FromSeconds(1));
        _manager.Start("p", Specs.Persistent("second", priority: 40));
        _time.Advance(TimeSpan.FromSeconds(1));
        _manager.Update("p", new ActivityUpdate("second") { Compact = new CompactContent(TrailingText: "busy") });

        Arrange().Main!.Id.Should().Be("first");
    }

    [Fact]
    public void Transient_takes_the_pill_and_the_previous_layout_returns_when_it_ends()
    {
        _manager.Start("p", Specs.Persistent("timer", ActivityKind.Timer));
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media));
        var before = Arrange();

        _manager.Start("p", Specs.Transient("note"));
        var during = Arrange();
        during.Transient!.Id.Should().Be("note");
        during.Main!.Id.Should().Be(before.Main!.Id, "the persistent layout is kept underneath");
        during.Bubble!.Id.Should().Be(before.Bubble!.Id);

        _time.Advance(TimeSpan.FromSeconds(4));
        _manager.ExpireDue();
        var after = Arrange();
        after.Transient.Should().BeNull();
        after.Main!.Id.Should().Be("timer");
        after.Bubble!.Id.Should().Be("media");
    }

    [Fact]
    public void Low_priority_transient_still_interrupts_a_high_priority_persistent_activity()
    {
        _manager.Start("p", Specs.Persistent("call", ActivityKind.Call));
        _manager.Start("p", Specs.Transient("bt", ActivityKind.Bluetooth));

        var layout = Arrange();
        layout.Transient!.Id.Should().Be("bt");
        layout.Main!.Id.Should().Be("call");
    }

    [Fact]
    public void Several_transients_show_highest_priority_then_newest_and_the_rest_wait()
    {
        _manager.Start("p", Specs.Transient("volume", ActivityKind.Volume));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _manager.Start("p", Specs.Transient("note", ActivityKind.Notification));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _manager.Start("p", Specs.Transient("note2", ActivityKind.Notification));

        var layout = Arrange();
        layout.Transient!.Id.Should().Be("note2", "same priority (80): newest wins");
        layout.WaitingTransients.Select(a => a.Id).Should().Equal("note", "volume");
    }

    [Fact]
    public void Quiet_mode_suppresses_notification_banners_only()
    {
        _manager.Start("p", Specs.Transient("note", ActivityKind.Notification));
        _manager.Start("p", Specs.Transient("charging", ActivityKind.Charging));
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media));

        var layout = Arrange(quiet: true);

        layout.Transient!.Id.Should().Be("charging");
        layout.Suppressed.Select(a => a.Id).Should().Equal("note");
        layout.Main!.Id.Should().Be("media");
    }

    [Fact]
    public void Paused_media_keeps_its_place_for_30_seconds_then_drops_to_the_queue()
    {
        _manager.Start("p", Specs.Persistent("download", ActivityKind.Download));
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media) with { IsPaused = true });
        var pausedAt = _time.GetUtcNow();

        var early = Arrange();
        early.Main!.Id.Should().Be("media");
        early.NextChangeAt.Should().Be(pausedAt + PriorityArbiter.PausedMediaGrace);

        _time.Advance(PriorityArbiter.PausedMediaGrace);
        var late = Arrange();
        late.Main!.Id.Should().Be("download");
        late.Bubble.Should().BeNull("paused media is in the queue, not the bubble");
        late.Queue.Select(a => a.Id).Should().Equal("media");

        _manager.Update("p", new ActivityUpdate("media") { IsPaused = false });
        Arrange().Main!.Id.Should().Be("media", "resuming playback restores it");
    }

    [Fact]
    public void User_preference_puts_the_chosen_activity_first()
    {
        _manager.Start("p", Specs.Persistent("timer", ActivityKind.Timer));
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media));
        _manager.Start("p", Specs.Persistent("download", ActivityKind.Download));

        var swapped = Arrange(preference: new UserPreference("media", "timer", 50));
        swapped.Main!.Id.Should().Be("media");
        swapped.Bubble!.Id.Should().Be("timer");

        var cycled = Arrange(preference: new UserPreference("download", null, 40));
        cycled.Main!.Id.Should().Be("download");
        cycled.Bubble!.Id.Should().Be("timer");

        Arrange(preference: new UserPreference("gone", null, 10)).Main!.Id.Should().Be("timer", "stale preference is ignored");
    }

    [Fact]
    public void Privacy_indicators_never_take_the_pill()
    {
        _manager.Start("p", Specs.Persistent("mic", ActivityKind.PrivacyIndicator, priority: 100));
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media));

        var layout = Arrange();
        layout.Main!.Id.Should().Be("media");
        layout.PrivacyIndicators.Select(a => a.Id).Should().Equal("mic");
    }

    [Fact]
    public void Next_change_is_the_earliest_expiry_or_pause_boundary()
    {
        _manager.Start("p", Specs.Transient("vol", ActivityKind.Volume));                   // +1.5 s
        _manager.Start("p", Specs.Persistent("sdk", ttl: TimeSpan.FromSeconds(60)));       // +60 s

        Arrange().NextChangeAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void Activity_is_hidden_while_its_own_app_is_in_front_and_returns_when_it_is_not()
    {
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media) with { SourceAppId = "Spotify.exe" });
        _manager.Start("p", Specs.Persistent("timer", ActivityKind.Timer) with { SourceAppId = "debug" });

        var inFront = PriorityArbiter.Arrange(new ArbiterInput(_manager.Activities, _time.GetUtcNow(), Foreground: new ForegroundApp("Spotify", null)));
        inFront.Main!.Id.Should().Be("timer");
        inFront.Bubble.Should().BeNull();
        inFront.HiddenBecauseAppInFront.Select(a => a.Id).Should().Equal("media");

        var minimised = PriorityArbiter.Arrange(new ArbiterInput(_manager.Activities, _time.GetUtcNow(), Foreground: new ForegroundApp("Code", null)));
        minimised.Main!.Id.Should().Be("timer");
        minimised.Bubble!.Id.Should().Be("media", "another app is in front, so the media activity shows again");
        minimised.HiddenBecauseAppInFront.Should().BeEmpty();
    }

    [Fact]
    public void Transients_from_the_app_in_front_still_show()
    {
        _manager.Start("p", Specs.Transient("note", ActivityKind.Notification) with { SourceAppId = "Spotify.exe" });

        var layout = PriorityArbiter.Arrange(new ArbiterInput(_manager.Activities, _time.GetUtcNow(), Foreground: new ForegroundApp("Spotify", null)));
        layout.Transient!.Id.Should().Be("note");
    }

    [Theory]
    [InlineData("chrome", null, "Chrome.VXSLNYZIFHXJXHXRSTL6TR5EKU", true)]
    [InlineData("chrome", null, "Chrome", true)]
    [InlineData("msedge", null, "MSEdge", true)]
    [InlineData("Spotify", null, "Spotify.exe", true)]
    [InlineData("Microsoft.Media.Player", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", true)]
    [InlineData("Contoso", "Contoso.App_1!App", "Contoso.App_1!App", true)]
    [InlineData("chrome", null, "MSEdge", false)]
    [InlineData("Code", null, "Spotify.exe", false)]
    [InlineData("debug", null, "", false)]
    public void Foreground_app_matching(string process, string? aumid, string sourceAppId, bool matches) =>
        new ForegroundApp(process, aumid).Matches(sourceAppId).Should().Be(matches);

    [Fact]
    public void Timer_promoted_to_85_moves_above_media_and_a_notification_banner_still_wins()
    {
        _manager.Start("p", Specs.Persistent("timer", ActivityKind.Timer));
        _manager.Start("p", Specs.Persistent("call", ActivityKind.Call));
        Arrange().Main!.Id.Should().Be("call");

        _manager.Update("p", new ActivityUpdate("timer") { Priority = 85 });
        Arrange().Main!.Id.Should().Be("call", "85 < call 90");
        _manager.End("p", "call");
        Arrange().Main!.Id.Should().Be("timer");
    }
}
