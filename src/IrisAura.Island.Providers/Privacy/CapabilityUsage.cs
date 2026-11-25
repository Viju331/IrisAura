namespace IrisAura.Island.Providers.Privacy;

/// <summary>Device whose use Windows records in the capability consent store.</summary>
public enum Capability
{
    Microphone,
    Camera,
}

/// <summary>An app currently using the microphone or camera.</summary>
/// <param name="Key">Consent-store subkey name (package family name, or the exe path with '#' separators).</param>
/// <param name="DisplayName">Short name for the menu ("ms-teams", "WindowsCamera").</param>
/// <param name="ProcessName">Exe name without extension (unpackaged apps), or <see langword="null"/>.</param>
/// <param name="PackageFamilyName">Package family name (packaged apps), or <see langword="null"/>.</param>
public sealed record CapabilityUser(string Key, string DisplayName, string? ProcessName, string? PackageFamilyName);

/// <summary>Who is using the microphone and camera right now.</summary>
public sealed record CapabilitySnapshot(IReadOnlyList<CapabilityUser> Microphone, IReadOnlyList<CapabilityUser> Camera)
{
    public static CapabilitySnapshot Empty { get; } = new([], []);

    public IReadOnlyList<CapabilityUser> For(Capability capability) =>
        capability == Capability.Microphone ? Microphone : Camera;

    /// <summary>Same users (by key) for both devices.</summary>
    public bool SameAs(CapabilitySnapshot other) =>
        Microphone.Select(u => u.Key).SequenceEqual(other.Microphone.Select(u => u.Key), StringComparer.OrdinalIgnoreCase)
        && Camera.Select(u => u.Key).SequenceEqual(other.Camera.Select(u => u.Key), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the consent store layout (BLUEPRINT §8.6.7): an app is using the device while its subkey has
/// <c>LastUsedTimeStart &gt; 0</c> and <c>LastUsedTimeStop == 0</c>. Pure helpers, for tests.
/// </summary>
public static class CapabilityUsage
{
    public static bool IsInUse(long lastUsedTimeStart, long lastUsedTimeStop) => lastUsedTimeStart > 0 && lastUsedTimeStop == 0;

    /// <summary>A user from a consent-store subkey name.</summary>
    /// <param name="key">Subkey name.</param>
    /// <param name="nonPackaged">Whether it is under <c>NonPackaged</c> (exe path with '#' instead of '\').</param>
    public static CapabilityUser UserFromKey(string key, bool nonPackaged)
    {
        if (nonPackaged)
        {
            var path = key.Replace('#', '\\');
            var file = path[(path.LastIndexOf('\\') + 1)..];
            var process = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
            return new CapabilityUser(key, process, process, null);
        }

        // "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera"; "MSTeams_8wekyb3d8bbwe" → "MSTeams".
        var name = key;
        var underscore = name.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        return new CapabilityUser(key, name, null, key);
    }

    /// <summary>
    /// Microphone users from the consent store plus processes with an active CoreAudio capture session (the
    /// documented source). A process already listed by the consent store is not added twice.
    /// </summary>
    public static IReadOnlyList<CapabilityUser> MergeCapture(IReadOnlyList<CapabilityUser> consentStore, IReadOnlyList<string> captureProcesses)
    {
        var merged = consentStore.ToList();
        foreach (var process in captureProcesses)
        {
            if (!merged.Any(u => string.Equals(u.ProcessName, process, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(new CapabilityUser("capture:" + process, process, process, null));
            }
        }

        merged.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return merged;
    }
}
