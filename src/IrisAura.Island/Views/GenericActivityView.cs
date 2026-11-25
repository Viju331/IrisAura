using IrisAura.Island.Engine;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Views;

/// <summary>A button in the generic expanded view.</summary>
public sealed record ActivityButtonView(string Label, RelayCommand Command);

/// <summary>
/// View model for activities without a custom view (SDK, HUDs, downloads, calls…): renders
/// <see cref="CompactContent"/> / <see cref="ExpandedContent"/> with the generic templates.
/// One instance per activity id; <see cref="Apply"/> updates it in place so the shell does not
/// cross-fade on every content update.
/// </summary>
public sealed class GenericActivityView : ObservableObject, IActivityViewModel
{
    private readonly Action<string, string> _invokeAction;
    private IslandActivity _activity;
    private IReadOnlyList<ActivityButtonView> _buttons = [];

    public GenericActivityView(IslandActivity activity, Action<string, string> invokeAction)
    {
        _activity = activity;
        _invokeAction = invokeAction;
        RebuildButtons();
    }

    public string ActivityId => _activity.Id;

    public ActivityKind Kind => _activity.Kind;

    public string? LeadingGlyph => _activity.Compact.LeadingGlyph;

    /// <summary>Icon colour role (brand tokens only): Default, Success (Aura.Cyan) or Alert (Aura.Error).</summary>
    public ActivityTone Tone => _activity.Compact.Tone;

    public string? LeadingText => _activity.Compact.LeadingText;

    public string? TrailingText => _activity.Compact.TrailingText;

    public double? TrailingProgress => _activity.Compact.TrailingProgress;

    public bool HasTrailingProgress => _activity.Compact.TrailingProgress is not null && _activity.Compact.TrailingText is null;

    public string Title => _activity.Expanded?.Title ?? _activity.Compact.LeadingText ?? _activity.Kind.ToString();

    public string? Subtitle => _activity.Expanded?.Subtitle ?? _activity.Compact.TrailingText;

    public double? Progress => _activity.Expanded?.Progress;

    public bool HasProgress => _activity.Expanded?.Progress is not null;

    public IReadOnlyList<ActivityButtonView> Buttons => _buttons;

    public bool HasButtons => _buttons.Count > 0;

    public string AccessibleDescription =>
        _activity.AccessibleText
        ?? string.Join(", ", new[] { Title, Subtitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public ExpandedKind? ExpandedKind => _activity.Expanded is null ? null : Shell.ExpandedKind.Generic;

    /// <summary>Height grows with subtitle, progress and buttons (120–200).</summary>
    public double ExpandedHeight =>
        120 + (Subtitle is null ? 0 : 4) + (HasProgress ? 18 : 0) + (HasButtons ? 42 : 0);

    public void SetVisible(bool visible)
    {
    }

    /// <summary>Applies a newer version of the same activity.</summary>
    public void Apply(IslandActivity activity)
    {
        _activity = activity;
        RebuildButtons();
        OnPropertyChanged(string.Empty); // all properties
    }

    private void RebuildButtons()
    {
        var id = _activity.Id;
        _buttons = _activity.Expanded?.Buttons?
            .Select(b => new ActivityButtonView(b.Label, new RelayCommand(() => _invokeAction(id, b.Id))))
            .ToList() ?? [];
    }
}
