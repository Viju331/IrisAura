namespace IrisAura.Island.Shell;

/// <summary>Result of a <see cref="HoverIntent.Update"/> call.</summary>
public enum HoverChange
{
    None,
    Expand,
    Collapse,
}

/// <summary>
/// Hover-to-expand logic (BLUEPRINT §8.1, §8.5): hover for <see cref="EnterDelay"/> expands; leaving for
/// <see cref="LeaveDelay"/> collapses unless pinned. Pure: the caller supplies the clock.
/// </summary>
public sealed class HoverIntent
{
    private bool _inside;
    private TimeSpan _since;

    public TimeSpan EnterDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan LeaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    public bool IsExpanded { get; private set; }

    /// <summary>Pinned open by a click or keyboard focus; hover-out does not collapse it.</summary>
    public bool IsPinned { get; private set; }

    public bool IsInside => _inside;

    /// <summary>Feeds the latest cursor sample.</summary>
    /// <param name="inside">Whether the cursor is over the interactive Island.</param>
    /// <param name="now">Monotonic time of the sample.</param>
    /// <param name="canExpand">Whether the current content has an expanded view.</param>
    public HoverChange Update(bool inside, TimeSpan now, bool canExpand)
    {
        if (inside != _inside)
        {
            _inside = inside;
            _since = now;
        }

        if (!canExpand)
        {
            return Reset();
        }

        if (!IsExpanded && _inside && now - _since >= EnterDelay)
        {
            IsExpanded = true;
            return HoverChange.Expand;
        }

        if (IsExpanded && !IsPinned && !_inside && now - _since >= LeaveDelay)
        {
            IsExpanded = false;
            return HoverChange.Collapse;
        }

        return HoverChange.None;
    }

    /// <summary>Pins the Island open (click or keyboard).</summary>
    public HoverChange Pin(bool canExpand)
    {
        if (!canExpand)
        {
            return HoverChange.None;
        }

        IsPinned = true;
        if (IsExpanded)
        {
            return HoverChange.None;
        }

        IsExpanded = true;
        return HoverChange.Expand;
    }

    /// <summary>Unpins; collapses straight away unless the cursor is still over the Island.</summary>
    public HoverChange Unpin(TimeSpan now)
    {
        IsPinned = false;
        if (IsExpanded && !_inside)
        {
            IsExpanded = false;
            return HoverChange.Collapse;
        }

        _since = now;
        return HoverChange.None;
    }

    /// <summary>Collapses and unpins (content changed to something that cannot expand).</summary>
    public HoverChange Reset()
    {
        IsPinned = false;
        if (!IsExpanded)
        {
            return HoverChange.None;
        }

        IsExpanded = false;
        return HoverChange.Collapse;
    }
}
