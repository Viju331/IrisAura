using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IrisAura.Contracts;
using IrisAura.Island.Activities;
using Serilog;

namespace IrisAura.Island.Providers.Sdk;

/// <summary>An app that has used the SDK since the Island started (Activities → External apps menu).</summary>
/// <param name="Key">Identity key (exe path + app name).</param>
/// <param name="Display">"Build Monitor (builder.exe)".</param>
/// <param name="Connected">Has an open connection.</param>
/// <param name="Blocked">Blocked by the user.</param>
/// <param name="Activities">Live activities.</param>
public sealed record SdkAppInfo(string Key, string Display, bool Connected, bool Blocked, int Activities);

/// <summary>
/// External activities (BLUEPRINT §8.6.14): lets any app of the current user show its own activities through the
/// per-user pipe. Messages go through <see cref="SdkRequestHandler"/> (version, ids, limits, sanitising); button
/// presses and clicks are sent back to the app's latest connection. Blocked apps are refused and their activities
/// removed. Logs carry exe file names and error codes only, never activity text.
/// </summary>
public sealed class SdkActivityProvider : IActivityProvider
{
    public const string ProviderId = "sdk";
    public const int MaxConsecutiveErrors = 20;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<string> _pipeName;
    private readonly HashSet<string> _blocked;
    private readonly Dictionary<string, (string Display, SdkConnection? Connection)> _apps = new(StringComparer.Ordinal);
    private SdkRequestHandler _handler;
    private IActivitySink? _sink;
    private SdkPipeServer? _server;

    /// <param name="blocked">Blocked app keys (persisted by the Island).</param>
    /// <param name="pipeName">Pipe name; default: the current user's <see cref="PipeNames.IslandActivities"/>.</param>
    /// <param name="time">Clock.</param>
    public SdkActivityProvider(IEnumerable<string>? blocked = null, Func<string>? pipeName = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _pipeName = pipeName ?? (() => PipeNames.IslandActivities(WindowsIdentity.GetCurrent().User!.Value));
        _blocked = new HashSet<string>(blocked ?? [], StringComparer.Ordinal);
        _handler = new SdkRequestHandler(_time, IsBlocked);
    }

    /// <summary>The set of known apps or the block list changed.</summary>
    public event EventHandler? AppsChanged;

    public string Id => ProviderId;

    public string DisplayName => "Apps using the IrisAura SDK";

    public string? PipeName => _server?.PipeName;

