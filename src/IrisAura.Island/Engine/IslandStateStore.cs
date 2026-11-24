using System.IO;
using System.Text.Json;
using Serilog;

namespace IrisAura.Island.Engine;

/// <summary>Island state that survives restarts (BLUEPRINT §10 Phase 2 "lifecycle persistence where appropriate").</summary>
public sealed record IslandState
{
    /// <summary>User's quiet mode (suppresses notification banners only).</summary>
    public bool QuietMode { get; init; }

    /// <summary>"Hide for 1 hour": hidden until this time (UTC), or <see langword="null"/>.</summary>
    public DateTimeOffset? HiddenUntil { get; init; }
}

/// <summary>
/// Persists <see cref="IslandState"/> as JSON in <c>%LocalAppData%\IrisAura\island-state.json</c>.
/// Writes are atomic (temp file + replace). A missing or corrupt file yields defaults.
/// Never stores activity or notification content.
/// </summary>
public sealed class IslandStateStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisAura", "island-state.json");

    public string FilePath => path;

    public IslandState Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new IslandState();
            }

            return JsonSerializer.Deserialize<IslandState>(File.ReadAllText(path), JsonOptions) ?? new IslandState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("Island state file unreadable ({Error}); using defaults", ex.GetType().Name);
            return new IslandState();
        }
    }

    public void Save(IslandState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Island state could not be saved ({Error})", ex.GetType().Name);
        }
    }
}
