namespace IrisAura.Island.Shell;

/// <summary>Something the Island can show (fake in Phase 1; activities from the engine in Phase 2).</summary>
public interface IIslandActivityView
{
    /// <summary>Spoken by screen readers when the activity is shown.</summary>
    string AccessibleDescription { get; }

    /// <summary>Expanded layout, or <see langword="null"/> when the activity cannot expand.</summary>
    ExpandedKind? ExpandedKind { get; }

    /// <summary>Height for a generic expanded layout (120–200).</summary>
    double ExpandedHeight { get; }

    /// <summary>Called when the activity becomes visible or hidden, so it can stop its timers.</summary>
    void SetVisible(bool visible);
}

/// <summary>Base layout requested by whoever drives the Island; hover/pin can add <see cref="IslandVisualState.Expanded"/>.</summary>
public enum IslandBaseState
{
    Idle,
    Compact,
    Split,
    Banner,
}

/// <summary>How an activity is rendered; also the prefix of its DataTemplate key ("Compact.FakeMediaActivity").</summary>
public enum PresentationMode
{
    Compact,
    Expanded,
    Banner,
    Bubble,
}

/// <summary>What the Island should show.</summary>
/// <param name="State">Base layout.</param>
/// <param name="Primary">Activity in the main pill (required unless idle).</param>
/// <param name="Secondary">Activity in the split bubble (split only).</param>
public sealed record IslandPresentation(IslandBaseState State, IIslandActivityView? Primary = null, IIslandActivityView? Secondary = null)
{
    public static IslandPresentation Idle { get; } = new(IslandBaseState.Idle);

    /// <summary>Whether hovering or pinning can expand the current primary activity.</summary>
    public bool CanExpand =>
        State is IslandBaseState.Compact or IslandBaseState.Split && Primary?.ExpandedKind is not null;
}
