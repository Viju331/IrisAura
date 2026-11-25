using FluentAssertions;
using IrisAura.Island.Engine;

namespace IrisAura.Island.Tests;

/// <summary>Provider isolation, marshalling and coalescing (BLUEPRINT §8.4, §8.9, §10 Phase 2 "provider failures").</summary>
public sealed class ProviderHostTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityManager _manager;

    public ProviderHostTests() => _manager = new ActivityManager(_time);

    [Fact]
    public void Provider_that_throws_on_start_is_faulted_and_others_keep_running()
    {
        var host = new ActivityProviderHost(_manager, null);
        var good = new TestProvider("good");
        var bad = new TestProvider("bad") { ThrowOnStart = true };
        host.Register(good);
        host.Register(bad);

        host.StartAll();
        good.Sink!.Start(Specs.Persistent("g"));

        host.Status("bad")!.State.Should().Be(ProviderState.Faulted);
        host.Status("bad")!.LastError.Should().Contain("InvalidOperationException");
        host.Status("good")!.State.Should().Be(ProviderState.Running);
        _manager.Find("g").Should().NotBeNull();
    }

    [Fact]
    public void Reported_failure_disables_the_provider_and_removes_only_its_activities()
    {
        var host = new ActivityProviderHost(_manager, null);
        var a = new TestProvider("a");
        var b = new TestProvider("b");
        host.Register(a);
        host.Register(b);
        host.StartAll();
        a.Sink!.Start(Specs.Persistent("a1"));
        a.Sink.Start(Specs.Transient("a2"));
        b.Sink!.Start(Specs.Persistent("b1"));

        a.Sink.ReportFailure(new TimeoutException("device vanished"));

        host.Status("a")!.State.Should().Be(ProviderState.Faulted);
        a.Stopped.Should().BeTrue("the host stops a faulted provider");
        _manager.Activities.Select(x => x.Id).Should().Equal("b1");
    }

    [Fact]
    public void Calls_from_a_faulted_provider_are_ignored()
    {
        var host = new ActivityProviderHost(_manager, null);
        var p = new TestProvider("p");
        host.Register(p);
        host.StartAll();
        var oldSink = p.Sink!;
        oldSink.ReportFailure(new InvalidOperationException("boom"));

        oldSink.Start(Specs.Persistent("late"));

        _manager.Count.Should().Be(0);
    }

    [Fact]
    public void Exception_in_an_action_handler_faults_only_that_provider()
    {
        var host = new ActivityProviderHost(_manager, null);
        var p = new TestProvider("p") { ThrowOnAction = true };
        var other = new TestProvider("other");
        host.Register(p);
        host.Register(other);
        host.StartAll();
        p.Sink!.Start(Specs.Persistent("x"));
        other.Sink!.Start(Specs.Persistent("y"));

        host.DispatchAction(_manager.Find("x")!, "cancel");

        host.Status("p")!.State.Should().Be(ProviderState.Faulted);
        host.Status("other")!.State.Should().Be(ProviderState.Running);
        _manager.Activities.Select(a => a.Id).Should().Equal("y");
    }

    [Fact]
    public void Actions_are_routed_to_the_owning_provider()
    {
        var host = new ActivityProviderHost(_manager, null);
        var p = new TestProvider("p");
        var q = new TestProvider("q");
        host.Register(p);
        host.Register(q);
        host.StartAll();
        q.Sink!.Start(Specs.Persistent("q1"));

        host.DispatchAction(_manager.Find("q1")!, "primary");

        q.Actions.Should().Equal(("q1", "primary"));
        p.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Faulted_provider_can_be_restarted()
    {
        var host = new ActivityProviderHost(_manager, null);
        var p = new TestProvider("p");
        host.Register(p);
        host.StartAll();
        p.Sink!.ReportFailure(new InvalidOperationException("boom"));

        host.Start("p");
        p.Sink!.Start(Specs.Persistent("again"));

        host.Status("p")!.State.Should().Be(ProviderState.Running);
        host.Status("p")!.LastError.Should().BeNull();
        _manager.Find("again").Should().NotBeNull();
    }

    [Fact]
    public void Stopping_a_provider_removes_its_activities()
    {
        var host = new ActivityProviderHost(_manager, null);
        var p = new TestProvider("p");
        host.Register(p);
        host.StartAll();
        p.Sink!.Start(Specs.Persistent("x"));

        host.Stop("p");

        host.Status("p")!.State.Should().Be(ProviderState.Stopped);
        _manager.Count.Should().Be(0);
    }

    [Fact]
    public void Sink_marshals_to_the_engine_context_and_coalesces_consecutive_updates()
    {
        var context = new ManualSynchronizationContext();
        var host = new ActivityProviderHost(_manager, context);
        var p = new TestProvider("p");
        host.Register(p);
        host.StartAll();
        var updates = 0;
        _manager.Changed += (_, e) => updates += e.Change == ActivityChange.Updated ? 1 : 0;

        p.Sink!.Start(Specs.Persistent("dl"));
        for (var i = 1; i <= 50; i++)
        {
            p.Sink.Update(new ActivityUpdate("dl") { Compact = new CompactContent(TrailingProgress: i / 50.0) });
        }

        p.Sink.Update(new ActivityUpdate("dl") { Priority = 45 });

        _manager.Count.Should().Be(0, "nothing applies until the engine context runs");
        context.Pending.Should().Be(1, "one post per burst");

        context.RunAll();

        updates.Should().Be(1, "51 consecutive updates were merged into one");
        var dl = _manager.Find("dl")!;
        dl.Compact.TrailingProgress.Should().Be(1.0);
        dl.Priority.Should().Be(45, "fields from different updates merge");
    }

    [Fact]
    public void Coalescing_preserves_order_across_start_update_end()
    {
        var context = new ManualSynchronizationContext();
        var host = new ActivityProviderHost(_manager, context);
        var p = new TestProvider("p");
        host.Register(p);
        host.StartAll();
        var events = new List<(ActivityChange, string)>();
        _manager.Changed += (_, e) => events.Add((e.Change, e.Activity.Id));

        p.Sink!.Start(Specs.Persistent("a"));
        p.Sink.Update(new ActivityUpdate("a") { Priority = 30 });
        p.Sink.End("a");
        p.Sink.Start(Specs.Persistent("a"));

        context.RunAll();

        events.Should().Equal((ActivityChange.Started, "a"), (ActivityChange.Updated, "a"), (ActivityChange.Ended, "a"), (ActivityChange.Started, "a"));
    }

    [Fact]
    public void Sink_is_safe_to_call_from_many_threads()
    {
        var context = new ManualSynchronizationContext();
        var host = new ActivityProviderHost(_manager, context);
        var p = new TestProvider("p");
        host.Register(p);
        host.StartAll();

        Parallel.For(0, 20, i => p.Sink!.Start(Specs.Persistent($"a{i}")));
        context.RunAll();

        _manager.Count.Should().Be(20);
    }

    [Fact]
    public void Registering_the_same_provider_id_twice_throws()
    {
        var host = new ActivityProviderHost(_manager, null);
        host.Register(new TestProvider("p"));
        var act = () => host.Register(new TestProvider("p"));
        act.Should().Throw<InvalidOperationException>();
    }

    // ---------------------------------------------------------------- Phase 4: enable/disable, unavailable

    [Fact]
    public void Disabled_provider_is_not_started_until_enabled()
    {
        var host = new ActivityProviderHost(_manager, null);
        var calls = new TestProvider("calls");
        host.Register(calls, enabled: false);

        host.StartAll();
        host.Status("calls")!.State.Should().Be(ProviderState.Registered);
        host.Status("calls")!.IsEnabled.Should().BeFalse();
        calls.Sink.Should().BeNull();

        host.SetEnabled("calls", true);
        host.Status("calls")!.State.Should().Be(ProviderState.Running);
        host.Status("calls")!.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Disabling_a_provider_stops_it_and_removes_its_activities()
    {
        var host = new ActivityProviderHost(_manager, null);
        var volume = new TestProvider("volume");
        var other = new TestProvider("other");
        host.Register(volume);
        host.Register(other);
        host.StartAll();
        volume.Sink!.Start(Specs.Persistent("v"));
        other.Sink!.Start(Specs.Persistent("o"));

        host.SetEnabled("volume", false);

        volume.Stopped.Should().BeTrue();
        host.Status("volume")!.State.Should().Be(ProviderState.Stopped);
        _manager.Find("v").Should().BeNull();
        _manager.Find("o").Should().NotBeNull();
        host.StartAll();
        host.Status("volume")!.State.Should().Be(ProviderState.Stopped, "StartAll skips providers the user switched off");
    }

    [Fact]
    public void Unavailable_provider_is_stopped_quietly_and_can_be_retried()
    {
        var host = new ActivityProviderHost(_manager, null);
        var power = new TestProvider("power");
        host.Register(power);
        host.StartAll();
        power.Sink!.Start(Specs.Persistent("p"));

        power.Sink.ReportUnavailable("no battery");

        host.Status("power")!.State.Should().Be(ProviderState.Unavailable);
        host.Status("power")!.LastError.Should().Be("no battery");
        host.Status("power")!.IsEnabled.Should().BeTrue("unavailable is not the user switching it off");
        power.Stopped.Should().BeTrue();
        _manager.Find("p").Should().BeNull();

        host.SetEnabled("power", true); // retry (e.g. a battery or radio appeared)
        host.Status("power")!.State.Should().Be(ProviderState.Running);
    }

    [Fact]
    public void Unavailable_report_from_an_earlier_run_does_not_affect_a_restarted_provider()
    {
        var context = new ManualSynchronizationContext();
        var host = new ActivityProviderHost(_manager, context);
        var bt = new TestProvider("bluetooth");
        host.Register(bt);
        host.StartAll();
        var oldSink = bt.Sink!;
        host.Stop("bluetooth");
        host.Start("bluetooth");

        oldSink.ReportUnavailable("late report");
        context.RunAll();

        host.Status("bluetooth")!.State.Should().Be(ProviderState.Running);
    }

    private sealed class TestProvider(string id) : IActivityProvider
    {
        public string Id => id;

        public string DisplayName => id;

        public bool ThrowOnStart { get; init; }

        public bool ThrowOnAction { get; init; }

        public IActivitySink? Sink { get; private set; }

        public bool Stopped { get; private set; }

        public List<(string Activity, string Action)> Actions { get; } = [];

        public void Start(IActivitySink sink)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("provider bug");
            }

            Sink = sink;
            Stopped = false;
        }

        public void Stop() => Stopped = true;

        public bool OnAction(string activityId, string actionId)
        {
            if (ThrowOnAction)
            {
                throw new InvalidOperationException("action bug");
            }

            Actions.Add((activityId, actionId));
            return true;
        }
    }
}
