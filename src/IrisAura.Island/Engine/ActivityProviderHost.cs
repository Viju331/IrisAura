using Serilog;

namespace IrisAura.Island.Engine;

public enum ProviderState
{
    Registered,
    Running,
    Stopped,

    /// <summary>Crashed: disabled, activities removed, error logged (BLUEPRINT §8.9).</summary>
    Faulted,

    /// <summary>Not supported on this device (no battery, no laptop panel, no radio…); not an error.</summary>
    Unavailable,
}

/// <summary>Provider status for the debug panel and settings.</summary>
/// <param name="Id">Provider id.</param>
/// <param name="DisplayName">Name shown in menus.</param>
/// <param name="State">Current state.</param>
/// <param name="LastError">Error (faulted) or reason (unavailable); never user content.</param>
/// <param name="IsEnabled">The user wants it on (persisted in island-settings.json).</param>
public sealed record ProviderStatus(string Id, string DisplayName, ProviderState State, string? LastError, bool IsEnabled = true);

/// <summary>
/// Runs providers in isolation (BLUEPRINT §8.9): an exception from one provider disables only that
/// provider and removes its activities. Sink calls from any thread are marshalled to the engine's
/// <see cref="SynchronizationContext"/> and consecutive updates to the same activity are coalesced
/// (BLUEPRINT §8.4: "providers post updates … with coalescing"), preserving the order of operations.
/// </summary>
public sealed class ActivityProviderHost
{
    private readonly ActivityManager _manager;
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<string, Entry> _providers = new(StringComparer.Ordinal);

    /// <param name="manager">The engine.</param>
    /// <param name="context">UI context to marshal to; <see langword="null"/> applies operations synchronously (tests).</param>
    public ActivityProviderHost(ActivityManager manager, SynchronizationContext? context)
    {
        _manager = manager;
        _context = context;
    }

    public event EventHandler<ProviderStatus>? ProviderStateChanged;

    public IReadOnlyList<ProviderStatus> Providers =>
        _providers.Values.Select(e => e.Status).ToList();

    public ProviderStatus? Status(string providerId) => _providers.GetValueOrDefault(providerId)?.Status;

    /// <param name="provider">The provider.</param>
    /// <param name="enabled">Whether <see cref="StartAll"/> starts it (user setting; BLUEPRINT §10 Phase 4 "each provider can be independently disabled").</param>
    public void Register(IActivityProvider provider, bool enabled = true)
    {
        if (_providers.ContainsKey(provider.Id))
        {
            throw new InvalidOperationException($"Provider '{provider.Id}' is already registered.");
        }

        _providers[provider.Id] = new Entry(this, provider) { IsEnabled = enabled };
    }

    /// <summary>Starts every enabled provider.</summary>
    public void StartAll()
    {
        foreach (var entry in _providers.Values.ToList())
        {
            if (entry.IsEnabled)
            {
                Start(entry.Provider.Id);
            }
        }
    }

    /// <summary>Turns a provider on (starts or restarts it) or off (stops it and removes its activities).</summary>
    public void SetEnabled(string providerId, bool enabled)
    {
        var entry = _providers[providerId];
        entry.IsEnabled = enabled;
        if (enabled)
        {
            if (entry.State != ProviderState.Running)
            {
                Start(providerId);
            }
            else
            {
                ProviderStateChanged?.Invoke(this, entry.Status);
            }
        }
        else if (entry.State == ProviderState.Running)
        {
            Stop(providerId);
        }
        else
        {
            ProviderStateChanged?.Invoke(this, entry.Status);
        }
    }

    public void StopAll()
    {
        foreach (var entry in _providers.Values.ToList())
        {
            Stop(entry.Provider.Id);
        }
    }

    /// <summary>Starts (or restarts after a fault) one provider.</summary>
    public void Start(string providerId)
    {
        var entry = _providers[providerId];
        if (entry.State == ProviderState.Running)
        {
            return;
        }

        entry.Sink = new Sink(this, entry);
        entry.LastError = null;
        entry.State = ProviderState.Running;
        try
        {
            entry.Provider.Start(entry.Sink);
            Log.Information("Island provider {Provider} started", providerId);
            ProviderStateChanged?.Invoke(this, entry.Status);
        }
        catch (Exception ex)
        {
            Fault(entry, ex);
        }
    }

    /// <summary>Stops a provider (it stays enabled; <see cref="Start"/> or <see cref="StartAll"/> runs it again).</summary>
    public void Stop(string providerId)
    {
        var entry = _providers[providerId];
        if (entry.State != ProviderState.Running)
        {
            return;
        }

        entry.Sink?.Close();
        entry.State = ProviderState.Stopped;
        try
        {
            entry.Provider.Stop();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Island provider {Provider} threw while stopping", providerId);
        }

        _manager.EndAllFrom(providerId);
        ProviderStateChanged?.Invoke(this, entry.Status);
    }

    /// <summary>Routes a button/gesture to the provider that owns <paramref name="activity"/>.</summary>
    /// <returns><see langword="true"/> if the provider handled it.</returns>
    public bool DispatchAction(IslandActivity activity, string actionId)
    {
        if (!_providers.TryGetValue(activity.ProviderId, out var entry) || entry.State != ProviderState.Running)
        {
            return false;
        }

        try
        {
            return entry.Provider.OnAction(activity.Id, actionId);
        }
        catch (Exception ex)
        {
            Fault(entry, ex);
            return false;
        }
    }

