namespace IrisAura.Island.Engine;

/// <summary>
/// An activity held by the engine (BLUEPRINT §8.5 <c>IslandActivity</c>) plus engine bookkeeping.
/// Immutable; every change produces a new instance. Provider-facing contracts (ActivitySpec,
/// ActivityUpdate, IActivityProvider…) live in IrisAura.Island.Providers (namespace IrisAura.Island.Activities).
/// </summary>
public sealed record IslandActivity(
    string Id,
    ActivityKind Kind,
    int Priority,
    bool IsTransient,
    TimeSpan? Ttl,
    string SourceAppId,
    CompactContent Compact,
    ExpandedContent? Expanded,
    DateTimeOffset StartedAt)
{
    /// <summary>Provider that owns the activity; only it may update or end it.</summary>
    public required string ProviderId { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the engine removes the activity, or <see langword="null"/> for no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Media: when playback paused, or <see langword="null"/> while playing.</summary>
    public DateTimeOffset? PausedSince { get; init; }

    public long Sequence { get; init; }

    public string? AccessibleText { get; init; }

    public object? View { get; init; }

    /// <summary>Ended with a final message that stays briefly (SDK <c>end</c> with <c>dismissAfterSeconds</c>).</summary>
    public bool IsEnding { get; init; }
}
