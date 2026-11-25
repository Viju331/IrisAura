using IrisAura.Contracts;

namespace IrisAura.Island.Sdk;

/// <summary>A button on your activity; presses arrive as <see cref="IslandClient.ActionReceived"/>.</summary>
public sealed class IslandButton(string id, string label)
{
    public string Id { get; } = id;

    public string Label { get; } = label;
}

/// <summary>What your activity shows (BLUEPRINT §8.6.14). Text is cleaned and shortened by the Island.</summary>
public sealed class IslandActivitySpec(string id, string title, double? progress = null)
{
    /// <summary>Your id (1–64 of A–Z a–z 0–9 . _ -), unique within your app.</summary>
    public string Id { get; } = id;

    public string Title { get; set; } = title;

    public string? Subtitle { get; set; }

    /// <summary>0–1 progress bar in the panel.</summary>
    public double? Progress { get; set; } = progress;

    /// <summary>Text at the left of the pill (defaults to your app name).</summary>
    public string? LeadingText { get; set; }

    /// <summary>Text at the right of the pill, e.g. "37%".</summary>
    public string? TrailingText { get; set; }

    /// <summary>0–1 ring at the right of the pill when there is no trailing text.</summary>
    public double? TrailingProgress { get; set; }

    /// <summary>PNG (or JPEG) bytes, at most 128 × 128.</summary>
    public byte[]? Icon { get; set; }

    /// <summary>Up to three buttons.</summary>
    public IList<IslandButton> Buttons { get; } = new List<IslandButton>();

    /// <summary>Opened when the user clicks the activity: http(s) or your app's own scheme.</summary>
    public string? OpenUri { get; set; }

    /// <summary>10–60 (default 30).</summary>
    public int? Priority { get; set; }

    /// <summary>Lifetime (default 1 hour, at most 24 hours). The activity stays after your app disconnects.</summary>
    public TimeSpan? Ttl { get; set; }

    internal IslandSdkMessage ToMessage(string? app) => new()
    {
        V = ProtocolVersions.IslandSdk,
        Op = IslandSdkOps.Start,
        Id = Id,
        App = app,
        Priority = Priority,
        OpenUri = OpenUri,
        TtlSeconds = Ttl?.TotalSeconds,
        Compact = new IslandSdkCompact
        {
            LeadingIcon = Icon is null ? null : IconUri(Icon),
            LeadingText = LeadingText,
            TrailingText = TrailingText,
            TrailingProgress = TrailingProgress,
        },
        Expanded = new IslandSdkExpanded
        {
            Title = Title,
            Subtitle = Subtitle,
            Progress = Progress,
            Buttons = Buttons.Count == 0 ? null : Buttons.Select(b => new IslandSdkButton { Id = b.Id, Label = b.Label }).ToList(),
        },
    };

    internal static string IconUri(byte[] icon) =>
        (icon.Length > 2 && icon[0] == 0xFF && icon[1] == 0xD8 ? "data:image/jpeg;base64," : "data:image/png;base64,") + Convert.ToBase64String(icon);
}

/// <summary>Values to change; <see langword="null"/> members stay as they are.</summary>
public sealed class IslandActivityUpdate
{
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public double? Progress { get; set; }

    public string? LeadingText { get; set; }

    public string? TrailingText { get; set; }

    public double? TrailingProgress { get; set; }

    public byte[]? Icon { get; set; }

    /// <summary>Replaces all buttons when set.</summary>
    public IList<IslandButton>? Buttons { get; set; }

    public string? OpenUri { get; set; }

    public int? Priority { get; set; }

    public TimeSpan? Ttl { get; set; }

    internal IslandSdkMessage ToMessage(string id, string? app) => new()
    {
        V = ProtocolVersions.IslandSdk,
        Op = IslandSdkOps.Update,
        Id = id,
        App = app,
        Priority = Priority,
        OpenUri = OpenUri,
        TtlSeconds = Ttl?.TotalSeconds,
        Compact = LeadingText is null && TrailingText is null && TrailingProgress is null && Icon is null ? null : new IslandSdkCompact
        {
            LeadingIcon = Icon is null ? null : IslandActivitySpec.IconUri(Icon),
            LeadingText = LeadingText,
            TrailingText = TrailingText,
            TrailingProgress = TrailingProgress,
        },
        Expanded = Title is null && Subtitle is null && Progress is null && Buttons is null ? null : new IslandSdkExpanded
        {
            Title = Title,
            Subtitle = Subtitle,
            Progress = Progress,
            Buttons = Buttons?.Select(b => new IslandSdkButton { Id = b.Id, Label = b.Label }).ToList(),
        },
    };
}
