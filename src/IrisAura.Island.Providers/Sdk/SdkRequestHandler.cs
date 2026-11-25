using System.Security.Cryptography;
using System.Text;
using IrisAura.Contracts;
using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Sdk;

/// <summary>
/// Who is talking on the pipe. <see cref="ExePath"/> is read by the Island from the connected process (not claimed
/// by the client); <see cref="AppName"/> is what the client declared. Together they are the app identity used for
/// ownership, limits and blocking: two apps cannot touch each other's activities, and one exe (for example the
/// <c>irisaura-island</c> command line) can act for several named scripts.
/// </summary>
public sealed record SdkClient(string ExePath, string ProcessName, string AppName)
{
    public string Key => ExePath.ToLowerInvariant() + "|" + AppName;

    public string Display => $"{AppName} ({Path.GetFileName(ExePath)})";
}

/// <summary>View payload of an SDK activity: its icon bytes (validated, ≤ 128 × 128) and app name.</summary>
public sealed record SdkVisuals(byte[]? Icon, string App);

/// <summary>What to do with one message.</summary>
/// <param name="Reply">Message to send back, if any.</param>
/// <param name="Start">Activity to start or replace (start and update both publish the full merged activity).</param>
/// <param name="EndId">Engine id to end.</param>
/// <param name="FinalText">End: final text.</param>
/// <param name="DismissAfter">End: how long the final text stays.</param>
public sealed record SdkOutcome(
    IslandSdkMessage? Reply,
    ActivitySpec? Start = null,
    string? EndId = null,
    string? FinalText = null,
    TimeSpan? DismissAfter = null);

/// <summary>
/// The SDK protocol rules (BLUEPRINT §8.6.14), pure and single-threaded (the provider serialises calls):
/// version check, ids, ownership, 5 activities per app, 10 updates/s per activity, priority 10–60, TTL, text
/// sanitising, icon size, safe open links, buttons. Activities outlive the connection (a script can push and exit);
/// they end at their TTL (default 1 h), on <c>end</c>, or when the app is blocked.
/// </summary>
public sealed class SdkRequestHandler(TimeProvider time, Func<string, bool> isBlocked)
{
    public const string ReservedOpenButton = "open";

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SdkRateLimiter _rate = new();

    /// <summary>Engine activity id for an app's own id: <c>sdk:&lt;8-hex app hash&gt;:&lt;id&gt;</c>.</summary>
    public static string EngineId(SdkClient client, string id) => $"sdk:{Hash(client.Key)}:{id}";

    public SdkOutcome Handle(SdkClient client, IslandSdkMessage message)
    {
        if (message.V != ProtocolVersions.IslandSdk)
        {
            return Error(IslandSdkErrors.BadVersion, message.Id, $"Protocol version {message.V} is not supported; use {ProtocolVersions.IslandSdk}.");
        }

        Prune();
        if (isBlocked(client.Key) && message.Op != IslandSdkOps.Query)
        {
            return Error(IslandSdkErrors.Blocked, message.Id, "This app is blocked in IrisAura Island.");
        }

        return message.Op switch
        {
            IslandSdkOps.Start => Start(client, message, isUpdate: false),
            IslandSdkOps.Update => Start(client, message, isUpdate: true),
            IslandSdkOps.End => End(client, message),
            IslandSdkOps.Query => new SdkOutcome(new IslandSdkMessage
            {
                V = ProtocolVersions.IslandSdk,
                Op = IslandSdkOps.Activities,
                Ids = _entries.Values.Where(e => e.ClientKey == client.Key).Select(e => e.Id).OrderBy(i => i, StringComparer.Ordinal).ToList(),
            }),
            _ => Error(IslandSdkErrors.BadRequest, message.Id, $"Unknown op '{SdkText.Clean(message.Op, 16)}'."),
        };
    }

    /// <summary>Owner, button ids and link of a live activity (for actions), or <see langword="null"/>.</summary>
    public (string ClientKey, string Id, string? OpenUri, IReadOnlyList<string> Buttons)? Find(string engineId)
    {
        Prune();
        return _entries.TryGetValue(engineId, out var e) ? (e.ClientKey, e.Id, e.OpenUri, e.Buttons) : null;
    }

    /// <summary>Forgets an app's activities (blocked); returns the engine ids to end.</summary>
    public IReadOnlyList<string> RemoveClient(string clientKey)
    {
        var ids = _entries.Where(kv => kv.Value.ClientKey == clientKey).Select(kv => kv.Key).ToList();
        foreach (var id in ids)
        {
            _entries.Remove(id);
            _rate.Forget(id);
        }

        return ids;
    }

