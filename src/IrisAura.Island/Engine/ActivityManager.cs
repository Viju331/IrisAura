namespace IrisAura.Island.Engine;

/// <summary>Outcome of a manager operation.</summary>
public enum ActivityResult
{
    Started,
    Updated,
    Ended,

    /// <summary>Update/end for an activity that does not exist (never started, already ended or expired).</summary>
    NotFound,

    /// <summary>Out-of-order update (sequence not newer) or update to an activity that is ending.</summary>
    Stale,

    /// <summary>Id already used by another provider.</summary>
    IdConflict,

    /// <summary>Invalid input (empty or over-long id, negative TTL).</summary>
    Invalid,
}

/// <summary>What changed.</summary>
public enum ActivityChange
{
    Started,
    Updated,
    Ended,
}

public sealed class ActivityChangedEventArgs(ActivityChange change, IslandActivity activity) : EventArgs
{
    public ActivityChange Change { get; } = change;

    public IslandActivity Activity { get; } = activity;
}

/// <summary>
/// Holds the live activities (BLUEPRINT §8.5). Single-threaded: call it on the UI thread
/// (providers go through <see cref="ActivityProviderHost"/>, which marshals and coalesces).
/// <list type="bullet">
/// <item>Ids are owned by the provider that started them; another provider gets <see cref="ActivityResult.IdConflict"/>.</item>
/// <item>Starting an existing id from the same provider replaces its content (keeps <c>StartedAt</c>).</item>
/// <item>Updates to unknown/ended activities are ignored (<see cref="ActivityResult.NotFound"/>); out-of-order
///   sequenced updates are ignored (<see cref="ActivityResult.Stale"/>).</item>
/// <item>Transients expire <c>Ttl</c> after their last update; persistent activities <c>Ttl</c> after start (or after an update that sets a new Ttl).</item>
/// </list>
/// </summary>
public sealed class ActivityManager(TimeProvider time)
{
    public const int MaxIdLength = 128;

    private readonly Dictionary<string, IslandActivity> _activities = new(StringComparer.Ordinal);

    public event EventHandler<ActivityChangedEventArgs>? Changed;

    public IReadOnlyCollection<IslandActivity> Activities => _activities.Values;

    public int Count => _activities.Count;

    public TimeProvider Time => time;

    public IslandActivity? Find(string id) => _activities.GetValueOrDefault(id);

    /// <summary>Earliest expiry among live activities.</summary>
    public DateTimeOffset? NextExpiry =>
        _activities.Values.Where(a => a.ExpiresAt is not null).Select(a => a.ExpiresAt).Min();

    public ActivityResult Start(string providerId, ActivitySpec spec, long sequence = 0)
    {
        if (!IsValidId(spec.Id) || spec.Ttl < TimeSpan.Zero)
        {
            return ActivityResult.Invalid;
        }

        var now = time.GetUtcNow();
        var transient = spec.IsTransient ?? ActivityDefaults.IsTransient(spec.Kind);
        var ttl = spec.Ttl ?? ActivityDefaults.Ttl(spec.Kind) ?? (transient ? TimeSpan.FromSeconds(4) : null);
        var priority = ActivityDefaults.ClampPriority(spec.Kind, spec.Priority ?? ActivityDefaults.Priority(spec.Kind));

        if (_activities.TryGetValue(spec.Id, out var existing))
        {
            if (!string.Equals(existing.ProviderId, providerId, StringComparison.Ordinal))
            {
                return ActivityResult.IdConflict;
            }

            if (sequence > 0 && sequence <= existing.Sequence)
            {
                return ActivityResult.Stale;
            }
        }

        var activity = new IslandActivity(
            spec.Id, spec.Kind, priority, transient, ttl, spec.SourceAppId, spec.Compact, spec.Expanded,
            existing?.StartedAt ?? now)
        {
            ProviderId = providerId,
            UpdatedAt = now,
            ExpiresAt = ttl is { } t ? now + t : null,
            PausedSince = spec.IsPaused ? existing?.PausedSince ?? now : null,
            Sequence = Math.Max(sequence, existing?.Sequence ?? 0),
            AccessibleText = spec.AccessibleText,
            View = spec.View,
        };

        _activities[spec.Id] = activity;
        Raise(existing is null ? ActivityChange.Started : ActivityChange.Updated, activity);
        return existing is null ? ActivityResult.Started : ActivityResult.Updated;
    }

