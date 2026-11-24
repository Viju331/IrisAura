using IrisAura.Island.Engine;

namespace IrisAura.Island.Tests;

/// <summary>A clock the test controls.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Collects posts and runs them when told (to test cross-thread marshalling and coalescing).</summary>
internal sealed class ManualSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public int Pending => _queue.Count;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    public void RunAll()
    {
        while (_queue.Count > 0)
        {
            var (callback, state) = _queue.Dequeue();
            callback(state);
        }
    }
}

internal static class Specs
{
    public static ActivitySpec Persistent(string id, ActivityKind kind = ActivityKind.Custom, int? priority = null, TimeSpan? ttl = null) =>
        new(id, kind, new CompactContent(TrailingText: id), new ExpandedContent(id), Priority: priority, Ttl: ttl);

    public static ActivitySpec Transient(string id, ActivityKind kind = ActivityKind.Notification, int? priority = null, TimeSpan? ttl = null) =>
        new(id, kind, new CompactContent(LeadingText: id), new ExpandedContent(id), Priority: priority, Ttl: ttl);
}