    private void Fault(Entry entry, Exception error)
    {
        if (entry.State == ProviderState.Faulted)
        {
            return;
        }

        entry.Sink?.Close();
        entry.State = ProviderState.Faulted;
        entry.LastError = $"{error.GetType().Name}: {error.Message}";

        // Exception type and message only: providers must not put user content in exception messages.
        Log.Error("Island provider {Provider} failed and was disabled: {Error}", entry.Provider.Id, entry.LastError);
        try
        {
            entry.Provider.Stop();
        }
        catch (Exception stopError)
        {
            Log.Warning("Island provider {Provider} also failed to stop: {Error}", entry.Provider.Id, stopError.GetType().Name);
        }

        _manager.EndAllFrom(entry.Provider.Id);
        ProviderStateChanged?.Invoke(this, entry.Status);
    }

    private void MarkUnavailable(Entry entry, string reason)
    {
        if (entry.State != ProviderState.Running)
        {
            return;
        }

        entry.Sink?.Close();
        entry.State = ProviderState.Unavailable;
        entry.LastError = reason;
        Log.Information("Island provider {Provider} unavailable on this device: {Reason}", entry.Provider.Id, reason);
        try
        {
            entry.Provider.Stop();
        }
        catch (Exception stopError)
        {
            Log.Warning("Island provider {Provider} failed to stop: {Error}", entry.Provider.Id, stopError.GetType().Name);
        }

        _manager.EndAllFrom(entry.Provider.Id);
        ProviderStateChanged?.Invoke(this, entry.Status);
    }

    private void Apply(Entry entry, Operation op)
    {
        if (entry.State != ProviderState.Running)
        {
            return;
        }

        var id = entry.Provider.Id;
        var result = op switch
        {
            StartOp s => _manager.Start(id, s.Spec),
            UpdateOp u => _manager.Update(id, u.Update),
            EndOp e => _manager.End(id, e.Id, e.FinalText, e.DismissAfter),
            _ => ActivityResult.Invalid,
        };

        if (result is ActivityResult.IdConflict or ActivityResult.Invalid)
        {
            Log.Warning("Island provider {Provider}: {Operation} rejected ({Result})", id, op.GetType().Name, result);
        }
    }

    private abstract record Operation;

    private sealed record StartOp(ActivitySpec Spec) : Operation;

    private sealed record UpdateOp(ActivityUpdate Update) : Operation;

    private sealed record EndOp(string Id, string? FinalText, TimeSpan? DismissAfter) : Operation;

    private sealed class Entry(ActivityProviderHost host, IActivityProvider provider)
    {
        public IActivityProvider Provider { get; } = provider;

        public ProviderState State { get; set; } = ProviderState.Registered;

        public string? LastError { get; set; }

        public Sink? Sink { get; set; }

        public bool IsEnabled { get; set; } = true;

        public ProviderStatus Status => new(Provider.Id, Provider.DisplayName, State, LastError, IsEnabled);

        public ActivityProviderHost Host { get; } = host;
    }

    /// <summary>
    /// Per-provider sink. Queues operations and drains them on the engine context in one post per burst;
    /// an update directly following an update to the same activity is merged into it.
    /// </summary>
    private sealed class Sink(ActivityProviderHost host, Entry entry) : IActivitySink
    {
        private readonly object _gate = new();
        private readonly List<Operation> _pending = [];
        private bool _posted;
        private bool _closed;

        public void Start(ActivitySpec spec) => Enqueue(new StartOp(spec));

        public void Update(ActivityUpdate update) => Enqueue(new UpdateOp(update));

        public void End(string id, string? finalText = null, TimeSpan? dismissAfter = null) =>
            Enqueue(new EndOp(id, finalText, dismissAfter));

        public void ReportFailure(Exception error)
        {
            if (host._context is null)
            {
                host.Fault(entry, error);
            }
            else
            {
                host._context.Post(_ => host.Fault(entry, error), null);
            }
        }

        public void ReportUnavailable(string reason)
        {
            // A sink from an earlier run (closed) must not affect a restarted provider.
            if (host._context is null)
            {
                if (!IsClosed)
                {
                    host.MarkUnavailable(entry, reason);
                }
            }
            else
            {
                host._context.Post(_ =>
                {
                    if (!IsClosed)
                    {
                        host.MarkUnavailable(entry, reason);
                    }
                }, null);
            }
        }

        private bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _pending.Clear();
            }
        }

        private void Enqueue(Operation op)
        {
            bool post;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                if (op is UpdateOp next && _pending.Count > 0 && _pending[^1] is UpdateOp last && last.Update.Id == next.Update.Id)
                {
                    _pending[^1] = new UpdateOp(last.Update.MergeWith(next.Update));
                }
                else
                {
                    _pending.Add(op);
                }

                post = !_posted;
                _posted = true;
            }

            if (!post)
            {
                return;
            }

            if (host._context is null)
            {
                Drain();
            }
            else
            {
                host._context.Post(_ => Drain(), null);
            }
        }

        private void Drain()
        {
            Operation[] batch;
            lock (_gate)
            {
                batch = [.. _pending];
                _pending.Clear();
                _posted = false;
            }

            foreach (var op in batch)
            {
                host.Apply(entry, op);
            }
        }
    }
}
