using System.IO;
using System.Text.Json;
using Serilog;

namespace IrisAura.Island.Engine;

/// <summary>
/// The file tray (BLUEPRINT §8.6.12, §8.13A): shortcuts to files and folders the user dropped on the Island. Holds
/// paths only; never copies, opens or reads the files. Newest first, no duplicates, at most <see cref="MaxItems"/>.
/// Pure (the caller decides what exists), for tests.
/// </summary>
public sealed class FileShelf
{
    public const int MaxItems = 30;

    private readonly List<string> _items = [];

    public FileShelf(IEnumerable<string>? items = null)
    {
        if (items is not null)
        {
            Add(items.Reverse(), _ => true);
        }
    }

    public IReadOnlyList<string> Items => _items;

    /// <summary>Adds paths (newest first); already-present paths move to the front; the oldest drop off past the limit.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Add(IEnumerable<string> paths, Func<string, bool> exists)
    {
        var changed = false;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || !exists(raw))
            {
                continue;
            }

            var path = raw.TrimEnd('\\', '/');
            if (path.Length == 0)
            {
                continue;
            }

            if (path.EndsWith(':'))
            {
                path += "\\"; // keep drive roots valid ("C:" means "current directory on C:")
            }

            var existing = _items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (existing == 0)
            {
                continue;
            }

            if (existing > 0)
            {
                _items.RemoveAt(existing);
            }

            _items.Insert(0, path);
            changed = true;
        }

        if (_items.Count > MaxItems)
        {
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        }

        return changed;
    }

    public bool Remove(string path) =>
        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0;

    public bool Clear()
    {
        var had = _items.Count > 0;
        _items.Clear();
        return had;
    }

    /// <summary>Drops shortcuts whose file or folder is gone (moved or deleted since).</summary>
    public bool PruneMissing(Func<string, bool> exists) => _items.RemoveAll(p => !exists(p)) > 0;

    /// <summary>Whether a path is an existing file or folder.</summary>
    public static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Persists the file tray's paths in <c>%LocalAppData%\IrisAura\island-shelf.json</c> (BLUEPRINT §8.6.12: "store the
/// paths in memory and in %LocalAppData%"). Atomic writes; a missing or corrupt file means an empty tray.
/// </summary>
public sealed class FileShelfStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisAura", "island-shelf.json");

    public string FilePath => path;

    public IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("File tray list unreadable ({Error}); starting empty", ex.GetType().Name);
            return [];
        }
    }

    public void Save(IReadOnlyList<string> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("File tray list could not be saved ({Error})", ex.GetType().Name);
        }
    }
}
