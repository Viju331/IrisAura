using IrisAura.Island.Activities;
using IrisAura.Island.Providers.Privacy;

namespace IrisAura.Island.Providers.Calls;

/// <summary>A known call app (BLUEPRINT §8.6.8; the list becomes configurable in the settings window).</summary>
/// <param name="DisplayName">Name shown on the Island.</param>
/// <param name="ProcessNames">Exe names without extension (unpackaged consent-store keys and audio sessions).</param>
/// <param name="PackageFamilyPrefixes">Package family name prefixes (packaged consent-store keys).</param>
public sealed record CallApp(string DisplayName, IReadOnlyList<string> ProcessNames, IReadOnlyList<string> PackageFamilyPrefixes)
{
    public static IReadOnlyList<CallApp> Defaults { get; } =
    [
        new("Microsoft Teams", ["ms-teams", "Teams"], ["MSTeams_", "MicrosoftTeams_"]),
        new("WhatsApp", ["WhatsApp", "WhatsApp.Root"], ["5319275A.WhatsAppDesktop_"]),
        new("Zoom", ["Zoom"], []),
        new("Slack", ["slack"], []),
        new("Discord", ["Discord"], []),
    ];

    public bool Matches(CapabilityUser user) =>
        (user.ProcessName is { } process && ProcessNames.Contains(process, StringComparer.OrdinalIgnoreCase))
        || (user.PackageFamilyName is { } family && PackageFamilyPrefixes.Any(p => family.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>What the detector wants shown.</summary>
public enum CallChange
{
    None,
    Started,
    Tick,
    Ended,
}

/// <summary>
/// Call detection heuristic (BLUEPRINT §8.6.8, beta). Pure state machine; the provider feeds it observations.
/// <list type="bullet">
/// <item>A call starts only when a known call app holds the microphone <b>and</b> is playing audio (its own output
///   session is active). Microphone use alone is never treated as a call (§10 Phase 4).</item>
/// <item>The call ends when that app has released the microphone for more than 3 s.</item>
/// </list>
/// </summary>
public sealed class CallDetector(IReadOnlyList<CallApp> apps)
{
    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(3);

    private DateTimeOffset? _micReleasedAt;

    public CallApp? ActiveApp { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Known call apps currently holding the microphone.</summary>
    public IReadOnlyList<CallApp> CallAppsUsingMicrophone(CapabilitySnapshot snapshot) =>
        apps.Where(app => snapshot.Microphone.Any(app.Matches)).ToList();

    /// <summary>Whether the detector needs a 1 s tick (a candidate is waiting for corroboration, or a call is active).</summary>
    public bool NeedsTicks(CapabilitySnapshot snapshot) => ActiveApp is not null || CallAppsUsingMicrophone(snapshot).Count > 0;

    /// <param name="snapshot">Microphone users now.</param>
    /// <param name="isPlayingAudio">Whether the app has an active audio output session (corroboration).</param>
    /// <param name="now">Current time.</param>
    public CallChange Observe(CapabilitySnapshot snapshot, Func<CallApp, bool> isPlayingAudio, DateTimeOffset now)
    {
        var holding = CallAppsUsingMicrophone(snapshot);
        if (ActiveApp is { } active)
        {
            if (holding.Contains(active))
            {
                _micReleasedAt = null;
                return CallChange.Tick;
            }

            _micReleasedAt ??= now;
            if (now - _micReleasedAt.Value > EndGrace)
            {
                ActiveApp = null;
                StartedAt = null;
                _micReleasedAt = null;
                return CallChange.Ended;
            }

            return CallChange.Tick;
        }

        foreach (var app in holding)
        {
            if (isPlayingAudio(app))
            {
                ActiveApp = app;
                StartedAt = now;
                _micReleasedAt = null;
                return CallChange.Started;
            }
        }

        return CallChange.None;
    }

    /// <summary>The call activity (green-phone equivalent in brand colours: Aura.Cyan).</summary>
    public static ActivitySpec Activity(CallApp app, TimeSpan elapsed)
    {
        var text = FormatElapsed(elapsed);
        return new ActivitySpec(
            CallProvider.ActivityId,
            ActivityKind.Call,
            new CompactContent(LeadingGlyph: "Aura.Glyph.Call", LeadingText: app.DisplayName, TrailingText: text, Tone: ActivityTone.Success),
            new ExpandedContent($"Call · {app.DisplayName}", $"{text} · call detection (beta)"),
            SourceAppId: app.ProcessNames[0],
            AccessibleText: $"Possible call in {app.DisplayName}, {text}. Call detection is a beta feature.");
    }

    public static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
}
