using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IrisAura.Contracts;
using IrisAura.Island.Providers.Interop;
using Serilog;

namespace IrisAura.Island.Providers.Sdk;

/// <summary>Another process already owns the SDK pipe name (possible squatting, or a second Island).</summary>
public sealed class SdkPipeTakenException(string message, Exception inner) : Exception(message, inner);

/// <summary>The SDK's JSON settings (camelCase, nulls omitted, depth-limited).</summary>
public static class SdkJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };
}

/// <summary>
/// Per-user named-pipe server for the Activity SDK (BLUEPRINT §8.6.14, THREAT_MODEL T-01/T-02):
/// <list type="bullet">
/// <item>DACL: the current user only (full control); the NETWORK group is denied; remote clients are rejected.</item>
/// <item>The first instance is created with <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>: if another process already owns the
///   name, start fails with <see cref="SdkPipeTakenException"/> instead of sharing it.</item>
/// <item>At most <see cref="MaxConnections"/> clients at once.</item>
/// </list>
/// </summary>
public sealed class SdkPipeServer(string pipeName, Func<SdkConnection, CancellationToken, Task> onConnected) : IDisposable
{
    public const int MaxConnections = 16;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public string PipeName => pipeName;

    /// <summary>Creates the first pipe instance (synchronously, so a squatted name is reported at once) and starts accepting.</summary>
    public void Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = Create(firstInstance: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SdkPipeTakenException("The IrisAura SDK pipe name is already in use by another process.", ex);
        }

        _loop = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelled.
        }

        _stop.Dispose();
    }

    /// <summary>Owner = current user; the user has full control; NETWORK is denied (no inherited or Everyone access).</summary>
    public static PipeSecurity CreateSecurity()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private NamedPipeServerStream Create(bool firstInstance) => NamedPipeServerStreamAcl.Create(
        pipeName,
        PipeDirection.InOut,
        MaxConnections + 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        inBufferSize: 16 * 1024,
        outBufferSize: 16 * 1024,
        CreateSecurity());

    private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken ct)
    {
        NamedPipeServerStream? listening = first;
        try
        {
            while (listening is not null && !ct.IsCancellationRequested)
            {
                await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = listening;
                _ = Task.Run(() => RunAsync(connected, ct), CancellationToken.None);

                listening = null;
                while (listening is null && !ct.IsCancellationRequested)
                {
                    try
                    {
                        listening = Create(firstInstance: false);
                    }
                    catch (IOException)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false); // all instances busy
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            Log.Warning("SDK pipe stopped accepting: {Error}", ex.GetType().Name);
        }
        finally
        {
            listening?.Dispose();
        }
    }

    private async Task RunAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var connection = new SdkConnection(pipe);
        try
        {
            await onConnected(connection, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client went away.
        }
        catch (Exception ex)
        {
            Log.Warning("SDK connection failed: {Error}", ex.GetType().Name);
        }
    }
}

/// <summary>One connected client: identity from the OS, line reading with the 64 KB limit, serialised writes.</summary>
public sealed class SdkConnection(NamedPipeServerStream pipe) : IAsyncDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly byte[] _buffer = new byte[8 * 1024];
    private readonly List<byte> _line = [];
    private int _start;
    private int _end;
    private volatile bool _closed;

    /// <summary>Set after <see cref="Identify"/>.</summary>
    public uint ProcessId { get; private set; }

    public string? ExePath { get; private set; }

    /// <summary>The last app name this connection declared (scripts send it with every message).</summary>
    public string? LastApp { get; set; }

    public bool IsClosed => _closed;

    /// <summary>
    /// Reads the client's process id, session and image path from the OS. Fails for a client in another session or a
    /// process whose image cannot be read.
    /// </summary>
    public bool Identify()
    {
        if (!ProviderNativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)
            || !ProviderNativeMethods.ProcessIdToSessionId(pid, out var session)
            || !ProviderNativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession)
            || session != ownSession
            || ProviderNativeMethods.ProcessImagePath(pid) is not { } path)
        {
            return false;
        }

        ProcessId = pid;
        ExePath = path;
        return true;
    }

    /// <summary>Next line (without the newline), or <see langword="null"/> at end of stream.</summary>
    /// <exception cref="InvalidDataException">The line is longer than <see cref="IslandSdkLimits.MaxMessageBytes"/>.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _line.Clear();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await pipe.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    return _line.Count > 0 ? Decode() : null;
                }
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var take = (newline < 0 ? _end : newline) - _start;
            if (_line.Count + take > IslandSdkLimits.MaxMessageBytes)
            {
                throw new InvalidDataException("Message longer than 64 KB.");
            }

            _line.AddRange(new ArraySegment<byte>(_buffer, _start, take));
            if (newline < 0)
            {
                _start = _end;
                continue;
            }

            _start = newline + 1;
            return Decode();
        }
    }

    public async Task SendAsync(IslandSdkMessage message)
    {
        if (_closed)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SdkJson.Options);
        using var timeout = new CancellationTokenSource(WriteTimeout);
        await _write.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _closed = true; // a client that does not read is dropped
        }
        finally
        {
            _write.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        try
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
        catch (Exception)
        {
            // Already broken.
        }

        pipe.Dispose();
        _write.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Decode()
    {
        var count = _line.Count > 0 && _line[^1] == '\r' ? _line.Count - 1 : _line.Count;
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(_line.ToArray(), 0, count);
    }
}
