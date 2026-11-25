namespace IrisAura.Island.Engine;

/// <summary>
/// The user's choice of main activity (bubble click = swap, mouse wheel = cycle), remembered until
/// <see cref="PreferredMainId"/> or <see cref="PartnerId"/> ends, or a more important activity starts.
/// </summary>
/// <param name="PreferredMainId">Activity the user put in the main pill.</param>
/// <param name="PartnerId">The activity it was swapped with (swap only).</param>
/// <param name="Priority">Priority of the preferred activity when chosen; a new activity above it clears the choice.</param>
public sealed record UserPreference(string PreferredMainId, string? PartnerId, int Priority);

/// <summary>Arbiter input.</summary>
/// <param name="Activities">Live activities.</param>
/// <param name="Now">Current time.</param>
/// <param name="QuietMode">Suppress notification banners.</param>
/// <param name="Preference">User's swap/cycle choice.</param>
/// <param name="Foreground">App in front; its own persistent activities are not shown (null = rule off / no app).</param>
public sealed record ArbiterInput(
    IReadOnlyCollection<IslandActivity> Activities,
    DateTimeOffset Now,
    bool QuietMode = false,
    UserPreference? Preference = null,
    ForegroundApp? Foreground = null);

/// <summary>Result: what goes where.</summary>
/// <param name="Transient">Banner/HUD currently taking over the pill, if any.</param>
/// <param name="Main">Persistent activity in the main pill.</param>
/// <param name="Bubble">Persistent activity in the split bubble.</param>
/// <param name="Queue">Other persistent activities, reachable by cycling.</param>
/// <param name="WaitingTransients">Transients waiting behind <see cref="Transient"/>.</param>
/// <param name="Suppressed">Notifications hidden by quiet mode.</param>
/// <param name="PrivacyIndicators">Mic/camera indicators (drawn as dots, never in the pill).</param>
/// <param name="NextChangeAt">When the layout may change by time alone (expiry, paused-media grace).</param>
/// <param name="HiddenAppInFront">Persistent activities hidden because their own app is in front.</param>
public sealed record ActivityLayout(
    IslandActivity? Transient,
    IslandActivity? Main,
    IslandActivity? Bubble,
    IReadOnlyList<IslandActivity> Queue,
    IReadOnlyList<IslandActivity> WaitingTransients,
    IReadOnlyList<IslandActivity> Suppressed,
    IReadOnlyList<IslandActivity> PrivacyIndicators,
    DateTimeOffset? NextChangeAt,
    IReadOnlyList<IslandActivity>? HiddenAppInFront = null)
{
    public static ActivityLayout Empty { get; } = new(null, null, null, [], [], [], [], null, []);

    public IReadOnlyList<IslandActivity> HiddenBecauseAppInFront => HiddenAppInFront ?? [];

    /// <summary>Persistent activities in display order: main, bubble, then queue.</summary>
    public IEnumerable<IslandActivity> PersistentInOrder =>
        new[] { Main, Bubble }.OfType<IslandActivity>().Concat(Queue);
}

/// <summary>
/// Decides which activity goes in the pill, the bubble and the queue (BLUEPRINT §8.5). Pure and deterministic.
/// <list type="number">
/// <item>Transients take the pill temporarily (highest priority, then newest); the persistent layout returns when they end.</item>
/// <item>Persistent: highest priority → main, second → bubble, rest → queue. Ties: the one that started first, then id.</item>
/// <item>Media paused for 30 s or more drops to the queue.</item>
/// <item>The user's preferred main activity (swap / cycle) goes first while it exists.</item>
/// <item>Quiet mode suppresses notification banners only.</item>
/// </list>
/// </summary>
public static class PriorityArbiter
{
    public static readonly TimeSpan PausedMediaGrace = TimeSpan.FromSeconds(30);

    public static ActivityLayout Arrange(ArbiterInput input)
    {
        var transients = new List<IslandActivity>();
        var suppressed = new List<IslandActivity>();
        var privacy = new List<IslandActivity>();
        var active = new List<IslandActivity>();
        var demoted = new List<IslandActivity>();
        var hiddenInFront = new List<IslandActivity>();
        DateTimeOffset? next = null;

        foreach (var a in input.Activities)
        {
            if (a.ExpiresAt is { } expires)
            {
                next = Min(next, expires);
            }

            if (a.Kind == ActivityKind.PrivacyIndicator)
            {
                privacy.Add(a);
            }
            else if (!a.IsTransient && input.Foreground is { } front && front.Matches(a.SourceAppId))
            {
                hiddenInFront.Add(a); // the user is looking at this app already
            }
            else if (a.IsTransient)
            {
                if (input.QuietMode && a.Kind == ActivityKind.Notification)
                {
                    suppressed.Add(a);
                }
                else
                {
                    transients.Add(a);
                }
            }
            else if (a.Kind == ActivityKind.Media && a.PausedSince is { } paused)
            {
                var demoteAt = paused + PausedMediaGrace;
                if (input.Now >= demoteAt)
                {
                    demoted.Add(a);
                }
                else
                {
                    active.Add(a);
                    next = Min(next, demoteAt);
                }
            }
            else
            {
                active.Add(a);
            }
        }

        transients.Sort(CompareTransient);
        active.Sort(ComparePersistent);
        demoted.Sort(ComparePersistent);

        if (input.Preference is { } pref)
        {
            var index = active.FindIndex(a => a.Id == pref.PreferredMainId);
            if (index > 0)
            {
                var preferred = active[index];
                active.RemoveAt(index);
                active.Insert(0, preferred);
            }
        }

        var queue = active.Skip(2).Concat(demoted).ToList();
        privacy.Sort(ComparePersistent);

        return new ActivityLayout(
            transients.FirstOrDefault(),
            active.ElementAtOrDefault(0),
            active.ElementAtOrDefault(1),
            queue,
            transients.Skip(1).ToList(),
            suppressed,
            privacy,
            next,
            hiddenInFront);
    }

    /// <summary>Persistent order: priority desc, started earlier first, then id.</summary>
    public static int ComparePersistent(IslandActivity x, IslandActivity y)
    {
        var byPriority = y.Priority.CompareTo(x.Priority);
        if (byPriority != 0)
        {
            return byPriority;
        }

        var byStart = x.StartedAt.CompareTo(y.StartedAt);
        return byStart != 0 ? byStart : string.CompareOrdinal(x.Id, y.Id);
    }

    /// <summary>Transient order: priority desc, most recently updated first, then id.</summary>
    public static int CompareTransient(IslandActivity x, IslandActivity y)
    {
        var byPriority = y.Priority.CompareTo(x.Priority);
        if (byPriority != 0)
        {
            return byPriority;
        }

        var byUpdate = y.UpdatedAt.CompareTo(x.UpdatedAt);
        return byUpdate != 0 ? byUpdate : string.CompareOrdinal(x.Id, y.Id);
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset b) => a is null || b < a ? b : a;
}