    /// <summary>Forgets everything (provider stopped; the host removes the activities).</summary>
    public void Clear() => _entries.Clear();

    public int CountFor(string clientKey)
    {
        Prune();
        return _entries.Values.Count(e => e.ClientKey == clientKey);
    }

    private SdkOutcome Start(SdkClient client, IslandSdkMessage m, bool isUpdate)
    {
        if (!SdkText.IsValidId(m.Id, IslandSdkLimits.MaxIdLength))
        {
            return Error(IslandSdkErrors.BadRequest, null, "id must be 1–64 characters of A–Z a–z 0–9 . _ -");
        }

        var engineId = EngineId(client, m.Id!);
        var exists = _entries.TryGetValue(engineId, out var entry);
        if (isUpdate && !exists)
        {
            return Error(IslandSdkErrors.NotFound, m.Id, "No such activity (never started, ended or expired).");
        }

        if (!exists && CountFor(client.Key) >= IslandSdkLimits.MaxActivitiesPerApp)
        {
            return Error(IslandSdkErrors.TooManyActivities, m.Id, $"At most {IslandSdkLimits.MaxActivitiesPerApp} activities per app.");
        }

        if (!_rate.TryTake(engineId, time.GetUtcNow()))
        {
            return Error(IslandSdkErrors.RateLimited, m.Id, $"At most {IslandSdkLimits.MaxUpdatesPerSecond} updates per second per activity; this one was dropped.");
        }

        // Merge onto the current state (update changes only what it sends; start replaces).
        var state = isUpdate ? entry!.State.Clone() : new MergedState(); // a rejected update leaves the activity as it was
        if (Merge(state, m, isUpdate) is { } invalid)
        {
            return Error(IslandSdkErrors.BadRequest, m.Id, invalid);
        }

        var app = state.App ?? SdkText.Clean(m.App, SdkText.MaxAppName) ?? client.AppName;
        state.App = app;
        var ttl = TimeSpan.FromSeconds(state.TtlSeconds ?? IslandSdkLimits.DefaultTtlSeconds);
        var buttons = state.Buttons ?? [];
        var title = state.Title ?? state.LeadingText ?? app;
        var spec = new ActivitySpec(
            engineId,
            ActivityKind.Custom,
            new CompactContent(
                LeadingGlyph: state.Icon is null ? "Aura.Glyph.Device" : null,
                LeadingText: state.LeadingText ?? app,
                TrailingText: state.TrailingText,
                TrailingProgress: state.TrailingProgress),
            new ExpandedContent(title, state.Subtitle, state.Progress, buttons.Select(b => new ActivityButton(b.Id, b.Label)).ToList()),
            Priority: state.Priority ?? ActivityDefaults.Priority(ActivityKind.Custom),
            Ttl: ttl,
            SourceAppId: state.OpenUri is null ? client.ProcessName : string.Empty,
            AccessibleText: string.Join(", ", new[] { app, title, state.Subtitle, state.TrailingText }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
            View: new SdkVisuals(state.Icon, app));

        _entries[engineId] = new Entry(client.Key, m.Id!, state, time.GetUtcNow() + ttl);
        return new SdkOutcome(null, spec);
    }

    private SdkOutcome End(SdkClient client, IslandSdkMessage m)
    {
        if (!SdkText.IsValidId(m.Id, IslandSdkLimits.MaxIdLength))
        {
            return Error(IslandSdkErrors.BadRequest, null, "id must be 1–64 characters of A–Z a–z 0–9 . _ -");
        }

        var engineId = EngineId(client, m.Id!);
        if (!_entries.Remove(engineId))
        {
            return Error(IslandSdkErrors.NotFound, m.Id, "No such activity (never started, ended or expired).");
        }

        _rate.Forget(engineId);
        var dismiss = m.DismissAfterSeconds is { } d && double.IsFinite(d) && d > 0
            ? TimeSpan.FromSeconds(Math.Min(d, IslandSdkLimits.MaxDismissSeconds))
            : (TimeSpan?)null;
        return new SdkOutcome(null, EndId: engineId, FinalText: SdkText.Clean(m.FinalText, SdkText.MaxFinalText), DismissAfter: dismiss);
    }

    /// <summary>Applies a message onto the state; returns an error text when a value is invalid.</summary>
    private static string? Merge(MergedState s, IslandSdkMessage m, bool isUpdate)
    {
        if (m.Priority is { } p)
        {
            s.Priority = Math.Clamp(p, IslandSdkLimits.MinPriority, IslandSdkLimits.MaxPriority);
        }

        if (m.TtlSeconds is { } ttl)
        {
            if (!double.IsFinite(ttl) || ttl <= 0)
            {
                return "ttlSeconds must be a positive number";
            }

            s.TtlSeconds = Math.Clamp(ttl, 1, IslandSdkLimits.MaxTtlSeconds);
        }

        if (m.OpenUri is not null)
        {
            if (!SdkUriPolicy.IsAllowed(m.OpenUri))
            {
                return "openUri must be an http(s) link or the app's own scheme (file, script and system schemes are not allowed)";
            }

            s.OpenUri = m.OpenUri;
        }

        if (!isUpdate && m.App is not null)
        {
            s.App = SdkText.Clean(m.App, SdkText.MaxAppName);
        }

        if (m.Compact is { } c)
        {
            if (c.LeadingIcon is not null)
            {
                var (bytes, error) = SdkImage.FromDataUri(c.LeadingIcon);
                if (error is not null)
                {
                    return error;
                }

                s.Icon = bytes;
            }

            s.LeadingText = c.LeadingText is null ? s.LeadingText : SdkText.Clean(c.LeadingText, SdkText.MaxLeadingText);
            s.TrailingText = c.TrailingText is null ? s.TrailingText : SdkText.Clean(c.TrailingText, SdkText.MaxTrailingText);
            if (c.TrailingProgress is { } tp)
            {
                if (!double.IsFinite(tp))
                {
                    return "trailingProgress must be a number from 0 to 1";
                }

                s.TrailingProgress = Math.Clamp(tp, 0, 1);
            }
        }

        if (m.Expanded is { } e)
        {
            s.Title = e.Title is null ? s.Title : SdkText.Clean(e.Title, SdkText.MaxTitle);
            s.Subtitle = e.Subtitle is null ? s.Subtitle : SdkText.Clean(e.Subtitle, SdkText.MaxSubtitle);
            if (e.Progress is { } pr)
            {
                if (!double.IsFinite(pr))
                {
                    return "progress must be a number from 0 to 1";
                }

                s.Progress = Math.Clamp(pr, 0, 1);
            }

            if (e.Buttons is { } buttons)
            {
                if (buttons.Count > IslandSdkLimits.MaxButtons)
                {
                    return $"At most {IslandSdkLimits.MaxButtons} buttons";
                }

                var list = new List<(string Id, string Label)>();
                foreach (var b in buttons)
                {
                    if (b is null || !SdkText.IsValidId(b.Id, IslandSdkLimits.MaxButtonIdLength) || b.Id == ReservedOpenButton
                        || SdkText.Clean(b.Label, SdkText.MaxButtonLabel) is not { } label || list.Any(x => x.Id == b.Id))
                    {
                        return "Each button needs a unique id (1–32 of A–Z a–z 0–9 . _ -, not 'open') and a label";
                    }

                    list.Add((b.Id!, label));
                }

                s.Buttons = list;
            }
        }

        return null;
    }

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var id in _entries.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
        {
            _entries.Remove(id);
            _rate.Forget(id);
        }
    }

    private static SdkOutcome Error(string code, string? id, string text) => new(new IslandSdkMessage
    {
        V = ProtocolVersions.IslandSdk,
        Op = IslandSdkOps.Error,
        Id = SdkText.IsValidId(id, IslandSdkLimits.MaxIdLength) ? id : null,
        Code = code,
        Message = text,
    });

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 4).ToLowerInvariant();

    private sealed record Entry(string ClientKey, string Id, MergedState State, DateTimeOffset ExpiresAt)
    {
        public string? OpenUri => State.OpenUri;

        public IReadOnlyList<string> Buttons => State.Buttons?.Select(b => b.Id).ToList() ?? [];
    }

    private sealed class MergedState
    {
        public MergedState Clone() => (MergedState)MemberwiseClone();

        public string? App { get; set; }

        public int? Priority { get; set; }

        public double? TtlSeconds { get; set; }

        public string? OpenUri { get; set; }

        public byte[]? Icon { get; set; }

        public string? LeadingText { get; set; }

        public string? TrailingText { get; set; }

        public double? TrailingProgress { get; set; }

        public string? Title { get; set; }

        public string? Subtitle { get; set; }

        public double? Progress { get; set; }

        public List<(string Id, string Label)>? Buttons { get; set; }
    }
}
