using System.Diagnostics;
using IrisAura.Island.Activities;
using IrisAura.Island.Providers.Interop;
using Serilog;

namespace IrisAura.Island.Providers.Downloads;

/// <summary>
/// Downloads (BLUEPRINT §8.6.11, best-effort): a <c>FileSystemWatcher</c> on the Downloads folder tracks browser
/// temporary files, shows the file name and the bytes written so far (the total is unknown, so there is no
/// percentage), and "Done ✓" when the file gets its final name. Sizes are read once a second only while a download
/// is in progress. File names are shown but never logged. Clicking opens the Downloads folder.
/// </summary>
public sealed class DownloadsProvider : IActivityProvider
{
    public const string ProviderId = "downloads";
    private static readonly TimeSpan SizeInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string?> _folder;
    private readonly object _gate = new();
    private DownloadTracker _tracker = new();
    private IActivitySink? _sink;
    private FileSystemWatcher? _watcher;
    private Timer? _sizeTimer;
    private string? _path;

    /// <param name="folder">Folder to watch; default: the user's Downloads known folder.</param>
    public DownloadsProvider(Func<string?>? folder = null) => _folder = folder ?? ProviderNativeMethods.DownloadsFolder;

    public string Id => ProviderId;

    /// <summary>The user's Downloads known folder (it can be moved), or <see langword="null"/>.</summary>
    public static string? DefaultFolder() => ProviderNativeMethods.DownloadsFolder();

    public string DisplayName => "Downloads";

    public void Start(IActivitySink sink)
    {
        var path = _folder();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            sink.ReportUnavailable("Downloads folder not found");
            return;
        }

        lock (_gate)
        {
            _sink = sink;
            _path = path;
            _tracker = new DownloadTracker();
            _sizeTimer = new Timer(_ => PollSizes(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 16 * 1024,
            };
            _watcher.Created += (_, e) => Handle(t => t.OnCreated(e.FullPath, SizeOf(e.FullPath)));
            _watcher.Renamed += (_, e) => Handle(t => t.OnRenamed(e.OldFullPath, e.FullPath, SizeOf(e.FullPath)));
            _watcher.Deleted += (_, e) => Handle(t => t.OnDeleted(e.FullPath, SizeOf(DownloadTracker.FinalPath(e.FullPath)) > 0));
            _watcher.Error += (_, e) => Log.Warning("Downloads watcher error: {Error}", e.GetException().GetType().Name);
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _sink = null;
            _watcher?.Dispose();
            _watcher = null;
            _sizeTimer?.Dispose();
            _sizeTimer = null;
        }
    }

    public bool OnAction(string activityId, string actionId)
    {
        if (actionId != ActivityActions.Open || _path is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Information("Could not open the Downloads folder: {Error}", ex.GetType().Name);
            return false;
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void Handle(Func<DownloadTracker, DownloadEvent?> change)
    {
        lock (_gate)
        {
            if (_sink is null)
            {
                return;
            }

            Publish(change(_tracker));
            _sizeTimer?.Change(_tracker.Count > 0 ? SizeInterval : Timeout.InfiniteTimeSpan, _tracker.Count > 0 ? SizeInterval : Timeout.InfiniteTimeSpan);
        }
    }

    private void PollSizes()
    {
        lock (_gate)
        {
            if (_sink is null)
            {
                return;
            }

            foreach (var path in _tracker.TrackedPaths.ToList())
            {
                Publish(_tracker.OnSize(path, SizeOf(path)));
            }
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void Publish(DownloadEvent? e)
    {
        if (e is null || _sink is null)
        {
            return;
        }

        switch (e.Kind)
        {
            case DownloadEventKind.Started:
            case DownloadEventKind.Progress:
                _sink.Start(DownloadTracker.Activity(e));
                break;
            case DownloadEventKind.Completed:
                _sink.Start(DownloadTracker.Activity(e) with
                {
                    Compact = new CompactContent(LeadingGlyph: "Aura.Glyph.Download", LeadingText: e.FileName, TrailingText: "Done ✓", Tone: ActivityTone.Success),
                    Expanded = new ExpandedContent(e.FileName, "Download complete"),
                    AccessibleText = $"{e.FileName} downloaded",
                });
                _sink.End(e.Id, "Done ✓", TimeSpan.FromSeconds(3));
                break;
            case DownloadEventKind.Cancelled:
                _sink.End(e.Id);
                break;
        }
    }
}
