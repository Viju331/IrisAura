using IrisAura.Island.Providers.Media;

namespace IrisAura.Island.Engine;

/// <summary>
/// The app whose window is in front (owner request, 2026-11-25): while an activity's own app is in front the
/// Island does not show that activity; when the app is minimised or another app comes to the front, it
/// appears. Like a phone hiding the live activity of the app you are using.
/// </summary>
/// <param name="ProcessName">Process name without ".exe" (e.g. "chrome").</param>
/// <param name="AppUserModelId">AUMID for packaged apps, otherwise <see langword="null"/>.</param>
public sealed record ForegroundApp(string ProcessName, string? AppUserModelId)
{
    /// <summary>True when an activity from <paramref name="sourceAppId"/> belongs to this app.</summary>
    public bool Matches(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        if (AppUserModelId is not null && string.Equals(AppUserModelId, sourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var process = MediaAppIdentity.ProcessNameFor(sourceAppId);
        return process is not null && string.Equals(process, ProcessName, StringComparison.OrdinalIgnoreCase);
    }
}
