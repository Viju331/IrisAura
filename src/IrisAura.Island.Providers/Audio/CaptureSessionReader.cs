using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IrisAura.Island.Providers.Audio;

/// <summary>
/// Processes currently recording from a microphone, from the documented CoreAudio session API: every active capture
/// endpoint's <c>IAudioSessionManager2</c> sessions in <c>AudioSessionStateActive</c>. Complements the consent store
/// (which some Windows builds no longer update; see KNOWN_ISSUES). Shared-mode capture only; exclusive-mode capture
/// has no session.
/// <para>Event-driven: it registers for session-created and per-session state/disconnect notifications and calls
/// <c>changed</c> when one arrives, so nothing runs while nothing changes (a 1 s poll measured ~0.15–0.2 % idle CPU).
/// Create, read and dispose on one MTA thread; the callback only signals.</para>
/// </summary>
public sealed class CaptureSessionReader : IDisposable
{
    private readonly Dictionary<uint, string> _names = [];
    private readonly List<MMDevice> _devices = [];
    private readonly List<AudioSessionControl> _sessions = [];
    private readonly SessionEvents _events;
    private readonly Action _changed;
    private readonly AudioSessionManager.SessionCreatedDelegate _onSessionCreated;
    private MMDeviceEnumerator? _enumerator;
    private volatile bool _rebuild = true;

    /// <param name="changed">Called on a CoreAudio thread when a capture session appears, changes state or goes away.</param>
    public CaptureSessionReader(Action changed)
    {
        _changed = changed;
        _events = new SessionEvents(this);
        _onSessionCreated = (_, _) =>
        {
            _rebuild = true; // pick up and subscribe to the new session on the reader thread
            _changed();
        };
    }

    /// <summary>Re-read the device and session lists on the next call (new microphones, audio service restart).</summary>
    public void RequestRebuild() => _rebuild = true;

    /// <summary>Process names (without .exe) with an active capture session, sorted, distinct.</summary>
    public IReadOnlyList<string> ActiveCaptureProcesses()
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_rebuild)
            {
                Rebuild();
            }

            foreach (var session in _sessions)
            {
                if (session.State == AudioSessionState.AudioSessionStateActive && NameOf(session.GetProcessID) is { Length: > 0 } name)
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // Device removed / audio service restarting: rebuild next time.
            Release();
            _rebuild = true;
        }

        return [.. result];
    }

    public void Dispose()
    {
        Release();
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void Rebuild()
    {
        Release();
        _rebuild = false;
        _enumerator ??= new MMDeviceEnumerator();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            _devices.Add(device);
            var manager = device.AudioSessionManager;
            manager.OnSessionCreated += _onSessionCreated;
            manager.RefreshSessions(); // also required before session-created notifications are delivered
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                session.RegisterEventClient(_events);
                _sessions.Add(session);
            }
        }
    }

    private void Release()
    {
        foreach (var session in _sessions)
        {
            try
            {
                session.UnRegisterEventClient(_events);
            }
            catch (Exception)
            {
                // Session already gone.
            }

            session.Dispose();
        }

        _sessions.Clear();
        foreach (var device in _devices)
        {
            try
            {
                device.AudioSessionManager.OnSessionCreated -= _onSessionCreated;
            }
            catch (Exception)
            {
                // Device removed.
            }

            device.Dispose();
        }

        _devices.Clear();
    }

    private string? NameOf(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        if (_names.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            name = string.Empty;
        }

        if (_names.Count > 256)
        {
            _names.Clear();
        }

        _names[pid] = name;
        return name;
    }

    /// <summary>Per-session notifications; only signals (CoreAudio callbacks must not block).</summary>
    private sealed class SessionEvents(CaptureSessionReader owner) : IAudioSessionEventsHandler
    {
        public void OnStateChanged(AudioSessionState state) => owner._changed();

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            owner._rebuild = true;
            owner._changed();
        }

        public void OnVolumeChanged(float volume, bool isMuted)
        {
        }

        public void OnDisplayNameChanged(string displayName)
        {
        }

        public void OnIconPathChanged(string iconPath)
        {
        }

        public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }
    }
}
