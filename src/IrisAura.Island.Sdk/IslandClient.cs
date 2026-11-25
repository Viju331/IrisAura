using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IrisAura.Contracts;

namespace IrisAura.Island.Sdk;

/// <summary>Connection options.</summary>
public sealed class IslandClientOptions
{
    /// <summary>App name shown on the Island and used (with your exe) to identify your activities.</summary>
    public string? AppName { get; set; }

    /// <summary>Server check before anything is sent; default <see cref="IslandServerVerification.SignedIsland"/>.</summary>
    public IslandServerVerification Verification { get; set; } = IslandServerVerification.SignedIsland;

    /// <summary>SHA-1 thumbprints of signing certificates to trust for <c>IrisAura.Island.exe</c> (for example the development certificate).</summary>
    public IList<string> TrustedThumbprints { get; } = new List<string>();

    /// <summary>Pipe name override (tests). Default: the current user's IrisAura Island pipe.</summary>
    public string? PipeName { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the Island to confirm each start/update/end.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>The Island refused a request (<see cref="Code"/> from <see cref="IslandSdkErrors"/>).</summary>
public sealed class IslandSdkException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A button press or click on one of your activities.</summary>
public sealed class IslandActionEventArgs(string activityId, string button) : EventArgs
{
    public string ActivityId { get; } = activityId;

    /// <summary>Your button id, or <c>open</c> when the user clicked an activity that has no open link.</summary>
    public string Button { get; } = button;
}

/// <summary>
/// Connection to IrisAura Island's Activity SDK pipe (BLUEPRINT §8.6.14). Thread-safe. Each start/update/end waits
/// for the Island's answer and throws <see cref="IslandSdkException"/> if it was refused.
/// </summary>
/// <example>
/// <code>
/// using var island = await IslandClient.ConnectAsync(new IslandClientOptions { AppName = "Builder" });
/// var act = await island.StartAsync(new IslandActivitySpec("build-42", "Deploying", progress: 0.1));
/// await act.UpdateAsync(progress: 0.5);
/// await act.EndAsync("Deployed ✓");
/// </code>
/// </example>
public sealed class IslandClient : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    private readonly NamedPipeClientStream _pipe;
    private readonly IslandClientOptions _options;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _request = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<bool> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<IslandSdkMessage>? _pending;
    private readonly List<IslandSdkMessage> _errors = [];

    private IslandClient(NamedPipeClientStream pipe, IslandClientOptions options)
    {
        _pipe = pipe;
        _options = options;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    /// <summary>A button press or click on one of your activities (raised on a background thread).</summary>
    public event EventHandler<IslandActionEventArgs>? ActionReceived;

    /// <summary>The connection closed.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Server version from the Island's greeting.</summary>
    public string? ServerVersion { get; private set; }

    public bool IsConnected => _pipe.IsConnected && !_closed.IsCancellationRequested;

    /// <summary>The current user's pipe name.</summary>
    public static string DefaultPipeName => PipeNames.IslandActivities(WindowsIdentity.GetCurrent().User!.Value);

    /// <summary>Connects and verifies the server (see <see cref="IslandClientOptions.Verification"/>).</summary>
    /// <exception cref="TimeoutException">IrisAura Island is not running (or the SDK is switched off).</exception>
    /// <exception cref="IslandServerVerificationException">The pipe server is not a trusted IrisAura Island.</exception>
    public static async Task<IslandClient> ConnectAsync(IslandClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new IslandClientOptions();
        var pipe = new NamedPipeClientStream(".", options.PipeName ?? DefaultPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            ServerVerifier.Verify(pipe.SafePipeHandle, options.Verification, options.TrustedThumbprints);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        var client = new IslandClient(pipe, options);
        _ = Task.Run(client.ReadLoopAsync);

        // The Island greets every client first; waiting for it confirms it speaks this protocol.
        var greeted = await Task.WhenAny(client._hello.Task, Task.Delay(options.ReplyTimeout, cancellationToken)).ConfigureAwait(false);
        if (greeted != client._hello.Task || !client._hello.Task.Result)
        {
            client.Dispose();
            throw new TimeoutException("IrisAura Island did not greet the client.");
        }

        return client;
    }

    /// <summary>Starts (or replaces) an activity.</summary>
    public async Task<IslandActivityHandle> StartAsync(IslandActivitySpec spec, CancellationToken cancellationToken = default)
    {
        var message = spec.ToMessage(_options.AppName);
        await SendConfirmedAsync(message, cancellationToken).ConfigureAwait(false);
        return new IslandActivityHandle(this, spec.Id);
    }

    /// <summary>Changes only the values given.</summary>
    public Task UpdateAsync(string id, IslandActivityUpdate update, CancellationToken cancellationToken = default) =>
        SendConfirmedAsync(update.ToMessage(id, _options.AppName), cancellationToken);

    /// <summary>Ends an activity, optionally showing <paramref name="finalText"/> for <paramref name="dismissAfter"/> first.</summary>
    public Task EndAsync(string id, string? finalText = null, TimeSpan? dismissAfter = null, CancellationToken cancellationToken = default) =>
        SendConfirmedAsync(new IslandSdkMessage
        {
            V = ProtocolVersions.IslandSdk,
            Op = IslandSdkOps.End,
            Id = id,
            App = _options.AppName,
            FinalText = finalText,
            DismissAfterSeconds = dismissAfter?.TotalSeconds ?? (finalText is null ? null : 3),
        }, cancellationToken);

    /// <summary>Ids of your live activities.</summary>
    public async Task<IReadOnlyList<string>> QueryAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(null, cancellationToken).ConfigureAwait(false);
        return reply.Ids ?? [];
    }

    /// <summary>Sends a raw protocol line (tests and tools). Does not wait for an answer.</summary>
    public Task SendRawAsync(string line, CancellationToken cancellationToken = default) =>
        WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);

    public void Dispose()
    {
        _closed.Cancel();
        _pipe.Dispose();
        _reader.Dispose();
        _request.Dispose();
    }

    private async Task SendConfirmedAsync(IslandSdkMessage message, CancellationToken cancellationToken)
    {
        if (message.Id is { } id && (id.Length == 0 || id.Length > IslandSdkLimits.MaxIdLength))
        {
            throw new ArgumentException("Activity ids are 1–64 characters of A–Z a–z 0–9 . _ -", nameof(message));
        }

        var reply = await RequestAsync(message, cancellationToken).ConfigureAwait(false);
        _ = reply;
    }

    /// <summary>
    /// Sends <paramref name="message"/> (if any) followed by <c>query</c>; the Island answers in order, so an error for
    /// the message arrives before the <c>activities</c> answer.
    /// </summary>
    private async Task<IslandSdkMessage> RequestAsync(IslandSdkMessage? message, CancellationToken cancellationToken)
    {
        await _request.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var done = new TaskCompletionSource<IslandSdkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _errors.Clear();
                _pending = done;
            }

            var bytes = new List<byte>();
            if (message is not null)
            {
                bytes.AddRange(JsonSerializer.SerializeToUtf8Bytes(message, Json));
                bytes.Add((byte)'\n');
            }

            bytes.AddRange(JsonSerializer.SerializeToUtf8Bytes(
                new IslandSdkMessage { V = ProtocolVersions.IslandSdk, Op = IslandSdkOps.Query, App = _options.AppName }, Json));
            bytes.Add((byte)'\n');
            if (bytes.Count > IslandSdkLimits.MaxMessageBytes * 2)
            {
                throw new ArgumentException("Message longer than 64 KB (icon too large?).", nameof(message));
            }

            await WriteAsync(bytes.ToArray(), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            timeout.CancelAfter(_options.ReplyTimeout);
            using (timeout.Token.Register(() => done.TrySetException(new TimeoutException("IrisAura Island did not answer."))))
            {
                var answer = await done.Task.ConfigureAwait(false);
                IslandSdkMessage? error;
                lock (_gate)
                {
                    error = _errors.FirstOrDefault();
                }

                if (error is not null)
                {
                    throw new IslandSdkException(error.Code ?? "error", error.Message ?? "IrisAura Island refused the request.");
                }

                return answer;
            }
        }
        finally
        {
            _request.Release();
        }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _pipe.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                IslandSdkMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IslandSdkMessage>(line, Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                switch (message?.Op)
                {
                    case IslandSdkOps.Hello:
                        ServerVersion = message.Server;
                        _hello.TrySetResult(true);
                        break;
                    case IslandSdkOps.Action when message.Id is not null && message.Button is not null:
                        ActionReceived?.Invoke(this, new IslandActionEventArgs(message.Id, message.Button));
                        break;
                    case IslandSdkOps.Error:
                        lock (_gate)
                        {
                            _errors.Add(message);
                        }

                        break;
                    case IslandSdkOps.Activities:
                        TaskCompletionSource<IslandSdkMessage>? pending;
                        lock (_gate)
                        {
                            pending = _pending;
                            _pending = null;
                        }

                        pending?.TrySetResult(message);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closed.
        }
        finally
        {
            _closed.Cancel();
            _hello.TrySetResult(false);
            lock (_gate)
            {
                _pending?.TrySetException(new IOException("IrisAura Island closed the connection."));
                _pending = null;
            }

            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A started activity.</summary>
public sealed class IslandActivityHandle
{
    private readonly IslandClient _client;

    internal IslandActivityHandle(IslandClient client, string id)
    {
        _client = client;
        Id = id;
    }

    public string Id { get; }

    public Task UpdateAsync(
        string? title = null,
        string? subtitle = null,
        double? progress = null,
        string? trailingText = null,
        CancellationToken cancellationToken = default) =>
        _client.UpdateAsync(Id, new IslandActivityUpdate { Title = title, Subtitle = subtitle, Progress = progress, TrailingText = trailingText }, cancellationToken);

    public Task UpdateAsync(IslandActivityUpdate update, CancellationToken cancellationToken = default) =>
        _client.UpdateAsync(Id, update, cancellationToken);

    public Task EndAsync(string? finalText = null, TimeSpan? dismissAfter = null, CancellationToken cancellationToken = default) =>
        _client.EndAsync(Id, finalText, dismissAfter, cancellationToken);
}