    public ActivityResult Update(string providerId, ActivityUpdate update)
    {
        if (!_activities.TryGetValue(update.Id, out var current))
        {
            return ActivityResult.NotFound;
        }

        if (!string.Equals(current.ProviderId, providerId, StringComparison.Ordinal))
        {
            return ActivityResult.IdConflict;
        }

        if (current.IsEnding || (update.Sequence > 0 && update.Sequence <= current.Sequence))
        {
            return ActivityResult.Stale;
        }

        if (update.Ttl < TimeSpan.Zero)
        {
            return ActivityResult.Invalid;
        }

        var now = time.GetUtcNow();
        var ttl = update.Ttl ?? current.Ttl;
        var expires = update.Ttl is { } newTtl
            ? now + newTtl
            : current.IsTransient && ttl is { } transientTtl ? now + transientTtl : current.ExpiresAt;

        var paused = update.IsPaused switch
        {
            true => current.PausedSince ?? now,
            false => null,
            null => current.PausedSince,
        };

        var updated = current with
        {
            Compact = update.Compact ?? current.Compact,
            Expanded = update.Expanded ?? current.Expanded,
            Priority = update.Priority is { } p ? ActivityDefaults.ClampPriority(current.Kind, p) : current.Priority,
            Ttl = ttl,
            ExpiresAt = expires,
            PausedSince = paused,
            AccessibleText = update.AccessibleText ?? current.AccessibleText,
            Sequence = Math.Max(current.Sequence, update.Sequence),
            UpdatedAt = now,
        };

        _activities[update.Id] = updated;
        Raise(ActivityChange.Updated, updated);
        return ActivityResult.Updated;
    }

    /// <summary>Ends an activity, optionally showing <paramref name="finalText"/> for <paramref name="dismissAfter"/> first.</summary>
    public ActivityResult End(string providerId, string id, string? finalText = null, TimeSpan? dismissAfter = null)
    {
        if (!_activities.TryGetValue(id, out var current))
        {
            return ActivityResult.NotFound;
        }

        if (!string.Equals(current.ProviderId, providerId, StringComparison.Ordinal))
        {
            return ActivityResult.IdConflict;
        }

        if (dismissAfter is { } delay && delay > TimeSpan.Zero && !current.IsEnding)
        {
            var now = time.GetUtcNow();
            var ending = current with
            {
                IsEnding = true,
                Compact = finalText is null ? current.Compact : current.Compact with { TrailingText = finalText, TrailingProgress = null },
                Expanded = finalText is null || current.Expanded is null
                    ? current.Expanded
                    : current.Expanded with { Subtitle = finalText, Progress = null, Buttons = null },
                ExpiresAt = now + delay,
                UpdatedAt = now,
            };
            _activities[id] = ending;
            Raise(ActivityChange.Updated, ending);
            return ActivityResult.Updated;
        }

        _activities.Remove(id);
        Raise(ActivityChange.Ended, current);
        return ActivityResult.Ended;
    }

    /// <summary>Removes every activity of a provider (provider stopped or failed).</summary>
    public int EndAllFrom(string providerId)
    {
        var ids = _activities.Values.Where(a => a.ProviderId == providerId).Select(a => a.Id).ToList();
        foreach (var id in ids)
        {
            var activity = _activities[id];
            _activities.Remove(id);
            Raise(ActivityChange.Ended, activity);
        }

        return ids.Count;
    }

    /// <summary>Removes activities whose expiry has passed.</summary>
    public int ExpireDue()
    {
        var now = time.GetUtcNow();
        var due = _activities.Values.Where(a => a.ExpiresAt <= now).Select(a => a.Id).ToList();
        foreach (var id in due)
        {
            var activity = _activities[id];
            _activities.Remove(id);
            Raise(ActivityChange.Ended, activity);
        }

        return due.Count;
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength && !id.Any(char.IsControl);

    private void Raise(ActivityChange change, IslandActivity activity) =>
        Changed?.Invoke(this, new ActivityChangedEventArgs(change, activity));
}
