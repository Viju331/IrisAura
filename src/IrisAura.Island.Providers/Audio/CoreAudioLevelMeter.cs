using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace IrisAura.Island.Providers.Audio;

/// <summary>Audio peak level for the waveform.</summary>
public interface IAudioLevelSource
{
    /// <summary>
    /// Current linear peak (0–1) of the audio sessions owned by <paramref name="processName"/>; when no session
    /// matches (or the name is <see langword="null"/>), the default output device's peak.
    /// </summary>
    double GetPeak(string? processName);

    /// <summary>Whether the last <see cref="GetPeak"/> matched a per-app session (false = device fallback).</summary>
    bool LastMatchedSession { get; }
}

/// <summary>
/// CoreAudio peak meters via NAudio (BLUEPRINT §8.6.2 option A): the peak of the media app's own audio
/// session(s), matched by process name (browsers play audio from helper processes with the same name);
/// falls back to the whole output device (option B without FFT). The default device and session list are
/// re-read every 2 s, which also covers headphones being plugged in (§8.9 "audio device changes").
/// Call from one thread (the UI thread).
/// </summary>
public sealed class CoreAudioLevelMeter : IAudioLevelSource, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly Dictionary<uint, string> _processNames = [];
    private readonly List<(string Process, AudioSessionControl Session)> _sessions = [];
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string? _deviceId;
    private long _lastRefresh = long.MinValue;
    private bool _disposed;

    public bool LastMatchedSession { get; private set; }

    public double GetPeak(string? processName)
    {
        if (_disposed)
        {
            return 0;
        }

        try
        {
            RefreshIfDue();
            if (processName is not null)
            {
                var best = -1.0;
                foreach (var (process, session) in _sessions)
                {
                    if (string.Equals(process, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        best = Math.Max(best, session.AudioMeterInformation.MasterPeakValue);
                    }
                }

                if (best >= 0)
                {
                    LastMatchedSession = true;
                    return best;
                }
            }

            LastMatchedSession = false;
            return _device?.AudioMeterInformation.MasterPeakValue ?? 0;
        }
        catch (Exception)
        {
            // Device removed or session gone: rebuild on the next call.
            Reset();
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset();
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void RefreshIfDue()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastRefresh != long.MinValue && Stopwatch.GetElapsedTime(_lastRefresh, now) < RefreshInterval)
        {
            return;
        }

        _lastRefresh = now;
        _enumerator ??= new MMDeviceEnumerator();
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (device.ID != _deviceId)
        {
            ClearSessions();
            _device?.Dispose();
            _device = device;
            _deviceId = device.ID;
        }
        else
        {
            device.Dispose();
        }

        ClearSessions();
        var manager = _device!.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            _sessions.Add((ProcessName(session.GetProcessID), session));
        }
    }

    private string ProcessName(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty; // system sounds
        }

        if (_processNames.TryGetValue(pid, out var name))
        {
            return name;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            name = string.Empty;
        }

        if (_processNames.Count > 256)
        {
            _processNames.Clear();
        }

        _processNames[pid] = name;
        return name;
    }

    private void ClearSessions()
    {
        foreach (var (_, session) in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private void Reset()
    {
        ClearSessions();
        _device?.Dispose();
        _device = null;
        _deviceId = null;
        _lastRefresh = long.MinValue;
    }
}
