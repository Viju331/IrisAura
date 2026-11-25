using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IrisAura.Island.Providers.Audio;

/// <summary>Whether an app is playing audio (call-detection corroboration).</summary>
public interface IAudioSessionActivity
{
    /// <summary>Whether any process with one of these names has an active output session on the default device.</summary>
    bool IsPlaying(IReadOnlyList<string> processNames);
}

/// <summary>
/// CoreAudio session state via NAudio: an output session in <c>AudioSessionStateActive</c> owned by the app.
/// Reads the session list on each call (only while a call app holds the microphone, at most once a second).
/// Thread-safe: each call uses its own COM objects.
/// </summary>
public sealed class AudioSessionActivity : IAudioSessionActivity
{
    public bool IsPlaying(IReadOnlyList<string> processNames)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Communications))
            {
                return false;
            }

            // Calls usually play on the communications device; check it and the multimedia default.
            foreach (var role in new[] { Role.Communications, Role.Multimedia })
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateActive && Matches(session.GetProcessID, processNames))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Device removed or audio service restarting: no corroboration.
        }

        return false;
    }

    private static bool Matches(uint pid, IReadOnlyList<string> processNames)
    {
        if (pid == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return processNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
