namespace IrisAura.Contracts;

/// <summary>
/// One line of the Island Activity SDK protocol v1 (BLUEPRINT §8.6.14, docs/ISLAND_SDK.md): newline-delimited UTF-8
/// JSON with camelCase names. One flat shape for every operation; unused members are omitted.
/// <para>App → Island: <c>start</c>, <c>update</c>, <c>end</c>, <c>query</c>. Island → app: <c>hello</c>, <c>action</c>,
/// <c>error</c>, <c>activities</c>.</para>
/// </summary>
public sealed class IslandSdkMessage
{
    /// <summary>Protocol version; must be <see cref="ProtocolVersions.IslandSdk"/>.</summary>
    public int V { get; set; }

    /// <summary>Operation (see <see cref="IslandSdkOps"/>).</summary>
    public string? Op { get; set; }

    /// <summary>Activity id chosen by the app: 1–64 of <c>A–Z a–z 0–9 . _ -</c>.</summary>
    public string? Id { get; set; }

    /// <summary>Display name of the app (start).</summary>
    public string? App { get; set; }

    /// <summary>10–60; higher wins the pill (start/update).</summary>
    public int? Priority { get; set; }

    public IslandSdkCompact? Compact { get; set; }

    public IslandSdkExpanded? Expanded { get; set; }

    /// <summary>Opened when the user clicks the activity (http, https or the app's own scheme).</summary>
    public string? OpenUri { get; set; }

    /// <summary>Lifetime in seconds (default 3600, max 86400).</summary>
    public double? TtlSeconds { get; set; }

    /// <summary>end: text shown briefly before the activity goes.</summary>
    public string? FinalText { get; set; }

    /// <summary>end: how long <see cref="FinalText"/> stays (max 10).</summary>
    public double? DismissAfterSeconds { get; set; }

    /// <summary>action: the button id the user pressed (<c>open</c> for a click without an open URI).</summary>
    public string? Button { get; set; }

    /// <summary>error: machine-readable code (see <see cref="IslandSdkErrors"/>).</summary>
    public string? Code { get; set; }

    /// <summary>error / hello: human-readable text.</summary>
    public string? Message { get; set; }

    /// <summary>hello: server version.</summary>
    public string? Server { get; set; }

    /// <summary>activities: ids of the caller's own live activities.</summary>
    public List<string>? Ids { get; set; }
}

/// <summary>Compact (pill) content.</summary>
public sealed class IslandSdkCompact
{
    /// <summary><c>data:image/png;base64,…</c> (PNG or JPEG, at most 128 × 128).</summary>
    public string? LeadingIcon { get; set; }

    public string? LeadingText { get; set; }

    public string? TrailingText { get; set; }

    /// <summary>0–1 ring when there is no trailing text.</summary>
    public double? TrailingProgress { get; set; }
}

/// <summary>Expanded (panel) content.</summary>
public sealed class IslandSdkExpanded
{
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>0–1 progress bar.</summary>
    public double? Progress { get; set; }

    /// <summary>Up to 3 buttons; presses come back as <c>action</c> messages.</summary>
    public List<IslandSdkButton>? Buttons { get; set; }
}

public sealed class IslandSdkButton
{
    /// <summary>1–32 of <c>A–Z a–z 0–9 . _ -</c>.</summary>
    public string? Id { get; set; }

    public string? Label { get; set; }
}

/// <summary>Operation names.</summary>
public static class IslandSdkOps
{
    public const string Start = "start";
    public const string Update = "update";
    public const string End = "end";
    public const string Query = "query";
    public const string Hello = "hello";
    public const string Action = "action";
    public const string Error = "error";
    public const string Activities = "activities";
}

/// <summary>Error codes sent by the Island.</summary>
public static class IslandSdkErrors
{
    public const string BadJson = "bad-json";
    public const string BadVersion = "bad-version";
    public const string BadRequest = "bad-request";
    public const string NotFound = "not-found";
    public const string RateLimited = "rate-limited";
    public const string TooManyActivities = "too-many-activities";
    public const string Blocked = "blocked";
    public const string Disabled = "disabled";
}

/// <summary>Protocol limits (BLUEPRINT §8.6.14). The Island enforces them; the client library checks them early.</summary>
public static class IslandSdkLimits
{
    /// <summary>Longest line in bytes (UTF-8, without the newline); a longer line closes the connection.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    public const int MinPriority = 10;
    public const int MaxPriority = 60;
    public const int MaxIconPixels = 128;
    public const int MaxUpdatesPerSecond = 10;
    public const int MaxActivitiesPerApp = 5;
    public const int MaxButtons = 3;
    public const int MaxIdLength = 64;
    public const int MaxButtonIdLength = 32;
    public const double DefaultTtlSeconds = 3600;
    public const double MaxTtlSeconds = 86400;
    public const double MaxDismissSeconds = 10;
}
