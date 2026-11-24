using FluentAssertions;
using IrisAura.Island.Engine;

namespace IrisAura.Island.Tests;

/// <summary>Activity lifecycle: start/update/end, duplicates, stale updates, expiry, cancellation (BLUEPRINT §8.5, §10 Phase 2).</summary>
public sealed class ActivityManagerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityManager _manager;
    private readonly List<(ActivityChange Change, string Id)> _events = [];

    public ActivityManagerTests()
    {
        _manager = new ActivityManager(_time);
        _manager.Changed += (_, e) => _events.Add((e.Change, e.Activity.Id));
    }

    [Fact]
    public void Start_applies_blueprint_defaults_for_the_kind()
    {
        _manager.Start("p", Specs.Persistent("media", ActivityKind.Media)).Should().Be(ActivityResult.Started);
        _manager.Start("p", Specs.Transient("vol", ActivityKind.Volume)).Should().Be(ActivityResult.Started);

        var media = _manager.Find("media")!;
        media.Priority.Should().Be(50);
        media.IsTransient.Should().BeFalse();
        media.ExpiresAt.Should().BeNull();

        var volume = _manager.Find("vol")!;
        volume.Priority.Should().Be(75);
        volume.IsTransient.Should().BeTrue();
        volume.ExpiresAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromSeconds(1.5));
    }

    [Theory]
    [InlineData(ActivityKind.Face, 100)]
    [InlineData(ActivityKind.Call, 90)]
    [InlineData(ActivityKind.Notification, 80)]
    [InlineData(ActivityKind.Brightness, 75)]
    [InlineData(ActivityKind.Charging, 70)]
    [InlineData(ActivityKind.Bluetooth, 65)]
    [InlineData(ActivityKind.Timer, 60)]
    [InlineData(ActivityKind.Media, 50)]
    [InlineData(ActivityKind.Download, 40)]
    public void Default_priorities_match_section_8_5_table(ActivityKind kind, int priority) =>
        ActivityDefaults.Priority(kind).Should().Be(priority);

    [Theory]
    [InlineData(99, 60)]
    [InlineData(1, 10)]
    [InlineData(45, 45)]
    public void Custom_sdk_priority_is_capped_at_60(int requested, int expected)
    {
        _manager.Start("sdk", Specs.Persistent("c", ActivityKind.Custom, requested));
        _manager.Find("c")!.Priority.Should().Be(expected);

        _manager.Update("sdk", new ActivityUpdate("c") { Priority = 100 });
        _manager.Find("c")!.Priority.Should().Be(60, "updates are capped too");
    }

    [Fact]
    public void Duplicate_id_from_the_same_provider_replaces_content_and_keeps_start_time()
    {
        _manager.Start("p", Specs.Persistent("a"));
        var started = _manager.Find("a")!.StartedAt;
        _time.Advance(TimeSpan.FromSeconds(5));

        _manager.Start("p", Specs.Persistent("a") with { Compact = new CompactContent(TrailingText: "v2") })
            .Should().Be(ActivityResult.Updated);

        _manager.Count.Should().Be(1);
        _manager.Find("a")!.Compact.TrailingText.Should().Be("v2");
        _manager.Find("a")!.StartedAt.Should().Be(started);
        _events.Should().Equal((ActivityChange.Started, "a"), (ActivityChange.Updated, "a"));
    }

    [Fact]
    public void Duplicate_id_from_another_provider_is_rejected()
    {
        _manager.Start("p1", Specs.Persistent("a"));

        _manager.Start("p2", Specs.Persistent("a")).Should().Be(ActivityResult.IdConflict);
        _manager.Update("p2", new ActivityUpdate("a") { Priority = 99 }).Should().Be(ActivityResult.IdConflict);
        _manager.End("p2", "a").Should().Be(ActivityResult.IdConflict);

        _manager.Find("a")!.ProviderId.Should().Be("p1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nid")]
    public void Invalid_ids_are_rejected(string id) =>
        _manager.Start("p", Specs.Persistent(id)).Should().Be(ActivityResult.Invalid);

    [Fact]
    public void Over_long_id_and_negative_ttl_are_rejected()
    {
        _manager.Start("p", Specs.Persistent(new string('x', ActivityManager.MaxIdLength + 1))).Should().Be(ActivityResult.Invalid);
        _manager.Start("p", Specs.Persistent("a", ttl: TimeSpan.FromSeconds(-1))).Should().Be(ActivityResult.Invalid);
        _manager.Count.Should().Be(0);
    }

    [Fact]
    public void Partial_update_changes_only_given_members()
    {
        _manager.Start("p", Specs.Persistent("a", priority: 30));
        _manager.Update("p", new ActivityUpdate("a") { Compact = new CompactContent(TrailingText: "80%") })
            .Should().Be(ActivityResult.Updated);

        var a = _manager.Find("a")!;
        a.Compact.TrailingText.Should().Be("80%");
        a.Priority.Should().Be(30);
        a.Expanded!.Title.Should().Be("a");
    }

    [Fact]
    public void Stale_sequenced_updates_are_ignored()
    {
        _manager.Start("p", Specs.Persistent("a"));
        _manager.Update("p", new ActivityUpdate("a") { Sequence = 5, Compact = new CompactContent(TrailingText: "5") });

        _manager.Update("p", new ActivityUpdate("a") { Sequence = 4, Compact = new CompactContent(TrailingText: "4") })
            .Should().Be(ActivityResult.Stale);
        _manager.Update("p", new ActivityUpdate("a") { Sequence = 5, Compact = new CompactContent(TrailingText: "5b") })
            .Should().Be(ActivityResult.Stale);

        _manager.Find("a")!.Compact.TrailingText.Should().Be("5");

        _manager.Update("p", new ActivityUpdate("a") { Sequence = 6, Compact = new CompactContent(TrailingText: "6") })
            .Should().Be(ActivityResult.Updated);
    }

    [Fact]
    public void Updates_after_end_are_ignored_and_do_not_resurrect()
    {
        _manager.Start("p", Specs.Persistent("a"));
        _manager.End("p", "a").Should().Be(ActivityResult.Ended);

        _manager.Update("p", new ActivityUpdate("a") { Priority = 40 }).Should().Be(ActivityResult.NotFound);
        _manager.End("p", "a").Should().Be(ActivityResult.NotFound);
        _manager.Count.Should().Be(0);
    }

    [Fact]
    public void Transient_expires_ttl_after_its_last_update()
    {
        _manager.Start("p", Specs.Transient("vol", ActivityKind.Volume));
        _time.Advance(TimeSpan.FromSeconds(1));
        _manager.Update("p", new ActivityUpdate("vol") { Compact = new CompactContent(TrailingProgress: 0.6) });
        _time.Advance(TimeSpan.FromSeconds(1));

        _manager.ExpireDue().Should().Be(0, "the update refreshed the 1.5 s HUD lifetime");
        _time.Advance(TimeSpan.FromSeconds(0.6));
        _manager.ExpireDue().Should().Be(1);
        _events.Should().EndWith((ActivityChange.Ended, "vol"));
    }

    [Fact]
    public void Persistent_ttl_counts_from_start_and_updates_do_not_extend_it()
    {
        _manager.Start("p", Specs.Persistent("sdk", ttl: TimeSpan.FromSeconds(10)));
        _time.Advance(TimeSpan.FromSeconds(8));
        _manager.Update("p", new ActivityUpdate("sdk") { Compact = new CompactContent(TrailingText: "x") });
        _time.Advance(TimeSpan.FromSeconds(2));

        _manager.ExpireDue().Should().Be(1);
        _manager.NextExpiry.Should().BeNull();
    }

    [Fact]
    public void End_with_final_text_shows_it_then_expires_and_ignores_further_updates()
    {
        _manager.Start("p", Specs.Persistent("build"));
        _manager.End("p", "build", "Deployed ✓", TimeSpan.FromSeconds(3)).Should().Be(ActivityResult.Updated);

        var ending = _manager.Find("build")!;
        ending.IsEnding.Should().BeTrue();
        ending.Compact.TrailingText.Should().Be("Deployed ✓");
        ending.Expanded!.Subtitle.Should().Be("Deployed ✓");

        _manager.Update("p", new ActivityUpdate("build") { Compact = new CompactContent(TrailingText: "late") })
            .Should().Be(ActivityResult.Stale);

        _time.Advance(TimeSpan.FromSeconds(3));
        _manager.ExpireDue().Should().Be(1);
        _manager.Find("build").Should().BeNull();
    }

    [Fact]
    public void Cancelling_a_provider_ends_only_its_activities()
    {
        _manager.Start("p1", Specs.Persistent("a"));
        _manager.Start("p1", Specs.Transient("b"));
        _manager.Start("p2", Specs.Persistent("c"));

        _manager.EndAllFrom("p1").Should().Be(2);

        _manager.Activities.Select(a => a.Id).Should().Equal("c");
    }

    [Fact]
    public void Pause_state_is_tracked_from_the_first_pause()
    {
        _manager.Start("p", Specs.Persistent("m", ActivityKind.Media) with { IsPaused = true });
        var pausedAt = _manager.Find("m")!.PausedSince;
        pausedAt.Should().Be(_time.GetUtcNow());

        _time.Advance(TimeSpan.FromSeconds(5));
        _manager.Update("p", new ActivityUpdate("m") { IsPaused = true });
        _manager.Find("m")!.PausedSince.Should().Be(pausedAt, "repeated pause reports keep the original time");

        _manager.Update("p", new ActivityUpdate("m") { IsPaused = false });
        _manager.Find("m")!.PausedSince.Should().BeNull();
    }
}
