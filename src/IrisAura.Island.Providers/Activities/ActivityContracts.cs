namespace IrisAura.Island.Activities;

// Contracts between activity providers and the Island's activity engine (BLUEPRINT §8.5).
// They live in IrisAura.Island.Providers so providers do not depend on the Island shell; the
// engine (ActivityManager, PriorityArbiter, ActivityProviderHost) lives in IrisAura.Island.

/// <summary>Kinds of activity (BLUEPRINT §8.5).</summary>
public enum ActivityKind
{
    Face,
    Call,
    Timer,
    Media,
    Navigation,
    Download,
    Charging,
    Bluetooth,
    Volume,
    Brightness,
    Notification,
    PrivacyIndicator,
    Custom,
}

/// <summary>Compact (leading + trailing) content. <see cref="LeadingGlyph"/> is a resource key such as "Aura.Glyph.Play".</summary>
public sealed record CompactContent(
    string? LeadingGlyph = null,
    string? LeadingText = null,
    string? TrailingText = null,
    double? TrailingProgress = null);

/// <summary>A button in the expanded view; pressing it is routed back to the owning provider.</summary>
public sealed record ActivityButton(string Id, string Label);

/// <summary>Expanded content for activities without a custom view.</summary>
public sealed record ExpandedContent(
    string Title,
    string? Subtitle = null,
    double? Progress = null,
    IReadOnlyList<ActivityButton>? Buttons = null);

/// <summary>What a provider asks the engine to show.</summary>
/// <param name="Id">Stable key, unique per provider, e.g. "media".</param>
/// <param name="Kind">Activity kind.</param>
/// <param name="Compact">Compact content.</param>
/// <param name="Expanded">Expanded content, or <see langword="null"/> when not expandable.</param>
/// <param name="Priority">Higher wins the main pill; <see langword="null"/> uses the kind's default (§8.5 table).</param>
/// <param name="IsTransient">Banner/HUD that dismisses itself; <see langword="null"/> uses the kind's default.</param>
/// <param name="Ttl">Lifetime; for transients refreshed by every update. <see langword="null"/> uses the kind's default.</param>
/// <param name="SourceAppId">AUMID or exe name, used to open the app.</param>
/// <param name="AccessibleText">Text for screen readers.</param>
/// <param name="View">Optional rich payload: a view model with its own templates, or a data snapshot the Island maps to one.</param>
/// <param name="IsPaused">Media only: paused media drops to the queue after 30 s.</param>
public sealed record ActivitySpec(
    string Id,
    ActivityKind Kind,
    CompactContent Compact,
    ExpandedContent? Expanded = null,
    int? Priority = null,
    bool? IsTransient = null,
    TimeSpan? Ttl = null,
    string SourceAppId = "",
    string? AccessibleText = null,
    object? View = null,
    bool IsPaused = false);

/// <summary>Partial update: only non-null members change.</summary>
public sealed record ActivityUpdate(string Id)
{
    public CompactContent? Compact { get; init; }

    public ExpandedContent? Expanded { get; init; }

    public int? Priority { get; init; }

    public TimeSpan? Ttl { get; init; }

    public bool? IsPaused { get; init; }

    public string? AccessibleText { get; init; }

    /// <summary>Monotonic sequence from the provider; an update with a sequence ≤ the last applied one is stale. 0 = unsequenced.</summary>
    public long Sequence { get; init; }

    /// <summary>Merges a newer update into this one (newer non-null members win).</summary>
    public ActivityUpdate MergeWith(ActivityUpdate newer) => this with
    {
        Compact = newer.Compact ?? Compact,
        Expanded = newer.Expanded ?? Expanded,
        Priority = newer.Priority ?? Priority,
        Ttl = newer.Ttl ?? Ttl,
        IsPaused = newer.IsPaused ?? IsPaused,
        AccessibleText = newer.AccessibleText ?? AccessibleText,
        Sequence = Math.Max(Sequence, newer.Sequence),
    };
}

/// <summary>Defaults from the BLUEPRINT §8.5 priority table.</summary>
public static class ActivityDefaults
{
    /// <summary>SDK/custom activities are capped at this priority (§8.6.14).</summary>
    public const int CustomMaxPriority = 60;

    public const int CustomMinPriority = 10;

    public static int Priority(ActivityKind kind) => kind switch
    {
        ActivityKind.Face => 100,
        ActivityKind.Call => 90,
        ActivityKind.Notification => 80,
        ActivityKind.Volume or ActivityKind.Brightness => 75,
        ActivityKind.Charging => 70,
        ActivityKind.Bluetooth => 65,
        ActivityKind.Timer => 60,
        ActivityKind.Navigation => 55,
        ActivityKind.Media => 50,
        ActivityKind.Download => 40,
        ActivityKind.Custom => 30,
        _ => 0,
    };

    public static bool IsTransient(ActivityKind kind) => kind is ActivityKind.Face or ActivityKind.Notification
        or ActivityKind.Volume or ActivityKind.Brightness or ActivityKind.Charging or ActivityKind.Bluetooth;

    public static TimeSpan? Ttl(ActivityKind kind) => kind switch
    {
        ActivityKind.Face => TimeSpan.FromSeconds(2),
        ActivityKind.Notification => TimeSpan.FromSeconds(4),
        ActivityKind.Volume or ActivityKind.Brightness => TimeSpan.FromSeconds(1.5),
        ActivityKind.Charging => TimeSpan.FromSeconds(2.5),
        ActivityKind.Bluetooth => TimeSpan.FromSeconds(3),
        _ => null,
    };

    /// <summary>Clamps a priority: custom activities to 10–60, others to 0–100.</summary>
    public static int ClampPriority(ActivityKind kind, int priority) => kind == ActivityKind.Custom
        ? Math.Clamp(priority, CustomMinPriority, CustomMaxPriority)
        : Math.Clamp(priority, 0, 100);
}

/// <summary>Where a provider sends its activities. Safe to call from any thread.</summary>
public interface IActivitySink
{
    void Start(ActivitySpec spec);

    void Update(ActivityUpdate update);

    void End(string id, string? finalText = null, TimeSpan? dismissAfter = null);

    /// <summary>Reports an unrecoverable provider error; the host disables the provider and removes its activities.</summary>
    void ReportFailure(Exception error);
}

/// <summary>One source of activities (media, power, Bluetooth, SDK, …; BLUEPRINT §8.6).</summary>
public interface IActivityProvider
{
    /// <summary>Stable id, also the owner id of its activities.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Starts producing activities into <paramref name="sink"/>. May throw; the host isolates it.</summary>
    void Start(IActivitySink sink);

    /// <summary>Stops and releases resources. May throw; the host isolates it.</summary>
    void Stop();

    /// <summary>A button or gesture on one of this provider's activities.</summary>
    /// <returns><see langword="true"/> if the provider handled the action.</returns>
    bool OnAction(string activityId, string actionId);
}

/// <summary>Well-known action ids sent to providers.</summary>
public static class ActivityActions
{
    /// <summary>Middle click (media: play/pause).</summary>
    public const string Primary = "primary";

    /// <summary>Click on the compact pill: bring the source app to the front.</summary>
    public const string Open = "open";
}
