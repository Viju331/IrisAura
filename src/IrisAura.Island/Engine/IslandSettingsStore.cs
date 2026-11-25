using System.IO;
using System.Text.Json;
using Serilog;

namespace IrisAura.Island.Engine;

/// <summary>
/// User settings for the Island (BLUEPRINT §8.7). Phase 4 stores only which providers are switched on;
/// the settings window (later phase) adds the rest. Never stores activity or notification content.
/// </summary>
public sealed record IslandSettings
{
    /// <summary>Provider id → enabled. Providers not listed use their default.</summary>
    public Dictionary<string, bool> Providers { get; init; } = new(StringComparer.Ordinal);

    /// <summary>SDK apps the user blocked (exe path + app name keys; §8.7 "SDK: block an app").</summary>
    public List<string> SdkBlockedApps { get; init; } = [];

    /// <summary>Camera mirror in the panel (§8.13A): off until the user turns it on.</summary>
    public bool CameraMirror { get; init; }

    public bool IsEnabled(string providerId, bool defaultValue) =>
        Providers.TryGetValue(providerId, out var enabled) ? enabled : defaultValue;

    public IslandSettings WithProvider(string providerId, bool enabled)
    {
        var providers = new Dictionary<string, bool>(Providers, StringComparer.Ordinal) { [providerId] = enabled };
        return this with { Providers = providers };
    }
}

/// <summary>
/// Persists <see cref="IslandSettings"/> as JSON in <c>%LocalAppData%\IrisAura\island-settings.json</c>.
/// Atomic writes; a missing or corrupt file yields defaults.
/// </summary>
public sealed class IslandSettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisAura", "island-settings.json");

    public string FilePath => path;

    public IslandSettings Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new IslandSettings();
            }

            var loaded = JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(path), JsonOptions);
            return loaded is null
                ? new IslandSettings()
                : loaded with
                {
                    Providers = new Dictionary<string, bool>(loaded.Providers ?? [], StringComparer.Ordinal),
                    SdkBlockedApps = loaded.SdkBlockedApps ?? [],
                };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("Island settings file unreadable ({Error}); using defaults", ex.GetType().Name);
            return new IslandSettings();
        }
    }

    public void Save(IslandSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Island settings could not be saved ({Error})", ex.GetType().Name);
        }
    }
}