    public IReadOnlyList<SdkAppInfo> Apps
    {
        get
        {
            lock (_gate)
            {
                return _apps.Select(kv => new SdkAppInfo(
                        kv.Key, kv.Value.Display, kv.Value.Connection is { IsClosed: false }, _blocked.Contains(kv.Key), _handler.CountFor(kv.Key)))
                    .Concat(_blocked.Where(k => !_apps.ContainsKey(k)).Select(k => new SdkAppInfo(k, DisplayFromKey(k), false, true, 0)))
                    .OrderBy(a => a.Display, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public IReadOnlyCollection<string> BlockedApps
    {
        get
        {
            lock (_gate)
            {
                return [.. _blocked];
            }
        }
    }

    public void Start(IActivitySink sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _handler = new SdkRequestHandler(_time, IsBlocked);
        }

        var server = new SdkPipeServer(_pipeName(), RunConnectionAsync);
        try
        {
            server.Start();
        }
        catch (SdkPipeTakenException)
        {
            Log.Warning("SDK pipe name already taken by another process; external activities are off (possible pipe squatting)");
            sink.ReportUnavailable("the SDK pipe name is in use by another process");
            return;
        }

        _server = server;
    }

    public void Stop()
    {
        _server?.Dispose();
        _server = null;
        lock (_gate)
        {
            _sink = null;
            _handler.Clear();
            foreach (var key in _apps.Keys.ToList())
            {
                _apps[key] = (_apps[key].Display, null);
            }
        }
    }

    /// <summary>Blocks or unblocks an app; blocking removes its activities at once.</summary>
    public void SetBlocked(string appKey, bool blocked)
    {
        IReadOnlyList<string> ended = [];
        IActivitySink? sink;
        lock (_gate)
        {
            if (blocked ? !_blocked.Add(appKey) : !_blocked.Remove(appKey))
            {
                return;
            }

            if (blocked)
            {
                ended = _handler.RemoveClient(appKey);
            }

            sink = _sink;
        }

        foreach (var id in ended)
        {
            sink?.End(id);
        }

        Log.Information("SDK app {State}", blocked ? "blocked" : "unblocked");
        AppsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool OnAction(string activityId, string actionId)
    {
        (string ClientKey, string Id, string? OpenUri, IReadOnlyList<string> Buttons)? entry;
        SdkConnection? connection;
        lock (_gate)
        {
            entry = _handler.Find(activityId);
            connection = entry is { } e && _apps.TryGetValue(e.ClientKey, out var app) ? app.Connection : null;
        }

        if (entry is not { } found)
        {
            return false;
        }

        string button;
        if (actionId == ActivityActions.Open)
        {
            if (found.OpenUri is { } uri && SdkUriPolicy.IsAllowed(uri))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); // the user's click
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Information("SDK: could not open the activity's link ({Error})", ex.GetType().Name);
                    return false;
                }
            }

            button = SdkRequestHandler.ReservedOpenButton;
        }
        else if (found.Buttons.Contains(actionId))
        {
            button = actionId;
        }
        else
        {
            return false;
        }

        if (connection is not { IsClosed: false })
        {
            return false; // the app is not connected to receive it
        }

        _ = connection.SendAsync(new IslandSdkMessage { V = ProtocolVersions.IslandSdk, Op = IslandSdkOps.Action, Id = found.Id, Button = button });
        return true;
    }

    private bool IsBlocked(string key) => _blocked.Contains(key);

    private static string DisplayFromKey(string key)
    {
        var bar = key.LastIndexOf('|');
        return bar < 0 ? key : $"{key[(bar + 1)..]} ({Path.GetFileName(key[..bar])})";
    }

    private async Task RunConnectionAsync(SdkConnection connection, CancellationToken ct)
    {
        if (!connection.Identify())
        {
            Log.Information("SDK: refused a client that could not be identified or is in another session");
            return;
        }

        var exe = Path.GetFileName(connection.ExePath);
        var process = Path.GetFileNameWithoutExtension(connection.ExePath) ?? "app";
        Log.Information("SDK: client connected ({Exe})", exe);
        await connection.SendAsync(new IslandSdkMessage
        {
            V = ProtocolVersions.IslandSdk,
            Op = IslandSdkOps.Hello,
            Server = "IrisAura.Island/" + typeof(SdkActivityProvider).Assembly.GetName().Version,
        }).ConfigureAwait(false);

        var errors = 0;
        try
        {
            while (!ct.IsCancellationRequested && !connection.IsClosed)
            {
                string? line;
                try
                {
                    line = await connection.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    Log.Information("SDK: closed a connection that sent a message over 64 KB ({Exe})", exe);
                    return;
                }
                catch (DecoderFallbackException)
                {
                    line = "\u0000invalid-utf8";
                }

                if (line is null)
                {
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var reply = Handle(connection, process, line);
                if (reply is not null)
                {
                    errors = reply.Op == IslandSdkOps.Error ? errors + 1 : 0;
                    await connection.SendAsync(reply).ConfigureAwait(false);
                    if (errors >= MaxConsecutiveErrors)
                    {
                        Log.Information("SDK: closed a connection after {Count} errors in a row ({Exe})", errors, exe);
                        return;
                    }
                }
                else
                {
                    errors = 0;
                }
            }
        }
        finally
        {
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Parses and applies one line; returns the reply, if any.</summary>
    private IslandSdkMessage? Handle(SdkConnection connection, string process, string line)
    {
        IslandSdkMessage? message;
        try
        {
            message = line.StartsWith('\u0000') ? null : JsonSerializer.Deserialize<IslandSdkMessage>(line, SdkJson.Options);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null)
        {
            return new IslandSdkMessage { V = ProtocolVersions.IslandSdk, Op = IslandSdkOps.Error, Code = IslandSdkErrors.BadJson, Message = "Each line must be one JSON object in UTF-8." };
        }

        var app = SdkText.Clean(message.App, SdkText.MaxAppName) ?? connection.LastApp ?? process;
        connection.LastApp = app;
        var client = new SdkClient(connection.ExePath!, process, app);

        SdkOutcome outcome;
        IActivitySink? sink;
        var newApp = false;
        lock (_gate)
        {
            sink = _sink;
            if (sink is null)
            {
                return new IslandSdkMessage { V = ProtocolVersions.IslandSdk, Op = IslandSdkOps.Error, Id = message.Id, Code = IslandSdkErrors.Disabled, Message = "External activities are switched off in IrisAura Island." };
            }

            outcome = _handler.Handle(client, message);
            newApp = !_apps.ContainsKey(client.Key);
            _apps[client.Key] = (client.Display, connection);

            // Apply under the lock so engine operations keep the order the app sent them in.
            if (outcome.Start is { } spec)
            {
                sink.Start(spec);
            }

            if (outcome.EndId is { } endId)
            {
                sink.End(endId, outcome.FinalText, outcome.DismissAfter);
            }
        }

        if (newApp)
        {
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }

        return outcome.Reply;
    }
}
