using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Downloads;

/// <summary>What happened to a tracked download.</summary>
public enum DownloadEventKind
{
    Started,
    Progress,
    Completed,
    Cancelled,
}

/// <summary>A change to show.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Id">Activity id ("download:1").</param>
/// <param name="FileName">Final file name (temporary extension removed); shown, never logged.</param>
/// <param name="Bytes">Bytes written so far.</param>
public sealed record DownloadEvent(DownloadEventKind Kind, string Id, string FileName, long Bytes);

/// <summary>
/// Tracks browser temporary download files in one folder (BLUEPRINT §8.6.11): <c>.crdownload</c> (Chrome, Edge,
/// Brave…), <c>.part</c> (Firefox), <c>.download</c>, <c>.partial</c>. Pure; the provider feeds it file-system events.
/// <list type="bullet">
/// <item>Temp file appears → started. Temp renamed to another temp name (Chrome's "Unconfirmed 123.crdownload" →
///   "file.pdf.crdownload") → same download, new name.</item>
/// <item>Temp renamed to the final name → completed. Temp deleted while the final file exists (Firefox) → completed;
///   otherwise cancelled.</item>
/// <item>The total size is unknown, so progress is bytes written only (no percentage).</item>
/// </list>
/// </summary>
public sealed class DownloadTracker
{
    public const int MaxTracked = 5;
    private static readonly string[] TempExtensions = [".crdownload", ".part", ".download", ".partial"];

    private readonly Dictionary<string, Entry> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private int _next;

    public IReadOnlyCollection<string> TrackedPaths => _tracked.Keys;

    public int Count => _tracked.Count;

    public static bool IsTemporary(string path) =>
        TempExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Path without the temporary extension.</summary>
    public static string FinalPath(string tempPath)
    {
        var ext = TempExtensions.FirstOrDefault(e => tempPath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return ext is null ? tempPath : tempPath[..^ext.Length];
    }

    /// <summary>Name to show: the final file name; Chrome's placeholder "Unconfirmed 123456" shows as "Download".</summary>
    public static string DisplayName(string tempPath)
    {
        var name = Path.GetFileName(FinalPath(tempPath));
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ? "Download" : name;
    }

    public DownloadEvent? OnCreated(string path, long bytes = 0)
    {
        if (!IsTemporary(path) || _tracked.ContainsKey(path) || _tracked.Count >= MaxTracked)
        {
            return null;
        }

        var entry = new Entry($"download:{++_next}", DisplayName(path), bytes);
        _tracked[path] = entry;
        return new DownloadEvent(DownloadEventKind.Started, entry.Id, entry.Name, bytes);
    }

    public DownloadEvent? OnRenamed(string oldPath, string newPath, long bytes = 0)
    {
        if (!_tracked.Remove(oldPath, out var entry))
        {
            return OnCreated(newPath, bytes);
        }

        if (IsTemporary(newPath))
        {
            entry = entry with { Name = DisplayName(newPath) };
            _tracked[newPath] = entry;
            return new DownloadEvent(DownloadEventKind.Progress, entry.Id, entry.Name, Math.Max(entry.Bytes, bytes));
        }

        return new DownloadEvent(DownloadEventKind.Completed, entry.Id, Path.GetFileName(newPath), Math.Max(entry.Bytes, bytes));
    }

    /// <param name="path">Deleted file.</param>
    /// <param name="finalFileExists">Whether the final file (path without the temp extension) exists with content.</param>
    public DownloadEvent? OnDeleted(string path, bool finalFileExists)
    {
        if (!_tracked.Remove(path, out var entry))
        {
            return null;
        }

        return finalFileExists
            ? new DownloadEvent(DownloadEventKind.Completed, entry.Id, Path.GetFileName(FinalPath(path)), entry.Bytes)
            : new DownloadEvent(DownloadEventKind.Cancelled, entry.Id, entry.Name, entry.Bytes);
    }

    public DownloadEvent? OnSize(string path, long bytes)
    {
        if (!_tracked.TryGetValue(path, out var entry) || entry.Bytes == bytes)
        {
            return null;
        }

        _tracked[path] = entry with { Bytes = bytes };
        return new DownloadEvent(DownloadEventKind.Progress, entry.Id, entry.Name, bytes);
    }

    /// <summary>Activity for a started/progressing download.</summary>
    public static ActivitySpec Activity(DownloadEvent e)
    {
        var size = FormatBytes(e.Bytes);
        return new ActivitySpec(
            e.Id,
            ActivityKind.Download,
            new CompactContent(LeadingGlyph: "Aura.Glyph.Download", LeadingText: e.FileName, TrailingText: size),
            new ExpandedContent(e.FileName, $"{size} downloaded · total size unknown (best-effort)"),
            AccessibleText: $"Downloading {e.FileName}, {size} so far");
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    private sealed record Entry(string Id, string Name, long Bytes);
}
